using Serilog;
using Serilog.Core;
using Serilog.Events;

using WslCare.Core.Config;

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
/// <para>A log directory that cannot be written — <c>/var/log/wsl-care</c> when a person runs
/// <c>config get</c> as themselves — is a degraded log, not a failure: the file sink is dropped with a
/// note on the console and the command still answers.</para>
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

        var fileSinkOpened = TryAddFileSink(configuration, host.Paths.LogDirectory, appName, startedUtc, console);
        var logger = configuration.CreateLogger();
        if (fileSinkOpened)
        {
            Prune(logger, host, config, DateOnly.FromDateTime(startedUtc));
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
            console.WriteLine($"{appName}: log directory '{logRoot}' is not writable ({e.Message}); continuing with console logging only.");
            return false;
        }
    }

    private static void Prune(Logger logger, CliHost host, EffectiveConfig config, DateOnly todayUtc)
    {
        var report = LogRetention.Prune(host.Files, host.Paths.LogDirectory, todayUtc, config.Int(ConfigKeys.Logging.RetentionDays));
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
