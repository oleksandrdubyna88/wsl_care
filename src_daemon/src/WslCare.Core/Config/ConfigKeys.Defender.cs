namespace WslCare.Core.Config;

/// <summary>The <c>defender.*</c> keys (E15.S10 of PLAN_disk_health.md): what the full run keeps of Windows Defender's CPU between
/// runs. One file per key group, as the others; the key joins <c>NumberKeys()</c> like every other number key.</summary>
public static partial class ConfigKeys
{
    public static class Defender
    {
        /// <summary>E15.S10: the lines of <c>{state}/defender-cpu.jsonl</c> older than this many days are dropped when the file is next
        /// written. Machine-only: it bounds a file root keeps (400 days × 30 runs a day × ~160 B ≈ 1.9 MB at the most). Default 400 — more than a
        /// year, so the next season's change can be set against the same week of the year before (E16.S5 needs at least 7 days).</summary>
        public static readonly ConfigKey.IntKey LocalRetentionDays = new("defender.localRetentionDays", 30, 1000) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }
}
