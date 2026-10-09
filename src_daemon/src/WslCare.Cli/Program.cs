using System.Diagnostics;

using Serilog;

using WslCare.Cli.Commands;
using WslCare.Cli.Logging;
using WslCare.Core;
using WslCare.Core.Config;

namespace WslCare.Cli;

/// <summary>
/// <c>wsl-care</c> — the daemon and CLI of plan §6. This build answers <c>--help</c>, <c>--version</c>, the
/// <c>config</c> verbs, <c>status</c>, <c>preview</c>, the full run <c>collect</c>, <c>doctor</c> and the follower
/// <c>events follow</c>, and the root-only <c>act</c> (E3.S1: the action engine with its first action, A10), and refuses
/// everything else; the other actions arrive in later stories.
/// </summary>
/// <remarks>
/// <para>Answers go to stdout and nothing else does, because the extension parses stdout. Every
/// refusal is ONE line on stderr prefixed with the binary's name, and a non-zero
/// <see cref="ExitCode"/>. Log lines go to stderr too (the family logging rule's stdio clause: this
/// process's stdout carries a protocol) and to one file per run.</para>
/// <para><c>--help</c>, <c>--version</c> and a usage refusal are answered before the machine is
/// touched: no host, no configuration, no log file — they are questions about the binary, not runs.
/// For every other verb the order in <see cref="Main"/> is the order things are needed: the host
/// (where everything is), the configuration (which decides the log level), the logger, then the verb
/// — and the logger is wrapped in <c>try/catch</c> so a crash while wiring up still leaves a line.
/// Each such run writes one log file (family logging rule) with at least its request line.</para>
/// </remarks>
internal static class Program
{
    private const string AppName = "wsl-care";

    private static int Main(string[] args)
    {
        if (AnswerWithoutTheMachine(CommandLine.Parse(args), Console.Out, Console.Error) is { } quick)
        {
            return quick;
        }

        using var shutdown = new ShutdownSignals();
        var first = CliHost.ForThisMachine() with { InterruptCause = () => shutdown.Cause };
        var loaded = first.LoadConfig();
        // E7.S2c: every configured number reaches its call site through the process's tuning — set once, before anything runs.
        Tuning.ForThisProcess(loaded.Config);
        // Phase two (plan §15q R2.2, review M1): the manual AI agents' folders join the protected roots BEFORE anything runs.
        var (host, protectedLoad) = first.WithAgentExtras(loaded);
        loaded = protectedLoad;
        using var logger = WslCareLogging.Start(host, loaded, AppName, Console.Error);
        return Guarded(() => Run(args, Console.Out, Console.Error, host, loaded, logger, shutdown.Token), logger, Console.Error, shutdown.Token);
    }

