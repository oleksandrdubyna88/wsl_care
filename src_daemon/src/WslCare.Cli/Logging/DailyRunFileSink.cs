using System.Globalization;

using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace WslCare.Cli.Logging;

/// <summary>
/// The run's log on disk: a folder per UTC day, a file per run — and, for a run that outlives the
/// day, a new <c>00-00-00</c> segment in the next day's folder with the same pid.
/// </summary>
/// <remarks>
/// <para>Ported from the family (the credential store's <c>DailyRunFileSink</c>). The boundary is the
/// CLOCK, not twenty-four hours after startup, and the roll is FORWARD only: an event stamped
/// earlier than the open segment — a clock correction, a queued line — lands in the open file rather
/// than reopening yesterday's. A late line in the right run beats a lost file.</para>
/// <para>Deviation from the family's copy, recorded: the bytes are written by a <see cref="StreamWriter"/>
/// of this class rather than by <c>Serilog.Sinks.File</c>. One fewer package in an AOT binary, and
/// the segment logic — the part worth sharing — owns the file either way. The writer flushes every
/// line: a daemon's log is read after a crash, and a buffered line is a line the crash ate.</para>
/// </remarks>
internal sealed class DailyRunFileSink : ILogEventSink, IDisposable
{
    private const string SegmentStart = "00-00-00";

    private readonly string _root;
    private readonly string _appName;
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly object _gate = new();

    private DateOnly _day;
    private StreamWriter _writer;
    private volatile string _path;

    public DailyRunFileSink(string logRoot, string appName, string outputTemplate, DateTime startedUtc)
    {
        _root = logRoot;
        _appName = appName;
        _formatter = new MessageTemplateTextFormatter(outputTemplate, formatProvider: null);
        _day = DateOnly.FromDateTime(startedUtc);
        _path = FilePath(logRoot, appName, _day, startedUtc.ToString("HH-mm-ss", CultureInfo.InvariantCulture));
        _writer = Open(_path);
    }

    /// <summary>The file being written right now; changes at every UTC midnight the process lives through.</summary>
    public string CurrentPath => _path;

    public void Emit(LogEvent logEvent)
    {
        var utcDay = DateOnly.FromDateTime(logEvent.Timestamp.UtcDateTime);
        lock (_gate)
        {
            if (utcDay > _day)
            {
                Roll(utcDay);
            }

            _formatter.Format(logEvent, _writer);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void Roll(DateOnly to)
    {
        _writer.Dispose();
        _day = to;
        _path = FilePath(_root, _appName, to, SegmentStart);
        _writer = Open(_path);
    }

    private static string FilePath(string logRoot, string appName, DateOnly utcDay, string time)
    {
        var folder = Path.Combine(logRoot, utcDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        // 0755 / 0644 at most on Linux (E6.S1 review S1): a root umask of 000 must not leave the run logs writable by every account.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return Path.Combine(folder, $"{appName}-{time}-{Environment.ProcessId}.log");
    }

    /// <summary>Append, shared for reading: a <c>tail -f</c> on the live file is the normal way to watch a run.</summary>
    private static StreamWriter Open(string file)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        }

        return new(new FileStream(file, options), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
