using System.Diagnostics;

using WslCare.Core;

namespace WslCare.Cli;

/// <summary>
/// <c>wsl-care</c> — the daemon and CLI of plan §6. In this build it answers <c>--help</c> and
/// <c>--version</c> and refuses everything else; the verbs arrive in later stories.
/// </summary>
/// <remarks>
/// <para>Answers go to stdout and nothing else does, because the extension parses stdout. Every
/// refusal is ONE line on stderr prefixed with the binary's name, and a non-zero
/// <see cref="ExitCode"/>.</para>
/// <para>Logging (Serilog, a file per run) is not wired yet: nothing in this build does work worth
/// logging, and the family logging rule lands with the seams in the next story.</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.Out, Console.Error);
        }
        catch (Exception e)
        {
            // The last frame before "nobody above me": a defect escaping here is reported in one
            // line and a distinct code instead of a .NET crash dump the extension cannot parse.
            Console.Error.WriteLine($"{CommandLine.BinaryName}: internal error: {e.GetType().Name}: {e.Message}");
            return (int)ExitCode.Internal;
        }
    }

    /// <summary>The whole program, with its streams passed in so it is a unit test.</summary>
    internal static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr) =>
        CommandLine.Parse(args) switch
        {
            Request.Help => Answer(stdout, CommandLine.HelpText),
            Request.Version => Answer(stdout, ProductVersion.Of(typeof(Program).Assembly).Text),
            Request.Failed failed => Refuse(stderr, failed.Message),
            var other => throw new UnreachableException($"no route for {other.GetType().Name}"),
        };

    private static int Answer(TextWriter stdout, string text)
    {
        stdout.WriteLine(text);
        return (int)ExitCode.Ok;
    }

    private static int Refuse(TextWriter stderr, string message)
    {
        stderr.WriteLine($"{CommandLine.BinaryName}: {message}");
        return (int)ExitCode.Usage;
    }
}
