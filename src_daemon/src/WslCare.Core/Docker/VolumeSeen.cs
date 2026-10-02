using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Docker;

/// <summary>When the daemon first saw one anonymous volume with no container referring to it.</summary>
public sealed record VolumeSighting(string Name, DateTimeOffset FirstSeen);

/// <summary>
/// <c>volume-seen.json</c> (plan §5 "Volume age", §15 #0 and #4): A4's age source. Docker keeps no "unused
/// since", and <c>docker volume prune</c> has no <c>until</c> filter, so the daemon records the first time it
/// saw each anonymous volume UNATTACHED; A4 may then remove only volumes seen unattached at least
/// <c>volumes.anonymousOlderThanDays</c> ago.
/// </summary>
/// <remarks>
/// <para>Keyed by volume name. <see cref="Observe"/> keeps the first sighting of a name still unattached,
/// adds the new ones at <c>now</c>, and DROPS every name that is no longer an unattached anonymous volume —
/// removed, or attached again — so the file is bounded by what Docker lists (a volume that comes back gets a
/// new first sighting; its age restarts, which errs on the side of keeping it).</para>
/// <para>Nothing here is trusted across a crash for deletion (plan §15a #0): A4 re-lists at run time and only
/// READS the age from here.</para>
/// </remarks>
public sealed record VolumeSeenRecord(int SchemaVersion, DateTimeOffset UpdatedAt, IReadOnlyList<VolumeSighting> Volumes)
{
    public static readonly VolumeSeenRecord Empty = new(Core.SchemaVersion.Current, DateTimeOffset.UnixEpoch, []);

    /// <summary>The record after one look at Docker's unattached anonymous volumes — pure.</summary>
    public VolumeSeenRecord Observe(IEnumerable<string> unattachedAnonymous, DateTimeOffset now)
    {
        var known = (Volumes ?? []).Where(v => v?.Name is not null).GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(v => v.FirstSeen), StringComparer.Ordinal);
        var current = unattachedAnonymous.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new(Core.SchemaVersion.Current, now, [.. current.Select(name => new VolumeSighting(name, known.TryGetValue(name, out var first) ? first : now))]);
    }

    /// <summary>When <paramref name="name"/> was first seen unattached; unavailable when it is not in the record.</summary>
    public Reading<DateTimeOffset> FirstSeen(string name) =>
        (Volumes ?? []).FirstOrDefault(v => string.Equals(v?.Name, name, StringComparison.Ordinal)) is { } sighting
            ? Reading.Of(sighting.FirstSeen)
            : Reading.Missing<DateTimeOffset>($"{name} has no first sighting recorded");
}

/// <summary>What reading <c>volume-seen.json</c> produced: the record (empty when there is none), and why it
/// could not be read when it could not — an unreadable record is reported, never silently taken for empty.</summary>
public sealed record VolumeSeenLoad(VolumeSeenRecord Record, string Problem);

/// <summary>Whether the record was written, or why not.</summary>
public abstract record VolumeSeenWrite
{
    private VolumeSeenWrite()
    {
    }

    public sealed record Written : VolumeSeenWrite;

    public sealed record NotWritten(string Reason) : VolumeSeenWrite;
}

/// <summary>
/// Reads and writes <c>{state}/volume-seen.json</c> through <see cref="IFileSystem"/> — written atomically
/// (temporary file + rename, judged by the deletion policy inside the state directory).
/// </summary>
/// <remarks>
/// <b>Who writes it (plan §15b #3).</b> State under <c>/var/lib/wsl-care</c> is written by root only. This
/// class does not decide privilege by an id check of its own: it ATTEMPTS the write and lets the operating
/// system answer. On the installed layout the state directory is <c>root:root 0755</c> (E4.S1's installer),
/// so only root succeeds; an unprivileged run (the extension's <c>preview</c>) gets the operating system's
/// refusal and reports <see cref="VolumeSeenWrite.NotWritten"/> — <i>read-only</i> — having written nothing,
/// and uses the record it read plus "first seen now" for new volumes, which can only make A4 select FEWER.
/// A sandbox under <c>WSL_CARE_ROOT</c> belongs to the test user and is therefore writable — the privileged
/// case; the unwritable case is tested with the state directory denied.
/// </remarks>
public sealed class VolumeSeenStore(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "volume-seen.json";
    private const string Action = "volume-seen";

    public string File => paths.Rules.Join(paths.StateDirectory, FileName);

    public VolumeSeenLoad Read() => files.ReadFile(File) switch
    {
        FileReadResult.Content content => Parse(content.Bytes),
        FileReadResult.Missing => new VolumeSeenLoad(VolumeSeenRecord.Empty, string.Empty),
        FileReadResult.Unreadable u => new VolumeSeenLoad(VolumeSeenRecord.Empty, $"{File} could not be read: {u.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    public VolumeSeenWrite TryWrite(VolumeSeenRecord record)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Default.VolumeSeenRecord);
            return files.WriteFileAtomically(File, bytes, new DeletionScope(paths.StateDirectory, Action)) switch
            {
                DeletionVerdict.Refused refused => new VolumeSeenWrite.NotWritten($"read-only: {refused.Reason}"),
                _ => new VolumeSeenWrite.Written(),
            };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return new VolumeSeenWrite.NotWritten($"read-only: {paths.StateDirectory} is not writable by this process ({e.Message}); the full run as root records first sightings");
        }
    }

    private VolumeSeenLoad Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.VolumeSeenRecord) is { } record
                ? new VolumeSeenLoad(record with { Volumes = record.Volumes ?? [] }, string.Empty)
                : new VolumeSeenLoad(VolumeSeenRecord.Empty, $"{File} holds no record");
        }
        catch (JsonException e)
        {
            return new VolumeSeenLoad(VolumeSeenRecord.Empty, $"{File} is not a volume-seen record: {e.Message}");
        }
    }
}
