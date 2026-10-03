using System.Globalization;

using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.UserCaches;

/// <summary>One package manager A17 trims: its own safe command and the cache that command shrinks (its default place —
/// the tool runs with a clean environment, so no variable moves it).</summary>
/// <param name="Tool">The name a person reads.</param>
/// <param name="Command">The user-scoped template (the tool's own command, never a delete of ours).</param>
/// <param name="Cache">The cache folder under the target user's home.</param>
public sealed record CacheTool(string Tool, CommandTemplate Command, IReadOnlyList<string> Cache);

/// <summary>
/// A17 (plan §5): the package managers' OWN cache trims, run AS THE TARGET USER through <c>runuser</c> (plan §15c #2) —
/// <c>pnpm store prune</c>, <c>uv cache prune</c>, <c>pip cache purge</c> (or <c>pip3</c>). A tool that is not installed in
/// the user's bin folders is skipped with the reason; none installed is a skip of the action, never an error. Off by
/// default (plan §5: button, or opt-in).
/// </summary>
/// <remarks>
/// <para><b>Not run, by decision (E3.S2):</b> <c>cargo sweep --time 30</c> cleans the <c>target/</c> folders of the projects
/// it is pointed at — and the projects live under <c>~/git</c>, under which nothing is ever deleted (plan §5 <i>Never</i>);
/// pointed anywhere else it has nothing to do. Gradle has no cache command: it prunes its own caches on its own retention
/// (unused entries and version caches after 30 days, Gradle ≥ 4.10). Both are named in the preview.</para>
/// <para><b>Freed bytes are measured</b>: each tool's cache walked right before and right after its command. Trigger (the
/// timer, when opted in): any one cache above <see cref="TriggerGib"/> GiB — npm's default (<c>npm.maxCacheGb</c>), because
/// no measurement of these caches exists on this machine yet.</para>
/// </remarks>
public sealed class ToolCacheTrims : ICleanupAction
{
    public const int TriggerGib = 5;

    public const string NotRun = "not run: cargo sweep (it deletes projects' target/ folders, and projects live under ~/git, under which nothing is ever deleted); Gradle (it prunes its own caches; there is no command)";

    private const long Gib = 1L << 30;

    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<CacheTool> Tools { get; } =
    [
        new("pnpm", User("pnpm-store-prune", "pnpm", "store", "prune"), [".local", "share", "pnpm", "store"]),
        new("uv", User("uv-cache-prune", "uv", "cache", "prune"), [".cache", "uv"]),
        new("pip", User("pip-cache-purge", "pip", "cache", "purge"), [".cache", "pip"]),
        new("pip3", User("pip3-cache-purge", "pip3", "cache", "purge"), [".cache", "pip"]),
    ];

    public ActionId Id { get; } = ActionId.Find("A17")!;

    public string Summary => "the package managers' own cache trims as the target user: pnpm store prune, uv cache prune, pip cache purge";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [.. Tools.Select(t => t.Command)];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var what = $"the caches of pnpm, uv and pip, trimmed by their own commands as {CacheFolders.UserName(context)}; {NotRun}";
        if (CacheFolders.Home(context).Length == 0)
        {
            return Task.FromResult(ActionPreview.Unavailable(what, context.TargetUser.Refusal.Length > 0 ? context.TargetUser.Refusal : "the caches are the WSL distro's"));
        }

        var installed = Installed(commands);
        var items = installed.Select(t => Item(context, t, cancellationToken)).ToList();
        var facts = items.ToDictionary(i => i.Name + "Bytes", i => i.Bytes ?? 0, StringComparer.Ordinal);
        var preview = ActionPreview.Of(what, items.Count, items.Sum(i => i.Bytes ?? 0), "each tool's cache folder, walked now", facts, string.Empty, items);
        return Task.FromResult(items.Count > 0 ? preview : preview with { Skip = $"none of pnpm, uv, pip is installed for {CacheFolders.UserName(context)}" });
    }

    /// <summary>Plan §5 A17, "per tool threshold": any one cache above <see cref="TriggerGib"/> GiB.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var largest = preview.Items.MaxBy(i => i.Bytes ?? 0);
        return new TriggerDecision(
            (largest?.Bytes ?? 0) > TriggerGib * Gib,
            string.Create(CultureInfo.InvariantCulture, $"the largest cache is {largest?.Name ?? "none"} with {(largest?.Bytes ?? 0) / (double)Gib:0.00} GiB; the trigger is above {TriggerGib} GiB"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var removed = new List<ActionItem>();
        var failures = new List<string>();
        var freed = 0L;
        var unknown = false;
        foreach (var tool in Installed(commands))
        {
            var cache = CacheFolders.UnderHome(context, [.. tool.Cache]);
            var before = CacheFolders.Measure(context.Files, cache, cancellationToken);
            var outcome = await commands.RunAsync(tool.Command, [], cancellationToken).ConfigureAwait(false);
            var after = CacheFolders.Measure(context.Files, cache, cancellationToken);
            var gone = CacheFolders.Freed(before, after);
            freed += gone ?? 0;
            unknown |= gone is null;
            removed.Add(new ActionItem("cache", tool.Tool, gone, gone is null ? "freed unknown: a walk of its cache was cut or unreadable" : cache));
            if (CommandFailures.Of(tool.Command.Shape, outcome) is { Length: > 0 } failed)
            {
                failures.Add(failed);
            }
        }

        return new ActionRun(removed.Count, unknown && freed == 0 ? null : freed, "each tool's cache folder walked right before and right after its own command", null, null, removed, commands.Ran, string.Join("; ", failures))
        {
            Notes = [NotRun],
        };
    }

    /// <summary>The tools found in the target user's bin folders — <c>pip3</c> only when there is no <c>pip</c> (both trim
    /// the same cache).</summary>
    private static IReadOnlyList<CacheTool> Installed(ActionCommands commands)
    {
        var found = Tools.Where(t => commands.Locate(t.Command) is ResolvedExecutable.Found).ToList();
        return [.. found.Where(t => t.Tool != "pip3" || found.All(f => f.Tool != "pip"))];
    }

    private static ActionItem Item(ActionContext context, CacheTool tool, CancellationToken cancellationToken)
    {
        var cache = CacheFolders.Measure(context.Files, CacheFolders.UnderHome(context, [.. tool.Cache]), cancellationToken);
        return new ActionItem("cache", tool.Tool, cache.CompleteBytes, cache.Complete ? cache.Path : $"{cache.Path} (walk cut or unreadable: size unknown)");
    }

    private static CommandTemplate User(string name, string executable, params string[] words) =>
        new(name, CommandScope.User, executable, [.. words.Select(w => new ArgPart.Literal(w))], Ceiling, CommandRequest.DefaultOutputCapChars);
}
