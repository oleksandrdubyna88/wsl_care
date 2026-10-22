using System.Globalization;

using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Thresholds;
using WslCare.Core.Watch;

namespace WslCare.Core.Status;

/// <summary>What the Windows <c>status</c> says of the lettered volumes: the verdict <c>disk.volumeFree</c> and the <c>disk</c> section carrying its
/// per-volume judgements, both on the same basis.</summary>
public sealed record VolumesStatus(Verdict Verdict, DiskReport Disk);

/// <summary>
/// The Windows <c>status</c>' <c>disk.volumeFree</c> (E19.S2, PLAN_disk_hygiene_windows_E19.md §3 decision 1). It judges the Windows watch's
/// <c>volumes.json</c> by <see cref="VolumeFreeRules"/> with the sample's REAL age as the basis (<c>sample</c>, when it was taken, how old it is) —
/// the first sample basis with an age above zero. When there is no such sample within <c>disk.volumeSampleMaxAgePeriods</c> ×
/// <c>mcpWatchdog.periodMinutes</c> (missing, unreadable, of another schema, dated in the future, or older), it reads the drives itself through
/// <see cref="IWindowsVolumes"/> — natively, no process, written nowhere — and says so in the verdict's reason; that reading IS a sample, age 0.
/// A live read that fails too is <c>unknown</c> with both reasons, never <c>ok</c>.
/// </summary>
public static class StatusVolumes
{
    /// <summary>The words that start the reason of a live read; the extension and the tests look for them.</summary>
    public const string ReadLive = "read live by status";

    public static VolumesStatus From(VolumeSampleStore store, IWindowsVolumes live, EffectiveConfig config, DateTimeOffset now)
    {
        var limits = VolumeFreeLimits.From(config);
        var window = Window(config);
        return Trust(store.Read(), window, now) switch
        {
            Choice.FromSample sample => Judged(VolumeFreeRules.Evaluate(Reading.Of(sample.Sample.ToVolumes()), limits), StatusVerdicts.SampleBasis(sample.Sample.SampledAt, now), string.Empty),
            Choice.ReadLive why => Judged(VolumeFreeRules.Evaluate(live.Read(), limits), StatusVerdicts.SampleBasis(now, now), LiveWords(window, why.Why)),
            _ => throw new System.Diagnostics.UnreachableException("Choice is a closed set"),
        };
    }

    /// <summary>How old a sample may be and still be trusted: <c>disk.volumeSampleMaxAgePeriods</c> watch periods.</summary>
    private static TimeSpan Window(EffectiveConfig config) =>
        TimeSpan.FromMinutes((double)config.Int(ConfigKeys.Disk.VolumeSampleMaxAgePeriods) * config.Int(ConfigKeys.McpWatchdog.PeriodMinutes));

    /// <summary>The words a live read puts in front of the rule's own reason.</summary>
    private static string LiveWords(TimeSpan window, string why) =>
        string.Create(CultureInfo.InvariantCulture, $"{ReadLive} (no watch sample within {window.TotalMinutes:0} min): {why}; ");

    private static VolumesStatus Judged(VolumeFreeAssessment assessment, VerdictBasis basis, string prefix) =>
        new(assessment.Verdict with { Reason = prefix + assessment.Verdict.Reason, Basis = basis }, StatusDisk.OnWindowsWith(assessment, basis));

    // ---------- which reading to judge ----------

    /// <summary>Which reading the verdict is judged from — a closed set.</summary>
    private abstract record Choice
    {
        private Choice()
        {
        }

        public sealed record FromSample(VolumeSample Sample) : Choice;

        public sealed record ReadLive(string Why) : Choice;
    }

    private static Choice Trust(Reading<VolumeSample> read, TimeSpan window, DateTimeOffset now) => read switch
    {
        Reading<VolumeSample>.Available { Value: var sample } => Aged(sample, now - sample.SampledAt, window),
        var unread => new Choice.ReadLive(unread.ReasonOrEmpty),
    };

    private static Choice Aged(VolumeSample sample, TimeSpan age, TimeSpan window) => age switch
    {
        _ when age < TimeSpan.Zero => new Choice.ReadLive(string.Create(CultureInfo.InvariantCulture, $"the watch's sample is dated {sample.SampledAt:yyyy-MM-dd HH:mm:ss}Z, in the future")),
        _ when age > window => new Choice.ReadLive(string.Create(CultureInfo.InvariantCulture, $"the watch's sample is {Math.Ceiling(age.TotalMinutes):0} min old")),
        _ => new Choice.FromSample(sample),
    };
}
