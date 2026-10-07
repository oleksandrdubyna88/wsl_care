using System.Security.Cryptography;
using System.Text.Json;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Archive;

/// <summary>Where an entry stands in the in-flight file (plan §15r D2, D5).</summary>
public static class InflightStates
{
    /// <summary>Phase 1 began: files may be in the base, the index line may be missing.</summary>
    public const string Copying = "copying";

    /// <summary>Phase 1 ended: copied, verified, indexed — waiting for phase 2.</summary>
    public const string Archived = "archived";

    /// <summary>Phase 2 passed its commit point: every source file is under its quarantine name and hashed equal.</summary>
    public const string Removing = "removing";
}

/// <summary>One session on its way (fixed size, plan review round C3: each file's hash and archived path live in the index line).</summary>
/// <param name="Under">The agent's layout root as this process saw it.</param>
/// <param name="QuarantineRun">The run id the quarantine names carry once the entry is <see cref="InflightStates.Removing"/>.</param>
public sealed record InflightEntry(string EntryId, string Agent, string Under, string Key, string Month, string State, string RunId, DateTimeOffset ArchivedAtUtc, int Files, string QuarantineRun);

public sealed record InflightFile(int V, IReadOnlyList<InflightEntry> Entries);

/// <summary>The base as it was mounted when a run first used it (D7): a different mount refuses the run.</summary>
public sealed record BaseRecord(int V, string Folder, string MountType, string MountSource, string MountPoint);

/// <summary>What the last run did — what <c>archive status</c> shows without reading the base.</summary>
public sealed record LastRunRecord(int V, string RunId, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, string Outcome, string Stop, int Copied, int Removed, long Bytes);

/// <summary>Who holds the side's lock now (written right after it was taken; the lock itself is the open handle).</summary>
public sealed record LockHolderRecord(int V, int Pid, long StartTicks, string BootId, DateTimeOffset SinceUtc, string RunId);

/// <summary>
/// Plan §15r D5 — the archive's local state, per side, owned by the user: <c>$XDG_STATE_HOME/wsl-care/archive/</c> in the distro,
/// <c>%LOCALAPPDATA%\wsl-care\archive\</c> on Windows, beside the user's own logs. Every file is read under
/// <c>archive.maxStateFileBytes</c>, written atomically and private (0600); the index key is 32 random bytes made on first use.
/// </summary>
public sealed class ArchiveState(IHostPaths paths, IFileSystem files)
{
    public const int Version = 1;
    private const int KeyBytes = 32;

    public string Folder { get; } = Path.Combine(Path.GetDirectoryName(paths.UserLogDirectory) ?? paths.UserLogDirectory, "archive");

    public string LockFile => Join("archive.lock");

    private string Join(string name) => Path.Combine(Folder, name);

    private DeletionScope Scope => new(Folder, "A13");

    private static int Cap => Tuning.Current.Int(ConfigKeys.Archive.MaxStateFileBytes);

    public void EnsureFolder() => files.CreateDirectory(Folder);

    public InflightFile Inflight() => Read(Join("inflight.json"), WslCareJsonContext.Compact.InflightFile) ?? new InflightFile(Version, []);

    public string WriteInflight(InflightFile file) => Write(Join("inflight.json"), JsonSerializer.SerializeToUtf8Bytes(file, WslCareJsonContext.Compact.InflightFile));

    /// <summary>The base's recorded mount; <c>null</c> when none is recorded or it does not read whole (see <see cref="BaseRecorded"/>).</summary>
    public BaseRecord? Base() => Read(BaseFile, WslCareJsonContext.Compact.BaseRecord) is { Folder: not null, MountType: not null, MountSource: not null, MountPoint: not null } whole ? whole : null;

    /// <summary>Where the base's mount is recorded.</summary>
    public string BaseFile => Join("base.json");

    /// <summary>Whether a record exists — review m4: one that exists and does not read is a refusal, never silently recorded again.</summary>
    public bool BaseRecorded => files.FileExists(BaseFile);

