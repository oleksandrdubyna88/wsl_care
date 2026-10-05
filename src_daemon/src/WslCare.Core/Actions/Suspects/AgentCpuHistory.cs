using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Actions.Suspects;

/// <summary>One AI-agent process as the history keeps it: its identity (pid AND start ticks, within <see cref="AgentCpuFile.BootId"/>),
/// the CPU ticks last seen, since when they have not changed, and when it was last seen.</summary>
public sealed record AgentCpuEntry(int Pid, long StartTicks, long CpuTicks, DateTimeOffset UnchangedSince, DateTimeOffset Seen);

/// <summary><c>/var/lib/wsl-care/agent-cpu.json</c> (plan §15q E7.S2b): one boot's entries.</summary>
public sealed record AgentCpuFile(int SchemaVersion, string BootId, IReadOnlyList<AgentCpuEntry> Entries)
{
    public static readonly AgentCpuFile Empty = new(Core.SchemaVersion.Current, string.Empty, []);
}

/// <summary>
/// "No CPU for N hours", MEASURED (plan §15q E7.S2b item 3): every root run records, for each AI-agent process of a non-root
/// account, its cumulative CPU ticks by identity — <c>(pid, boot_id, start ticks)</c>, <c>/proc/&lt;pid&gt;/stat</c> fields 22
/// and 14 + 15. A process has been idle since the OLDEST sample of the same identity whose ticks equal now's; a different boot,
/// a different start (a reused pid) or no history at all is "no history" — never idle, so the first runs end nothing.
/// The file is root's state (read back with <see cref="IFileSystem.ReadStateFile"/>: uid 0, no link, no wait, a cap), written
/// atomically, holding live processes only, at most <see cref="MaxEntries"/>.
/// </summary>
public static class AgentCpuHistory
{
    public const string FileName = "agent-cpu.json";

    public const int MaxEntries = 512;

    /// <summary>A full history (512 identities, ~180 B each) is ~90 KiB; the cap leaves room above it.</summary>
    public const int MaxBytes = 128 * 1024;

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    /// <summary>The recorded history; <see cref="AgentCpuFile.Empty"/> when there is none or it cannot be read (= no history).</summary>
    public static AgentCpuFile Read(IHostPaths paths, IFileSystem files) =>
        files.ReadStateFile(File(paths), MaxBytes) is FileReadResult.Content content ? Parse(content.Bytes) : AgentCpuFile.Empty;

    private static AgentCpuFile Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.AgentCpuFile) is { BootId.Length: > 0 } file ? file : AgentCpuFile.Empty;
        }
        catch (JsonException)
        {
            return AgentCpuFile.Empty;
        }
    }

    /// <summary>The next history: <paramref name="samples"/> (the live AI-agent processes now) merged into <paramref name="before"/> —
    /// an identity whose ticks did not move keeps its "unchanged since", one that moved starts again now, a new one starts now;
    /// identities not alive now are dropped; another boot starts afresh.</summary>
    public static AgentCpuFile Next(AgentCpuFile before, string bootId, IReadOnlyList<SuspectSample> samples, DateTimeOffset now)
    {
        var known = before.BootId == bootId
            ? before.Entries.ToDictionary(e => (e.Pid, e.StartTicks))
            : [];
        var entries = samples
            .Select(s => known.TryGetValue((s.Pid, s.StartTicks), out var was) && was.CpuTicks == s.CpuTicks
                ? was with { Seen = now }
                : new AgentCpuEntry(s.Pid, s.StartTicks, s.CpuTicks, now, now))
            .OrderBy(e => e.Pid)
            .Take(MaxEntries)
            .ToList();
        return new AgentCpuFile(Core.SchemaVersion.Current, bootId, entries);
    }

    /// <summary>How long <paramref name="sample"/> has used no CPU, by the history; <see cref="TimeSpan.Zero"/> when the history
    /// does not hold this identity with these ticks in this boot (no history = not idle).</summary>
    public static TimeSpan IdleFor(AgentCpuFile history, string bootId, SuspectSample sample, DateTimeOffset now) =>
        history.BootId.Length > 0 && history.BootId == bootId
        && history.Entries.FirstOrDefault(e => e.Pid == sample.Pid && e.StartTicks == sample.StartTicks) is { } entry
        && entry.CpuTicks == sample.CpuTicks
            ? now - entry.UnchangedSince
            : TimeSpan.Zero;

    /// <summary>Empty when written; otherwise why not.</summary>
    public static string Write(IHostPaths paths, IFileSystem files, AgentCpuFile history)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(history, WslCareJsonContext.Default.AgentCpuFile);
            return files.WriteFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, "agent-cpu")) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>This boot's id (<c>/proc/sys/kernel/random/boot_id</c>, under the layout's <c>/proc</c>); empty when unknown.</summary>
    public static string BootId(LinuxHostPaths paths, IFileSystem files) =>
        ProcText.Read(files, $"{paths.ProcRoot}/sys/kernel/random/boot_id").ValueOr(string.Empty).Trim();

    /// <summary>The AI-agent processes of non-root accounts in <paramref name="processes"/>, sampled from <c>/proc</c> now.</summary>
    public static IReadOnlyList<SuspectSample> Sample(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes) =>
        [.. processes
            .Where(p => p.Family == ProcessFamilies.AiAgents && p.User != "root" && p.Pid > 1)
            .Select(p => SuspectTermination.Sample(files, paths, p.Pid))
            .OfType<SuspectSample>()
            .Where(s => s.Uid != 0)];

    /// <summary>One root run's record (the timer's full run, A18's preview): sample, merge, write. Empty when written.</summary>
    public static string Record(LinuxHostPaths paths, IFileSystem files, IEnumerable<ProcessEntry> processes, DateTimeOffset now)
    {
        var boot = BootId(paths, files);
        return boot.Length == 0
            ? "the boot id cannot be read; the AI-agent CPU history is not recorded"
            : Write(paths, files, Next(Read(paths, files), boot, Sample(paths, files, processes), now));
    }
}
