using WslCare.Core.Config;

namespace WslCare.Core.Thresholds;

/// <summary>
/// The seven <c>disk.volumeFree*</c> keys in force (E19.S1; PLAN_disk_hygiene_windows.md §3.2, §3.9), read once so the rule
/// (<see cref="VolumeFreeRules"/>) is pure over its inputs.
/// </summary>
/// <param name="WarnGib">A volume with less free than this is at least <c>warn</c> (GiB floors: volumes of <paramref name="AbsoluteMinSizeGib"/> or more).</param>
/// <param name="CriticalGib">A volume with less free than this is <c>critical</c>.</param>
/// <param name="WarnPercent">A volume with a smaller free share than this is at least <c>warn</c> — the only floor of a small volume.</param>
/// <param name="CriticalPercent">A volume with a smaller free share than this is <c>critical</c>.</param>
/// <param name="MinSizeGib">A volume smaller than this is not judged.</param>
/// <param name="AbsoluteMinSizeGib">The GiB floors apply only to a volume of at least this size.</param>
/// <param name="IgnoreLetters">Drive letters that are not judged.</param>
public sealed record VolumeFreeLimits(
    int WarnGib,
    int CriticalGib,
    int WarnPercent,
    int CriticalPercent,
    int MinSizeGib,
    int AbsoluteMinSizeGib,
    IReadOnlyList<string> IgnoreLetters)
{
    public static VolumeFreeLimits From(EffectiveConfig config) =>
        new(
            config.Int(ConfigKeys.Disk.VolumeFreeWarnGib),
            config.Int(ConfigKeys.Disk.VolumeFreeCriticalGib),
            config.Int(ConfigKeys.Disk.VolumeFreeWarnPercent),
            config.Int(ConfigKeys.Disk.VolumeFreeCriticalPercent),
            config.Int(ConfigKeys.Disk.VolumeFreeMinSizeGib),
            config.Int(ConfigKeys.Disk.VolumeFreeAbsoluteMinSizeGib),
            config.TextList(ConfigKeys.Disk.VolumeFreeIgnoreLetters));
}
