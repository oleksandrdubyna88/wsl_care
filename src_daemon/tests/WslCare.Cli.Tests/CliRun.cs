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

    public static (int Exit, string Stdout, string Stderr) Over(SandboxHost sandbox, ILogger logger, CancellationToken cancellationToken, params string[] args) =>
        Over(sandbox, sandbox.Files, logger, cancellationToken, args);

    /// <summary>The same, with <paramref name="files"/> in place of the sandbox's real file system — for
    /// a test that needs the disk to answer something it cannot be made to answer for real.</summary>
    public static (int Exit, string Stdout, string Stderr) Over(SandboxHost sandbox, Core.Files.IFileSystem files, params string[] args) =>
        Over(sandbox, files, Logger.None, CancellationToken.None, args);

    /// <summary>The whole program over a host the test built — its own probe, clock or runner.</summary>
    public static (int Exit, string Stdout, string Stderr) Over(CliHost host, params string[] args) =>
        Over(host, Logger.None, CancellationToken.None, args);

    private static (int Exit, string Stdout, string Stderr) Over(SandboxHost sandbox, Core.Files.IFileSystem files, ILogger logger, CancellationToken cancellationToken, string[] args) =>
        Over(new CliHost(sandbox.Paths, files, new FixedTimeProvider(), new RecordingCommandRunner()), logger, cancellationToken, args);

    public static (int Exit, string Stdout, string Stderr) Over(CliHost host, ILogger logger, CancellationToken cancellationToken, params string[] args)
    {
        var loaded = host.LoadConfig();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, host, loaded, logger, cancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Lines as a terminal shows them: ANY line break counts, not only this platform's
    /// <see cref="Environment.NewLine"/> — splitting on "\r\n" alone let a bare "\n" pass as one line.</summary>
    public static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
