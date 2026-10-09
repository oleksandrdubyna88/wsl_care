using System.Globalization;
using System.Text.Json;

using WslCare.Core.Archive;
using WslCare.Core.Json;

namespace WslCare.Cli.Commands;

/// <summary>
/// The progress of <c>archive run</c> and <c>archive restore</c> (plan §15r D8, the coai code round over S2b/S3): one JSON line per
/// event with <c>--json</c> (no line names a session), a short human line on stderr without it; a heartbeat at least every
/// <c>archive.progressSilenceSeconds</c> from a <see cref="PeriodicTimer"/>; nothing after the answer (review m1); a broken pipe on
/// the consumer's side ends the progress, never the process.
/// </summary>
internal sealed class ArchiveProgress : IDisposable
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _json;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeat;
    private bool _closed;

    public ArchiveProgress(TextWriter stdout, TextWriter stderr, bool json, TimeProvider clock, TimeSpan silence)
    {
        (_stdout, _stderr, _json, _clock) = (stdout, stderr, json, clock);
        _started = clock.GetTimestamp();
        _heartbeat = BeatAsync(silence);
    }

    /// <summary>One event: a file done, or a heartbeat.</summary>
    public void Line(ArchiveProgressLine line)
    {
        lock (_gate)
        {
            if (!_closed)
            {
                Write(line);
            }
        }
    }

    /// <summary>The answer, written last: the heartbeat stopped first, and no line after it.</summary>
    public int Answer(Func<int> answer)
    {
        _stop.Cancel();
        lock (_gate)
        {
            _closed = true;
            return answer();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The heartbeat ends at the cancel; its token source is let go once it has (never under a heartbeat still waking).
        _ = _heartbeat.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    /// <summary>The human form of a line (stderr, without <c>--json</c>): counts and time, never a name.</summary>
    internal static string Human(ArchiveProgressLine line) => line.Progress == "heartbeat"
        ? string.Create(CultureInfo.InvariantCulture, $"archive: still working ({line.Seconds:0} s)")
        : string.Create(CultureInfo.InvariantCulture, $"archive: {line.Files} file(s), {line.Bytes / 1048576.0:0.0} MiB ({line.Seconds:0} s)");

    private void Write(ArchiveProgressLine line)
    {
        try
        {
            if (_json)
            {
                Output.Progress(_stdout, JsonSerializer.Serialize(line, WslCareJsonContext.Compact.ArchiveProgressLine));
            }
            else
            {
                Output.Note(_stderr, Human(line));
            }
        }
        catch (IOException)
        {
            // The consumer went away (a pipe to `head`): no more progress lines; the work and its answer go on.
            _closed = true;
        }
    }

    private async Task BeatAsync(TimeSpan silence)
    {
        using var timer = new PeriodicTimer(silence, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Line(new ArchiveProgressLine("heartbeat", 0, 0, _clock.GetElapsedTime(_started).TotalSeconds));
            }
        }
        catch (OperationCanceledException)
        {
            // stopped by the answer
        }
    }
}
