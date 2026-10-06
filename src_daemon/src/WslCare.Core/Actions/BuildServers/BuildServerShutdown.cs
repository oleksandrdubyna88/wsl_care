using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.BuildServers;

/// <summary>
/// A3 (plan §5): <c>dotnet build-server shutdown</c> as the OWNING user (the target user, plan §15c #2, through
/// <c>runuser</c>) — the official command; the next build starts them again. It stops the target user's .NET build servers
/// (MSBuild nodes, the Roslyn compiler server, the Razor server: the <c>dotnet-build-servers</c> family of plan §4.2).
/// Auto trigger: one of them has been alive for <c>buildServers.idleHours</c> or longer. REFUSED — for a button too — while
/// any <c>dotnet build</c> / <c>test</c> / <c>run</c> (or <c>publish</c>, <c>pack</c>, <c>msbuild</c>, <c>watch</c>) is alive:
/// stopping the servers under a build fails it.
/// </summary>
/// <remarks>
/// <para><b>Re-checked at run time</b>: the process table is read again just before the command; a build that started since
/// the preview stops the run before anything is asked (nothing stopped, said in the notes).</para>
/// <para><b>Measured</b>: the servers of the preview are looked up again after the command; each one gone is an item with
/// the memory it held (<c>RssAnon</c> + <c>RssShmem</c>), each one still alive is not removed. A3 frees memory, not disk.</para>
/// <para>A <c>dotnet</c> that is not in the target user's bin folders is a SKIP with the reason, never a failure.</para>
/// </remarks>
public sealed class BuildServerShutdown : ICleanupAction
{
    public const string IdleServersFact = "idleServers";

    public const string Family = "dotnet-build-servers";

    public static readonly CommandTemplate Shutdown = new(
        "dotnet-build-server-shutdown",
        CommandScope.User,
        "dotnet",
        [new ArgPart.Literal("build-server"), new ArgPart.Literal("shutdown")],
        ConfigKeys.BuildServers.ShutdownTimeoutSeconds,
        ConfigKeys.Commands.OutputCapBytes);

    private const string Kind = "process";

    private static readonly string[] BuildVerbs = ["build", "test", "run", "publish", "pack", "msbuild", "watch"];

    public ActionId Id { get; } = ActionId.Find("A3")!;

    public string Summary => "dotnet build-server shutdown as the target user, never while a dotnet build, test or run is alive";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [Shutdown];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var hours = context.Config.Int(ConfigKeys.BuildServers.IdleHours);
        var what = string.Create(CultureInfo.InvariantCulture, $"dotnet build-server shutdown as the target user: its MSBuild nodes, compiler and Razor servers (the timer: one alive for {hours} h or more)");
        if (context.TargetUser is not TargetUserResult.Found found)
        {
            return Task.FromResult(ActionPreview.Unavailable(what, context.TargetUser.Refusal));
        }

        if (context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return Task.FromResult(ActionPreview.Unavailable(what, "the process table could not be read"));
        }

