using System.Globalization;

using WslCare.Core.Files;

namespace WslCare.Cli;

/// <summary>
/// A list on STDIN — A4's shown list (<c>act … --only -</c>, E6.S1, plan §15j M2) and the manual AI agents
/// (<c>config set aiAgents.extra -</c>, E7.S2, plan §15q D4) — read under the same byte cap as an <c>--only</c>
/// file — one byte past 1 MiB is a refusal, whatever arrives after — AND a time ceiling, because a pipe whose writer never
/// closes would otherwise hold the run forever (a timeout is ours to decide: it is a refusal with its reason, never a
/// cancellation). All of it before the lock and before any state is touched; the lines are validated by the caller and never
/// echoed. Measured 2026-10-04 (research/2026-10-03_wsl_exe_facts.md row 20): 650 000 bytes reach a Linux program through
/// <c>wsl.exe</c> byte for byte, with the EOF.
/// </summary>
internal static class StdinList
{
    /// <summary>The ceiling a person waits for the list's end (plan §15j M2: 10 s).</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    /// <summary>The most bytes a list on stdin may hold (plan §15j M2: 1 MiB) — one byte more is a refusal.</summary>
    public const int MaxBytes = 1024 * 1024;

    public static FileReadResult Read(Stream stdin, int maxBytes, TimeSpan ceiling)
    {
        // The reading task is abandoned on the ceiling: the process refuses and ends, which closes the stream under it.
        var read = Task.Run(() => Capped(stdin, maxBytes));
        return read.Wait(ceiling)
            ? read.Result
            : new FileReadResult.Unreadable(string.Create(CultureInfo.InvariantCulture, $"had no end within {ceiling.TotalSeconds:0.#} s (the list must be followed by the end of input)"));
    }

    private static FileReadResult Capped(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total > maxBytes
            ? new FileReadResult.Unreadable(string.Create(CultureInfo.InvariantCulture, $"is larger than {maxBytes} bytes"))
            : new FileReadResult.Content(buffer[..total]);
    }
}
