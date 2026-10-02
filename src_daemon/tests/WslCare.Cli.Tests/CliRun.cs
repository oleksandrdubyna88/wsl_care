using Serilog;
using Serilog.Core;

using WslCare.Cli;
using WslCare.Core.Config;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>Runs the whole program in-process over a sandboxed host, streams captured.</summary>
internal static class CliRun
{
    public static (int Exit, string Stdout, string Stderr) Over(SandboxHost sandbox, params string[] args) =>
        Over(sandbox, Logger.None, CancellationToken.None, args);

    public static (int Exit, string Stdout, string Stderr) Over(SandboxHost sandbox, ILogger logger, CancellationToken cancellationToken, params string[] args)
    {
        var host = new CliHost(sandbox.Paths, sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner());
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, host, loaded, logger, cancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Lines as a terminal shows them: ANY line break counts, not only this platform's
    /// <see cref="Environment.NewLine"/> — splitting on "\r\n" alone let a bare "\n" pass as one line.</summary>
    public static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
