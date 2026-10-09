using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>The side's lock: <paramref name="State"/> <c>free</c>, <c>running</c>, <c>stuck-in-kernel</c> (its holder in state <c>D</c>,
/// review M11) or <c>unknown</c> (held, its holder not readable).</summary>
public sealed record ArchiveLockReport(string State, int Pid, long StartTicks, string RunId, DateTimeOffset? SinceUtc);

/// <summary>One entry on its way, as <c>archive status</c> shows it (the user's own answer; root's records never name a session).</summary>
public sealed record ArchiveInflightReport(string Agent, string Key, string Month, string State, int Files, DateTimeOffset? ArchivedAtUtc);

/// <summary><c>archive status --json</c> (plan §15r E9.S2b): what the side is doing now, from LOCAL state only — the base is never
/// read, so a share that does not answer cannot hang it.</summary>
public sealed record ArchiveStatusReport(
    int SchemaVersion,
    string Side,
    string SideFolder,
    string BaseFolder,
    ArchiveLockReport Lock,
    IReadOnlyList<ArchiveInflightReport> Inflight,
    LastRunRecord? LastRun);

public static class ArchiveStatus
{
    public static ArchiveStatusReport Of(IHostPaths paths, IFileSystem files, EffectiveConfig config, IProcessTable processes)
    {
        var state = new ArchiveState(paths, files);
        return new ArchiveStatusReport(
            SchemaVersion.Current,
            paths.Side == HostSide.Wsl ? "wsl" : "windows",
            SideName.OfThisProcess(paths.Side),
            config.Text(ConfigKeys.Archive.BaseFolder),
            Lock(paths, files, state, processes),
            [.. state.Inflight().Entries.Select(e => new ArchiveInflightReport(e.Agent, e.Key, e.Month, e.State, e.Files, e.ArchivedAtUtc == DateTimeOffset.MinValue ? null : e.ArchivedAtUtc))],
            state.LastRun());
    }

    /// <summary>The lock taken and let go at once when free; when held, its holder as <c>holder.json</c> names it and — inside the
    /// distribution — whether that process sits in uninterruptible sleep (a share that stopped answering).</summary>
    private static ArchiveLockReport Lock(IHostPaths paths, IFileSystem files, ArchiveState state, IProcessTable processes)
    {
        if (!files.DirectoryExists(state.Folder))
        {
            return new ArchiveLockReport("free", 0, 0, string.Empty, null);
        }

        if (files.TryLockExclusive(state.LockFile) is ExclusiveLock.Held held)
        {
            held.Handle.Dispose();
            return new ArchiveLockReport("free", 0, 0, string.Empty, null);
        }

        return state.Holder() is { } holder
            ? new ArchiveLockReport(HolderState(paths, files, processes, holder.Pid), holder.Pid, holder.StartTicks, holder.RunId, holder.SinceUtc)
            : new ArchiveLockReport("unknown", 0, 0, string.Empty, null);
    }

    private static string HolderState(IHostPaths paths, IFileSystem files, IProcessTable processes, int pid) =>
        processes.Lookup(pid) is not ProcessLookup.Alive ? "unknown"
        : paths is LinuxHostPaths linux && StateLetter(files, linux, pid) == 'D' ? "stuck-in-kernel"
        : "running";

    /// <summary>The state letter of <c>/proc/&lt;pid&gt;/stat</c> — the character after the command's closing parenthesis.</summary>
    private static char StateLetter(IFileSystem files, LinuxHostPaths paths, int pid)
    {
        var text = files.ReadFile(Path.Combine(paths.ProcRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat")) is FileReadResult.Content content
            ? System.Text.Encoding.ASCII.GetString(content.Bytes)
            : string.Empty;
        var close = text.LastIndexOf(')');
        return close >= 0 && close + 2 < text.Length ? text[close + 2] : '?';
    }
}
