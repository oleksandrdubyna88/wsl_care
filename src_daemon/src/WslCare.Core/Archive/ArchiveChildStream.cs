using System.Text.Json;

using WslCare.Core.Json;

namespace WslCare.Core.Archive;

/// <summary>A child's stream broke its contract — the stream stops and the child is killed (the runner kills the tree when a line
/// callback throws): its words are data, and data past its bounds is refused, never read on.</summary>
public sealed class ArchiveChildStreamRefused(string reason) : Exception(reason);

/// <summary>
/// What root reads of an archive child's streamed stdout (plan §15r D8, risk consult 9/9.4 #4, the S4 plan round's finding 1):
/// PROGRESS lines (<see cref="ArchiveProgressLine"/>, each at most <c>archive.progressLineMaxBytes</c>) are validated, counted as a
/// step and DROPPED — never kept, so the number of files a run copies cannot outgrow any cap; the ANSWER is the ONE line that is not
/// progress, the last, shorter than the runner's cut (<paramref name="lineCut"/>, <c>archive.childOutputCapBytes</c>): a line as long
/// as the cut may have been cut and is refused, never parsed as a prefix. A malformed progress line, a second line that is not
/// progress, or anything after the answer ends the stream.
/// </summary>
public sealed class ArchiveChildStream(int progressLineMaxBytes, int lineCut)
{
    private const string ProgressStart = "{\"progress\":";

    private readonly Lock _gate = new();
    private string _answer = string.Empty;

    /// <summary>The answer line; empty when none came.</summary>
    public string Answer
    {
        get
        {
            lock (_gate)
            {
                return _answer;
            }
        }
    }

    /// <summary>How many progress lines arrived.</summary>
    public int ProgressLines { get; private set; }

    /// <summary>One line of the stream; throws <see cref="ArchiveChildStreamRefused"/> when it breaks the contract.</summary>
    public void Take(string line)
    {
        lock (_gate)
        {
            if (Refusal(line) is { Length: > 0 } refusal)
            {
                throw new ArchiveChildStreamRefused(refusal);
            }

            Keep(line);
        }
    }

    private string Refusal(string line) =>
        line.Length >= lineCut ? $"a line reached the cut of {lineCut} characters, so it may have been cut; it is never read as the start of a record"
        : _answer.Length > 0 ? "the child wrote on after its answer; its answer is ONE line, the last"
        : line.StartsWith(ProgressStart, StringComparison.Ordinal) ? ProgressRefusal(line)
        : string.Empty;

    private void Keep(string line)
    {
        if (line.StartsWith(ProgressStart, StringComparison.Ordinal))
        {
            ProgressLines++;
            return;
        }

        _answer = line;
    }

    /// <summary>A progress line is small and well formed: a known kind, counts that are not negative, no name of anything.</summary>
    private string ProgressRefusal(string line)
    {
        if (line.Length > progressLineMaxBytes)
        {
            return $"a progress line is longer than archive.progressLineMaxBytes ({progressLineMaxBytes})";
        }

        try
        {
            return JsonSerializer.Deserialize(line, WslCareJsonContext.Default.ArchiveProgressLine) is { Progress: "file" or "heartbeat", Files: >= 0, Bytes: >= 0 } progress && double.IsFinite(progress.Seconds) && progress.Seconds >= 0
                ? string.Empty
                : "a progress line is not a progress line (its kind or a count is out of shape)";
        }
        catch (JsonException)
        {
            return "a progress line is not valid JSON";
        }
    }
}