    /// <summary>The program's last frame, as <see cref="Main"/> runs it — and as a test runs it (the retro round over PR #11
    /// derives the units' success exits from what a run ENDS with, and a stopped run's 130 is answered here): a cancellation the
    /// signal asked for is the interrupted code; any other exception is a defect.</summary>
    internal static int Guarded(Func<int> run, ILogger logger, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            return run();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Warning("interrupted by a signal before the command finished");
            return Output.Interrupted(stderr);
        }
        catch (Exception e)
        {
            // The last frame before "nobody above me": a defect escaping here is reported in one
            // line and a distinct code instead of a .NET crash dump the extension cannot parse.
            logger.Fatal(e, "internal error");
            return Output.Internal(stderr, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The whole program, with its streams and its machine passed in so it is a unit test.</summary>
    internal static int Run(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CliHost host,
        ConfigLoadResult loaded,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var request = CommandLine.Parse(args);
        if (AnswerWithoutTheMachine(request, stdout, stderr) is { } quick)
        {
            return quick;
        }

        LogRequest(logger, request, loaded, args);
        cancellationToken.ThrowIfCancellationRequested();
        // E7.S2c: the verb reads its numbers from THIS configuration — what Main made the process's tuning, and what a test's
        // host hands in (an AsyncLocal scope: parallel runs never see each other's).
        using var tuned = Tuning.Use(loaded.Config);
        return request switch
        {
            Request.ConfigGet get => ConfigCommand.Get(get, loaded, stdout, stderr),
            Request.ConfigSet set => ConfigCommand.Set(set, host, stdout, stderr),
            Request.ConfigReset reset => ConfigCommand.Reset(reset, host, stdout, stderr),
            Request.Status status => StatusCommand.Run(status, host, loaded, stdout, cancellationToken),
            Request.Preview preview => PreviewCommand.Run(preview, host, loaded, stdout, cancellationToken),
            Request.Collect collect => CollectCommand.Run(collect, host, loaded, stdout, stderr, logger, cancellationToken),
            Request.Doctor doctor => DoctorCommand.Run(doctor, host, loaded, stdout, cancellationToken),
            Request.Busy busy => BusyCommand.Run(busy, host, loaded, stdout),
            Request.Watch watch => WatchCommand.Run(watch, host, loaded, stdout, stderr, logger, cancellationToken),
            Request.AgentsList agents => AgentsCommand.Run(agents, host, loaded, stdout, stderr, cancellationToken),
            Request.AgentsProbe probe => AgentsCommand.Probe(probe, host, stdout, stderr, cancellationToken),
            Request.ArchiveCheckBase check => ArchiveCommand.CheckBase(check, host, stdout, stderr),
            Request.ArchivePreview preview => ArchiveCommand.Preview(preview, host, loaded, stdout, stderr, cancellationToken),
            Request.ArchiveRun run => ArchiveRunCommand.Run(run, host, loaded, stdout, stderr, cancellationToken),
            Request.ArchiveStatus archiveStatus => ArchiveRunCommand.Status(archiveStatus, host, loaded, stdout, stderr),
            Request.ArchiveReconcileScan scan => ArchiveRunCommand.ReconcileScan(scan, host, loaded, stdout, stderr, cancellationToken),
            Request.ArchiveReach reach => ArchiveRunCommand.Reach(reach, host, loaded, stdout, stderr, cancellationToken),
            Request.ArchiveRestore restore => ArchiveRunCommand.Restore(restore, host, loaded, stdout, stderr, cancellationToken),
            Request.ArchiveList list => ArchiveRunCommand.List(list, host, loaded, stdout, stderr, cancellationToken),
            Request.EventsFollow follow => EventsCommand.Run(follow, host, stdout, stderr, logger, cancellationToken),
            Request.Act act => ActCommand.Run(act, host, loaded, stdout, stderr, logger, cancellationToken),
            Request.Logs logs => LogsCommand.Logs(logs, host, stdout, stderr),
            Request.Runs runs => LogsCommand.Runs(runs, host, stdout, stderr),
            Request.RunsShow show => LogsCommand.Show(show, host, stdout, stderr),
            Request.ActFromRequest fromRequest => DetachedRuns.FromRequest(fromRequest, host, loaded, stdout, stderr, logger.ForContext(typeof(DetachedRuns)), cancellationToken),
            Request.UnitsDropIn dropIn => UnitsDropIn(dropIn, loaded, stdout, stderr),
            Request.ActStop stop => RunStops.Stop(stop, host, stdout, stderr, logger.ForContext(typeof(RunStops)), cancellationToken),
            var other => throw new UnreachableException($"no route for {other.GetType().Name}"),
        };
    }

    /// <summary>What <c>--version</c> prints and <c>status --json</c> names as <c>productVersion</c> — one expression, so
    /// the two cannot disagree (plan §15g B1).</summary>
    internal static string VersionText => ProductVersion.Of(typeof(Program).Assembly).Text;

    /// <summary><c>units dropin &lt;unit&gt;</c>: the drop-in of the configuration in force — refused (78, observe-only) while a layer is
    /// in error (E7.S2b/S2c review C-M2): install.sh never installs a drop-in from a configuration the daemon itself refuses.</summary>
    private static int UnitsDropIn(Request.UnitsDropIn request, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        if (loaded.IsObserveOnly)
        {
            Output.Note(stderr, $"units dropin {request.Unit}: the configuration is in error ({string.Join("; ", loaded.Errors.Select(e => e.Display))}); no drop-in is written from it");
            return (int)ExitCode.ObserveOnly;
        }

        return Output.Answer(stdout, Core.Systemd.UnitDropIns.Render(request.Unit).TrimEnd('\n'));
    }

    /// <summary>The three requests that need nothing of the machine; <c>null</c> for a verb that does.</summary>
    private static int? AnswerWithoutTheMachine(Request request, TextWriter stdout, TextWriter stderr) => request switch
    {
        Request.Help => Output.Answer(stdout, CommandLine.HelpText),
        Request.Version => Output.Answer(stdout, VersionText),
        Request.Failed failed => Output.Refuse(stderr, failed.Message),
        _ => null,
    };

    private static void LogRequest(ILogger logger, Request request, ConfigLoadResult loaded, IReadOnlyList<string> args)
    {
        var log = logger.ForContext(typeof(Program));
        foreach (var error in loaded.Errors)
        {
            log.Error("configuration error, running observe-only: {ConfigError}", error.Display);
        }

        foreach (var notice in loaded.Notices)
        {
            log.Warning("configuration value not taken: {ConfigNotice}", notice.Display);
        }

        // At Information on purpose: a run log that holds no line is a file nobody can read anything from.
        log.Information("{Request}: wsl-care {Argv}", request.GetType().Name, string.Join(' ', args.Select(CommandLine.Printable)));
    }
}