        var servers = Servers(snapshot.All, found.User.Name);
        var builds = Builds(snapshot.All);
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [IdleServersFact] = AliveFor(servers, TimeSpan.FromHours(hours)) };
        var refusal = BuildRefusal(builds);
        var preview = ActionPreview.Of(what, servers.Count, null, "the process table now; each item carries the memory its server holds (RssAnon + RssShmem) - memory, not disk, so no bytes are counted", facts, refusal, [.. servers.Select(Item)]);
        return Task.FromResult(Skip(preview, servers.Count, commands));
    }

    /// <summary>Plan §5 A3: a build server alive for <c>buildServers.idleHours</c> or longer.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var hours = config.Int(ConfigKeys.BuildServers.IdleHours);
        return preview.Facts.TryGetValue(IdleServersFact, out var idle)
            ? new TriggerDecision(idle > 0, string.Create(CultureInfo.InvariantCulture, $"{idle} of {preview.Count} build server(s) alive for {hours} h or more (buildServers.idleHours); the trigger is any"))
            : new TriggerDecision(false, "the build servers were not read");
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (Stopped(context.Processes(cancellationToken), commands) is { } stoppedBefore)
        {
            return stoppedBefore;
        }

        var failure = CommandFailures.Of("dotnet build-server shutdown", await commands.RunAsync(Shutdown, [], cancellationToken).ConfigureAwait(false));
        return context.Processes(cancellationToken) is Reading<ProcessSnapshot>.Available { Value: var table }
            ? Measured(preview, commands, failure, table.All.Select(p => p.Pid).ToHashSet())
            : new ActionRun(0, null, FreedBasis, null, null, [], commands.Ran, failure)
            {
                NotRemoved = preview.Targets,
                Notes = ["the process table could not be read after the command: which servers ended is unknown"],
            };
    }

    private const string FreedBasis = "A3 frees memory, not disk: each stopped server's item carries what it held";

    /// <summary>The servers gone after the command (removed) and those still running (kept, with why).</summary>
    private static ActionRun Measured(ActionPreview preview, ActionCommands commands, string failure, HashSet<int> alive)
    {
        var stopped = preview.Targets.Where(t => !alive.Contains(Pid(t))).ToList();
        return new ActionRun(stopped.Count, null, FreedBasis, null, null, stopped, commands.Ran, failure)
        {
            NotRemoved = [.. preview.Targets.Where(t => alive.Contains(Pid(t))).Select(t => t with { Note = "still running after dotnet build-server shutdown" })],
        };
    }

    /// <summary>How many of <paramref name="servers"/> are alive for <paramref name="age"/> or longer.</summary>
    private static int AliveFor(IReadOnlyList<ProcessEntry> servers, TimeSpan age) =>
        servers.Count(s => s.Age is Reading<TimeSpan>.Available { Value: var alive } && alive >= age);

    private static string BuildRefusal(IReadOnlyList<string> builds) =>
        builds.Count > 0 ? $"a dotnet build is alive ({string.Join("; ", builds.Take(3))}): stopping its servers would fail it" : string.Empty;

    /// <summary>The re-check just before the command: the run it stops at (a build alive since the preview — nothing asked;
    /// a process table that cannot be read again — refused, as the preview refuses, since a build that cannot be ruled out
    /// may be alive: independent review of E3, 2026-10-03), or none when the command may go.</summary>
    private static ActionRun? Stopped(Reading<ProcessSnapshot> now, ActionCommands commands) => now switch
    {
        Reading<ProcessSnapshot>.Unavailable unread => ActionRun.Nothing(commands.Ran, "nothing stopped") with
        {
            Failure = $"the process table could not be read again just before the command ({unread.Reason}): nothing was stopped",
        },
        Reading<ProcessSnapshot>.Available { Value: var table } when Builds(table.All) is { Count: > 0 } builds => ActionRun.Nothing(commands.Ran, "nothing stopped") with
        {
            Notes = [$"a dotnet build started since the preview ({string.Join("; ", builds.Take(3))}): nothing was stopped"],
        },
        _ => null,
    };

    /// <summary>The target user's .NET build servers (plan §4.2's family).</summary>
    public static IReadOnlyList<ProcessEntry> Servers(IEnumerable<ProcessEntry> processes, string user) =>
        [.. processes.Where(p => p.Family == Family && p.User == user && p.State != 'Z')];

    /// <summary>Every running <c>dotnet build|test|run|publish|pack|msbuild|watch</c>, whoever runs it.</summary>
    public static IReadOnlyList<string> Builds(IEnumerable<ProcessEntry> processes) =>
        [.. processes.Where(p => IsDotnetBuild(p.CommandLine)).Select(p => p.CommandLine)];

    internal static bool IsDotnetBuild(string commandLine)
    {
        var words = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && NeverList.Exe(words) == "dotnet" && BuildVerbs.Contains(words[1]);
    }

    private static ActionPreview Skip(ActionPreview preview, int servers, ActionCommands commands) =>
        commands.Locate(Shutdown) is ResolvedExecutable.NotFound missing
            ? preview with { Skip = $"dotnet is not installed for the target user: {missing.Reason}" }
            : servers == 0 ? preview with { Skip = "no .NET build server of the target user is running" } : preview;

    private static ActionItem Item(ProcessEntry p) =>
        new(Kind, string.Create(CultureInfo.InvariantCulture, $"{p.Pid} {p.Name}"), p.HeldBytes,
            string.Create(CultureInfo.InvariantCulture, $"{p.Age.Map(a => a.TotalHours).ValueOr(0):0.0} h old: {p.CommandLine}"))
        {
            Key = p.Pid.ToString(CultureInfo.InvariantCulture),
        };

    private static int Pid(ActionItem item) => int.Parse(item.Key, CultureInfo.InvariantCulture);
}
