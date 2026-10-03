using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>What the idle check read: how busy the CPUs were over the window, and the builds that are running.</summary>
/// <param name="CpuBusyPercent">The load average of <paramref name="Window"/> ÷ the CPUs, as a percent (capped at 100).</param>
/// <param name="Window">Which load average answered: <c>1 min</c>, <c>5 min</c> or <c>15 min</c>.</param>
/// <param name="Builds">The command lines of running builds (<c>docker build</c>, <c>dotnet build|test</c>, <c>npm ci</c>).</param>
public sealed record IdleSample(Reading<double> CpuBusyPercent, string Window, Reading<IReadOnlyList<string>> Builds);

/// <summary>Idle, or busy with the reason the action is deferred.</summary>
public sealed record IdleVerdict(bool Idle, string Reason);

/// <summary>
/// Plan §5 <i>Heavy actions wait for idle</i>: CPU below <c>idle.cpuPercent</c> for <c>idle.minutes</c>, and no
/// <c>docker build</c> / <c>dotnet build|test</c> / <c>npm ci</c> running; otherwise the action is DEFERRED to the next run
/// and the deferral is logged (the run detail and the run log name it).
/// </summary>
/// <remarks>
/// <para><b>"For N minutes" is the kernel's load average (E3.S1 decision).</b> A run is a moment, not a 5-minute watch, and
/// the only CPU history the kernel keeps is <c>/proc/loadavg</c>'s 1, 5 and 15-minute averages: the window used is the
/// shortest one covering <c>idle.minutes</c> (15 minutes when it is longer — the longest the kernel keeps), divided by
/// the CPUs of <c>/proc/stat</c>. The load counts runnable AND uninterruptible tasks, so I/O wait reads as busy — the
/// conservative direction: an action is deferred more often, never run on a busy machine.</para>
/// <para><b>Builds</b> are read from the process table the fast probe reads (the distro's PID namespace: a build on the
/// Windows side, or inside a container, is not seen). <c>npm install</c> and <c>dotnet publish|pack|msbuild</c> count too.</para>
/// <para>An unread figure is BUSY with the reason, never idle.</para>
/// </remarks>
public static class IdleGate
{
    private static readonly string[] DotnetBuildVerbs = ["build", "test", "publish", "pack", "msbuild"];
    private static readonly string[] NpmBuildVerbs = ["ci", "install"];

    /// <summary>Whether <paramref name="rule"/> gates a run started by <paramref name="trigger"/>.</summary>
    public static bool Applies(IdleRule rule, RunTrigger trigger) =>
        rule == IdleRule.Always || (rule == IdleRule.TimerOnly && trigger == RunTrigger.Timer);

    /// <summary>Reads <c>/proc/loadavg</c> and <c>/proc/stat</c> under <paramref name="paths"/>' procfs root.</summary>
    public static IdleSample Sample(LinuxHostPaths paths, IFileSystem files, Reading<ProcessSnapshot> processes, int minutes)
    {
        var (field, window) = minutes <= 1 ? (0, "1 min") : minutes <= 5 ? (1, "5 min") : (2, "15 min");
        var load = ProcText.Read(files, $"{paths.ProcRoot}/loadavg").Bind(text => LoadAverage(text, field));
        var cpus = ProcText.Read(files, $"{paths.ProcRoot}/stat").Bind(CpuCount);
        var busy = Reading.Combine(load, cpus, (l, n) => Math.Min(100.0, Math.Round(100.0 * l / n, 1)));
        return new IdleSample(busy, window, processes.Map(p => BuildsAmong(p.All)));
    }

    /// <summary>Idle only when both figures were read and both are quiet.</summary>
    public static IdleVerdict Judge(IdleSample sample, EffectiveConfig config)
    {
        var limit = config.Int(ConfigKeys.Idle.CpuPercent);
        return (sample.CpuBusyPercent, sample.Builds) switch
        {
            (Reading<double>.Unavailable u, _) => new(false, $"deferred: CPU use is unknown ({u.Reason})"),
            (_, Reading<IReadOnlyList<string>>.Unavailable u) => new(false, $"deferred: running builds are unknown ({u.Reason})"),
            (Reading<double>.Available { Value: var busy }, _) when busy >= limit =>
                new(false, string.Create(CultureInfo.InvariantCulture, $"deferred: CPU {busy:0.#} % over the last {sample.Window} is not below idle.cpuPercent {limit} %")),
            (_, Reading<IReadOnlyList<string>>.Available { Value.Count: > 0 } builds) =>
                new(false, $"deferred: a build is running ({string.Join("; ", builds.Value.Take(3))})"),
            (Reading<double>.Available { Value: var quiet }, _) =>
                new(true, string.Create(CultureInfo.InvariantCulture, $"idle: CPU {quiet:0.#} % over the last {sample.Window}, no build running")),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };
    }

    /// <summary>The command lines among <paramref name="processes"/> that are builds.</summary>
    public static IReadOnlyList<string> BuildsAmong(IEnumerable<ProcessEntry> processes) =>
        [.. processes.Where(p => IsBuild(p.CommandLine)).Select(p => p.CommandLine)];

    /// <summary><c>docker … build|bake</c>, <c>dotnet build|test|publish|pack|msbuild</c>, <c>npm ci|install</c>.</summary>
    internal static bool IsBuild(string commandLine)
    {
        var words = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return false;
        }

        var exe = Processes.Policy.NeverList.Exe(words);
        return exe switch
        {
            "docker" or "com.docker.cli" => words.Skip(1).Take(4).Any(w => w is "build" or "bake"),
            "dotnet" => DotnetBuildVerbs.Contains(words[1]),
            "npm" or "npm-cli.js" => NpmBuildVerbs.Contains(words[1]),
            "node" => IsNodeRunningNpmBuild(words),
            _ => false,
        };
    }

    /// <summary><c>node …/npm-cli.js ci|install</c>.</summary>
    private static bool IsNodeRunningNpmBuild(IReadOnlyList<string> words) =>
        words.Count > 2 && Processes.Policy.NeverList.Exe([words[1]]) == "npm-cli.js" && NpmBuildVerbs.Contains(words[2]);

    private static Reading<double> LoadAverage(string text, int field)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > field && double.TryParse(fields[field], NumberStyles.Float, CultureInfo.InvariantCulture, out var load) && load >= 0
            ? Reading.Of(load)
            : Reading.Missing<double>("/proc/loadavg is not \"load1 load5 load15 …\"");
    }

    private static Reading<int> CpuCount(string procStat)
    {
        var count = ProcText.Lines(procStat).Count(l => l.Length > 3 && l.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(l[3]));
        return count > 0 ? Reading.Of(count) : Reading.Missing<int>("/proc/stat lists no cpuN line");
    }
}
