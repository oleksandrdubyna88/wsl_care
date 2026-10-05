using System.Globalization;

using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.UserCaches;

/// <summary>
/// A8 (plan §5, §15c #2/#3): <c>npm cache clean --force</c>, run AS THE TARGET USER — <c>runuser -u &lt;user&gt; --
/// &lt;npm's full path&gt; cache clean --force</c>, <c>npm</c> resolved in that user's fixed bin folders (nvm's default
/// node first), a clean environment, never <c>bash -ic</c>. An <c>npm</c> that is not installed is a SKIP with the
/// reason, never an error.
/// </summary>
/// <remarks>
/// <para><b>Preview</b> = the A8 row of <c>preview --all</c>: the size of <c>~/.npm</c> the newest full run measured (a
/// folder walk the daily run takes, plan §4.4), with its age — the same builder, so the two cannot differ.</para>
/// <para><b>Freed bytes are measured</b> (plan §5): <c>~/.npm</c> walked right before and right after, the difference —
/// unknown (never estimated) when either walk was cut by its ceiling.</para>
/// </remarks>
public sealed class NpmCacheClean : ICleanupAction
{
    /// <summary>The cache, cleaned by npm itself (a user-scoped template: it only ever runs through <c>runuser</c>).</summary>
    public static readonly CommandTemplate Clean = new(
        "npm-cache-clean",
        CommandScope.User,
        "npm",
        [new ArgPart.Literal("cache"), new ArgPart.Literal("clean"), new ArgPart.Literal("--force")],
        TimeSpan.FromMinutes(10),
        CommandRequest.DefaultOutputCapChars);

    /// <summary>Where npm keeps its cache as the user's own configuration says (review S4) — asked, never assumed.</summary>
    public static readonly CommandTemplate WhereCache = new(
        "npm-config-get-cache",
        CommandScope.User,
        "npm",
        [new ArgPart.Literal("config"), new ArgPart.Literal("get"), new ArgPart.Literal("cache")],
        TimeSpan.FromSeconds(30),
        64 * 1024);

    private const long Gib = 1L << 30;

    public ActionId Id { get; } = ActionId.Find("A8")!;

    public string Summary => "npm cache clean --force, as the target user (runuser, npm from the user's bin folders)";

    public CommandScope Scope => CommandScope.User;

    /// <summary><c>~/.npm</c>, which <c>npm cache clean</c> empties.</summary>
    public IReadOnlyList<HomeFolder> HomeRoots { get; } = [HomeFolder.Of(".npm")];

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [Clean, WhereCache];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var row = CleanupPreviews.A8Row(context.Config, LastFullRun.Read(context.Paths, context.Files, context.Clock).Folders);
        var cache = CacheFolders.UnderHome(context, ".npm");
        var preview = Skipped(RowPreviews.FromFolder(row, "npm cache", cache.Length > 0 ? cache : "~/.npm"), context, commands);
        return preview.Skip.Length > 0 || context.TargetUser is not TargetUserResult.Found
            ? preview
            : preview with { Refusal = await CacheFolders.ConfiguredCacheRefusal(context, commands, WhereCache, "npm", cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>Plan §5 A8: <c>~/.npm</c> above <c>npm.maxCacheGb</c>.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var maxGb = config.Int(ConfigKeys.Npm.MaxCacheGb);
        var bytes = preview.Bytes ?? 0;
        return new TriggerDecision(bytes > maxGb * Gib, string.Create(CultureInfo.InvariantCulture, $"~/.npm holds {bytes / (double)Gib:0.00} GiB; the trigger is above {maxGb} GiB"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var cache = CacheFolders.UnderHome(context, ".npm");
        if (cache.Length == 0)
        {
            return new ActionRun(0, null, "nothing was cleaned", null, null, [], commands.Ran, context.TargetUser.Refusal);
        }

        var before = CacheFolders.Measure(context.Files, cache, cancellationToken);
        var outcome = await commands.RunAsync(Clean, [], cancellationToken).ConfigureAwait(false);
        var after = CacheFolders.Measure(context.Files, cache, cancellationToken);
        var freed = CacheFolders.Freed(before, after);
        var removed = Cleaned(cache, freed);
        return new ActionRun(removed.Count, freed, FreedBasis(freed), before.CompleteBytes, after.CompleteBytes, removed, commands.Ran, CommandFailures.Of("npm cache clean --force", outcome));
    }

    /// <summary>The cache, as the one object cleaned, when it gave something back.</summary>
    private static IReadOnlyList<ActionItem> Cleaned(string cache, long? freed) => freed is > 0 ? [new ActionItem("npm cache", cache, freed)] : [];

    private static string FreedBasis(long? freed) =>
        freed is null ? "unknown: a walk of ~/.npm was cut by its ceiling or could not read it" : "the size of ~/.npm walked right before minus right after npm cleaned it";

    /// <summary>A target user without npm in their bin folders: nothing to do, said as a skip.</summary>
    private static ActionPreview Skipped(ActionPreview preview, ActionContext context, ActionCommands commands) =>
        context.TargetUser is TargetUserResult.Found && commands.Locate(Clean) is ResolvedExecutable.NotFound missing
            ? preview with { Skip = $"npm is not installed for {CacheFolders.UserName(context)}: {missing.Reason}" }
            : preview;
}