    public string WriteBase(BaseRecord record) => Write(Join("base.json"), JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Compact.BaseRecord));

    public LastRunRecord? LastRun() => Read(Join("last-run.json"), WslCareJsonContext.Compact.LastRunRecord);

    public string WriteLastRun(LastRunRecord record) => Write(Join("last-run.json"), JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Compact.LastRunRecord));

    public LockHolderRecord? Holder() => Read(Join("holder.json"), WslCareJsonContext.Compact.LockHolderRecord);

    public string WriteHolder(LockHolderRecord record) => Write(Join("holder.json"), JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Compact.LockHolderRecord));

    /// <summary>The archive's table of contents (D5): per agent and month the sessions, files and bytes this side archived and the
    /// newest archive time — what E10's AI-agents columns show without reading a network share.</summary>
    public SummaryFile Summary() => Read(Join("summary.json"), WslCareJsonContext.Compact.SummaryFile) ?? new SummaryFile(Version, []);

    /// <summary>One archived session counted into its agent's month; empty when written.</summary>
    public string Count(string agent, string month, int files, long bytes, DateTimeOffset at)
    {
        var rows = Summary().Months.ToList();
        var row = rows.FirstOrDefault(r => r.Agent == agent && r.Month == month) ?? new SummaryRow(agent, month, 0, 0, 0, at);
        rows.RemoveAll(r => r.Agent == agent && r.Month == month);
        rows.Add(row with { Sessions = row.Sessions + 1, Files = row.Files + files, Bytes = row.Bytes + bytes, NewestUtc = at > row.NewestUtc ? at : row.NewestUtc });
        return Write(Join("summary.json"), JsonSerializer.SerializeToUtf8Bytes(new SummaryFile(Version, [.. rows.OrderBy(r => r.Agent, StringComparer.Ordinal).ThenBy(r => r.Month, StringComparer.Ordinal)]), WslCareJsonContext.Compact.SummaryFile));
    }

    /// <summary>The entries restored into the agent folders and not archived again (plan §15r D5, D6, review M10) — what lets a restored
    /// session be archived again as an EVENT only, without its bytes. An entry with a field missing is skipped.</summary>
    public IReadOnlyList<RestoredEntry> Restored() =>
        Read(Join(RestoredName), WslCareJsonContext.Compact.RestoredFile) is { Entries: { } entries }
            ? [.. entries.Where(e => e is { EntryId: not null, Agent: not null, Key: not null, Month: not null })]
            : [];

    /// <summary>The entry added (replacing one of the same id); empty when written.</summary>
    public string AddRestored(RestoredEntry entry) =>
        WriteRestored([.. Restored().Where(e => e.EntryId != entry.EntryId), entry]);

    /// <summary>The entry removed — it was archived again; empty when written.</summary>
    public string DropRestored(string entryId) => WriteRestored([.. Restored().Where(e => e.EntryId != entryId)]);

    private string WriteRestored(IReadOnlyList<RestoredEntry> entries) =>
        Write(Join(RestoredName), JsonSerializer.SerializeToUtf8Bytes(new RestoredFile(Version, entries), WslCareJsonContext.Compact.RestoredFile));

    private const string RestoredName = "restored.json";

    /// <summary>The side's MAC key when one exists — never made (a reader such as <c>archive list</c> writes nothing); empty otherwise,
    /// and every line then reads unverified.</summary>
    public byte[] ExistingIndexKey() =>
        files.ReadRegularFile(Join("index.key"), KeyBytes * 4) is FileReadResult.Content { Bytes.Length: KeyBytes } key ? key.Bytes : [];

    /// <summary>The side's MAC key; made (32 random bytes, 0600) when there is none. A key that cannot be read makes every line
    /// unverified — never invalid (D4).</summary>
    public byte[] IndexKey()
    {
        var path = Join("index.key");
        return files.ReadRegularFile(path, KeyBytes * 4) switch
        {
            FileReadResult.Content { Bytes.Length: KeyBytes } key => key.Bytes,
            FileReadResult.Missing => NewKey(path),
            _ => [],
        };
    }

    private byte[] NewKey(string path)
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        return files.WritePrivateFileAtomically(path, key, Scope).IsAllowed ? key : [];
    }

    private T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class =>
        files.ReadRegularFile(path, Cap) is FileReadResult.Content content ? Parsed(content.Bytes, type) : null;

    private static T? Parsed<T>(byte[] bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Written atomically and private; empty when written, why not otherwise (a file past its cap is refused).</summary>
    private string Write(string path, byte[] bytes) =>
        bytes.Length > Cap ? $"{path} would be {bytes.Length} bytes, past {ConfigKeys.Archive.MaxStateFileBytes.Name} ({Cap})"
        : files.WritePrivateFileAtomically(path, bytes, Scope) is DeletionVerdict.Refused refused ? refused.Reason
        : string.Empty;
}

/// <summary>One agent's month in <c>summary.json</c>.</summary>
public sealed record SummaryRow(string Agent, string Month, int Sessions, int Files, long Bytes, DateTimeOffset NewestUtc);

public sealed record SummaryFile(int V, IReadOnlyList<SummaryRow> Months);

/// <summary>One entry restored into an agent folder: where its index line is (the agent and the month) and the unit it is.</summary>
public sealed record RestoredEntry(string EntryId, string Agent, string Key, string Month);

/// <summary><c>restored.json</c> (plan §15r D5): the entries whose latest status is <c>restored</c>.</summary>
public sealed record RestoredFile(int V, IReadOnlyList<RestoredEntry> Entries);
