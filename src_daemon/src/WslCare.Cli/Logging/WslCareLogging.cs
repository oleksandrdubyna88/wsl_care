using Serilog;
using Serilog.Core;
using Serilog.Events;

using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Cli.Logging;

/// <summary>
/// The one place logging is configured (family logging rule): a coloured console sink on STDERR
/// — this process's stdout carries the answers the extension parses — and a file per run under
/// <c>{log dir}/{yyyy-MM-dd}/wsl-care-{HH-mm-ss}-{pid}.log</c>, everything UTC, the level from the
/// configuration, and a 14-day startup prune of day folders that goes through the file-system seam.
/// </summary>
/// <remarks>
/// <para>Configured in code, not through <c>Serilog.Settings.Configuration</c>: that package finds
/// sinks by scanning assemblies, which is reflection a Native AOT binary does not have. The keys are
/// <c>logging.minimumLevel</c> and <c>logging.retentionDays</c> in the daemon's own configuration.</para>
/// <para>A run that may not write <c>/var/log/wsl-care</c> — a person running <c>config get</c> or <c>collect</c>
/// as themselves — logs to the user's own <c>$XDG_STATE_HOME/wsl-care/logs</c> (plan §15b #3, <see cref="LogRoot"/>).
/// When even that cannot be written the log is degraded, not failed: the file sink is dropped with a note on the
/// console and the command still answers.</para>
/// </remarks>
internal static class WslCareLogging
{
    private const string FileTemplate =
        "[{UtcTimestamp:yyyy-MM-dd HH:mm:ss.fff}Z {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}";

    public static Logger Start(CliHost host, EffectiveConfig config, string appName, TextWriter console)
    {
        var startedUtc = host.Clock.GetUtcNow().UtcDateTime;
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(Level(config.Text(ConfigKeys.Logging.MinimumLevel)))
            .Enrich.FromLogContext()
            .Enrich.With(new UtcTimestampEnricher())
            .Enrich.WithProperty("Application", appName)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Sink(new AnsiConsoleSink(console));

        var logRoot = LogRoot(host);
        var fileSinkOpened = TryAddFileSink(configuration, logRoot, appName, startedUtc, console);
        var logger = configuration.CreateLogger();
        if (fileSinkOpened)
        {
            Prune(logger, host, logRoot, config, DateOnly.FromDateTime(startedUtc));
        }

        return logger;
    }

    /// <summary>Serilog's level names, as the schema allows them; no <c>Enum.Parse</c>, nothing reflective.</summary>
    internal static LogEventLevel Level(string name) => name switch
    {
        "Verbose" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    private static bool TryAddFileSink(LoggerConfiguration configuration, string logRoot, string appName, DateTime startedUtc, TextWriter console)
    {
        try
        {
            configuration.WriteTo.Sink(new DailyRunFileSink(logRoot, appName, FileTemplate, startedUtc));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Output.Note(console, $"log directory '{logRoot}' is not writable ({e.Message}); continuing with console logging only.");
            return false;
        }
    }

    /// <summary>The run log's root: the system log directory when this process may write it, otherwise the user's own
    /// (plan §15b #3: an unprivileged run logs to <c>$XDG_STATE_HOME/wsl-care/logs</c>).</summary>
    internal static string LogRoot(CliHost host) =>
        host.Files.ProbeWriteAccess(host.Paths.LogDirectory) is WriteAccess.Writable ? host.Paths.LogDirectory : host.Paths.UserLogDirectory;

    private static void Prune(Logger logger, CliHost host, string logRoot, EffectiveConfig config, DateOnly todayUtc)
    {
        var report = LogRetention.Prune(host.Files, logRoot, todayUtc, config.Int(ConfigKeys.Logging.RetentionDays));
        if (report.Deleted.Count > 0)
        {
            logger.Information("log retention removed {Count} day folder(s) older than {RetentionDays} days", report.Deleted.Count, config.Int(ConfigKeys.Logging.RetentionDays));
        }

        foreach (var line in report.Refused.Concat(report.Failed))
        {
            logger.Warning("log retention skipped {Folder}", line);
        }
    }

    /// <summary>Adds <c>UtcTimestamp</c>: Serilog's own <c>{Timestamp}</c> renders local time, and the file it
    /// goes into is named from UTC. One file, one clock.</summary>
    private sealed class UtcTimestampEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory) =>
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("UtcTimestamp", logEvent.Timestamp.UtcDateTime));
    }
}
