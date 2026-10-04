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
        var host = CliHost.ForThisMachine() with { InterruptCause = () => shutdown.Cause };
        var loaded = host.LoadConfig();
        using var logger = WslCareLogging.Start(host, loaded.Config, AppName, Console.Error);
        try
        {
            return Run(args, Console.Out, Console.Error, host, loaded, logger, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.Token.IsCancellationRequested)
        {
            logger.Warning("interrupted by a signal before the command finished");
            return Output.Interrupted(Console.Error);
        }
        catch (Exception e)
        {
            // The last frame before "nobody above me": a defect escaping here is reported in one
            // line and a distinct code instead of a .NET crash dump the extension cannot parse.
            logger.Fatal(e, "internal error");
            return Output.Internal(Console.Error, $"{e.GetType().Name}: {e.Message}");
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
        return request switch
        {
            Request.ConfigGet get => ConfigCommand.Get(get, loaded, stdout, stderr),
            Request.ConfigSet set => ConfigCommand.Set(set, host, stdout, stderr),
            Request.ConfigReset reset => ConfigCommand.Reset(reset, host, stdout, stderr),
            Request.Status status => StatusCommand.Run(status, host, loaded, stdout, cancellationToken),
            Request.Preview preview => PreviewCommand.Run(preview, host, loaded, stdout, cancellationToken),
            Request.Collect collect => CollectCommand.Run(collect, host, loaded, stdout, stderr, logger, cancellationToken),
            Request.Doctor doctor => DoctorCommand.Run(doctor, host, loaded, stdout, cancellationToken),
            Request.EventsFollow follow => EventsCommand.Run(follow, host, stdout, stderr, logger, cancellationToken),
            Request.Act act => ActCommand.Run(act, host, loaded, stdout, stderr, logger, cancellationToken),
            Request.Logs logs => LogsCommand.Logs(logs, host, stdout, stderr),
            Request.Runs runs => LogsCommand.Runs(runs, host, stdout, stderr),
            Request.RunsShow show => LogsCommand.Show(show, host, stdout, stderr),
            var other => throw new UnreachableException($"no route for {other.GetType().Name}"),
        };
    }

    /// <summary>What <c>--version</c> prints and <c>status --json</c> names as <c>productVersion</c> — one expression, so
    /// the two cannot disagree (plan §15g B1).</summary>
    internal static string VersionText => ProductVersion.Of(typeof(Program).Assembly).Text;

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

        // At Information on purpose: a run log that holds no line is a file nobody can read anything from.
        log.Information("{Request}: wsl-care {Argv}", request.GetType().Name, string.Join(' ', args.Select(CommandLine.Printable)));
    }
}
