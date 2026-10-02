using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace WslCare.Cli.Logging;

/// <summary>
/// Writes coloured log lines with ANSI escapes, unconditionally, to the writer it is given.
/// </summary>
/// <remarks>
/// <para><b>Ported from the family</b> (the credential store's <c>AnsiConsoleSink</c>) per the shared
/// logging rule, which keeps this code per-repo by deliberate trade. Why not Serilog's own console
/// theme: measured on Serilog.Sinks.Console 6.1.1, <c>theme: AnsiConsoleTheme.Code</c> with
/// <c>applyThemeToRedirectedOutput: true</c> wrote ZERO escape bytes to a redirected stream, while a
/// control writing one escape by hand in the same process produced four. journald and the
/// extension's output channel are redirected streams by definition.</para>
/// <para>The colours are few: the level is the only thing coloured strongly, because a line where
/// everything is coloured is a line where nothing stands out. The message is rendered through
/// Serilog's own formatter with <c>:lj</c> — never <c>RenderMessage()</c>, which quotes every string
/// property. One write per event, built before the lock is taken.</para>
/// <para>This writes to stderr, so the rendered message goes through
/// <see cref="CommandLine.Printable"/> as every <see cref="Output"/> message does: a logged value
/// never carries a raw control character to the terminal.</para>
/// </remarks>
internal sealed class AnsiConsoleSink(TextWriter output) : ILogEventSink
{
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[38;5;245m";
    private const string Context = "\x1b[38;5;110m";

    private readonly object _gate = new();
    private readonly MessageTemplateTextFormatter _message = new("{Message:lj}", formatProvider: null);

    public void Emit(LogEvent logEvent)
    {
        var line = Render(logEvent);
        lock (_gate)
        {
            output.Write(line);
            output.Flush();
        }
    }

    private string Render(LogEvent logEvent)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var context)
            ? Shorten(context.ToString().Trim('"'))
            : string.Empty;

        var line = new StringWriter();
        line.Write($"{Dim}[{logEvent.Timestamp.UtcDateTime:HH:mm:ss}Z{Reset} {LevelColour(logEvent.Level)}{Abbreviate(logEvent.Level)}{Reset}{Dim}]{Reset} ");
        if (source.Length > 0)
        {
            line.Write($"{Context}{source}{Reset}{Dim}:{Reset} ");
        }

        line.Write(CommandLine.Printable(Message(logEvent)));
        line.Write(Environment.NewLine);
        if (logEvent.Exception is not null)
        {
            line.Write($"{LevelColour(LogEventLevel.Error)}{PrintableLines(logEvent.Exception.ToString())}{Reset}{Environment.NewLine}");
        }

        return line.ToString();
    }

    /// <summary>The message alone, so its values — a key read from the user's file, a path, an
    /// exception message — can be made printable before they meet the terminal: only this sink's
    /// own colour escapes may reach it, and a value cannot split the line or repaint the screen.</summary>
    private string Message(LogEvent logEvent)
    {
        var message = new StringWriter();
        _message.Format(logEvent, message);
        return message.ToString();
    }

    /// <summary>An exception keeps its lines (a stack trace is many); each line is made printable.</summary>
    private static string PrintableLines(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => CommandLine.Printable(l.TrimEnd('\r'))));

    internal static string LevelColour(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "\x1b[38;5;240m",
        LogEventLevel.Debug => "\x1b[38;5;244m",
        LogEventLevel.Information => "\x1b[38;5;42m",
        LogEventLevel.Warning => "\x1b[38;5;214m",
        LogEventLevel.Error => "\x1b[38;5;203m",
        _ => "\x1b[1;38;5;199m",
    };

    private static string Abbreviate(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        _ => "FTL",
    };

    /// <summary>The last two segments of a namespace-qualified type — the part that identifies the writer is at the end.</summary>
    private static string Shorten(string sourceContext)
    {
        var parts = sourceContext.Split('.');
        return parts.Length <= 2 ? sourceContext : string.Join('.', parts[^2..]);
    }
}
