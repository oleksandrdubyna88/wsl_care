using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Status;

namespace WslCare.Core.Watch;

/// <summary>One lettered volume as <c>volumes.json</c> keeps it. A figure that was not read is <c>available: false</c> with its reason — never 0.</summary>
public sealed record SampledVolume(string Letter, string FileSystem, string Label, ByteFigure Size, ByteFigure Free);

/// <summary>
/// <c>%LOCALAPPDATA%\wsl-care\volumes.json</c> (E19.S2): the Windows watch's native reading of the lettered volumes, overwritten every
/// <c>mcpWatchdog.periodMinutes</c>. About 26 volumes at 0.15 KB; a write killed halfway leaves the previous file (atomic rename) and its
/// <see cref="SampledAt"/> says how old that is.
/// </summary>
/// <param name="SchemaVersion">The file's schema; a version this build does not know is unreadable, never guessed at.</param>
/// <param name="SampledAt">When the watch read the drives (its clock, UTC) — the age a status judges the sample with.</param>
public sealed record VolumeSample(int SchemaVersion, DateTimeOffset SampledAt, IReadOnlyList<SampledVolume> Volumes)
{
    public static VolumeSample Of(DateTimeOffset at, IReadOnlyList<WindowsVolume> volumes) =>
        new(Core.SchemaVersion.Current, at, [.. volumes.Select(Entry)]);

    /// <summary>The sample as the volume table the rules judge — the disk number is unread, as the native reading leaves it.</summary>
    public IReadOnlyList<WindowsVolume> ToVolumes() => [.. Volumes.Select(Volume)];

    private static SampledVolume Entry(WindowsVolume v) => new(v.Letter, v.FileSystem, v.Label, StatusReports.Bytes(v.SizeBytes), StatusReports.Bytes(v.FreeBytes));

    private static WindowsVolume Volume(SampledVolume v) =>
        new(v.Letter, Reading.Missing<int>(WindowsVolumes.NotReadNatively), v.FileSystem, Figure(v.Size), Figure(v.Free), v.Label);

    private static Reading<long> Figure(ByteFigure figure) =>
        figure is { Available: true, Bytes: { } bytes } ? Reading.Of(bytes) : Reading.Missing<long>(figure.Reason ?? "the sample gives no reason");
}

/// <summary>
/// Reads and writes <c>volumes.json</c> under the state directory (E19.S2) — written atomically through <see cref="IFileSystem.WriteFileAtomically"/>
/// and the deletion policy's <see cref="DeletionScope"/> for the state directory (a reader sees the old sample or the new, never half), read back
/// with a REASON whenever it cannot be: a missing, unreadable, foreign-version or half-shaped file is never an empty table. The pattern of
/// <c>VolumeSeenStore</c> and <c>WorktreeReportStore</c>.
/// </summary>
public sealed class VolumeSampleStore(IHostPaths paths, IFileSystem files)
{
    public const string FileName = "volumes.json";

    private const string Action = "volumes";

    /// <summary>The cap the file is READ back under (<c>records.maxStateFileBytes</c>).</summary>
    private static int MaxBytes => Tuning.Current.Int(ConfigKeys.Records.MaxStateFileBytes);

    public string File => paths.Rules.Join(paths.StateDirectory, FileName);

    public Reading<VolumeSample> Read() => files.ReadRegularFile(File, MaxBytes) switch
    {
        FileReadResult.Content content => Parse(content.Bytes),
        FileReadResult.Missing => Reading.Missing<VolumeSample>($"{File} does not exist"),
        FileReadResult.Unreadable unreadable => Reading.Missing<VolumeSample>($"{File} could not be read: {unreadable.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    private Reading<VolumeSample> Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.VolumeSample) is { } sample
                ? Checked(sample)
                : Reading.Missing<VolumeSample>($"{File} is not a volume sample: it holds nothing");
        }
        catch (JsonException e)
        {
            return Reading.Missing<VolumeSample>($"{File} is not a volume sample: {e.Message}");
        }
    }

    /// <summary>A deserialiser does not run a record's defaults: an omitted list is null. The sample is handed to a status only when everything the
    /// status reads is there.</summary>
    private Reading<VolumeSample> Checked(VolumeSample sample) =>
        sample.SchemaVersion != Core.SchemaVersion.Current ? Reading.Missing<VolumeSample>($"{File} has schema {sample.SchemaVersion}; this build reads schema {Core.SchemaVersion.Current}")
        : Lacks(sample) is { Length: > 0 } what ? Reading.Missing<VolumeSample>($"{File} is not a volume sample: it lacks {what}")
        : Reading.Of(sample);

    private static string Lacks(VolumeSample sample) =>
        sample.Volumes is null ? "its volumes" : sample.Volumes.Select(LacksInVolume).FirstOrDefault(m => m.Length > 0) ?? string.Empty;

    private static string LacksInVolume(SampledVolume? volume) => volume switch
    {
        null => "a volume",
        { Letter: null } or { FileSystem: null } or { Label: null } or { Size: null } or { Free: null } => "a field of a volume",
        _ => string.Empty,
    };

    /// <summary>Empty when written; otherwise why not. A write that cannot happen is a reason, never an exception.</summary>
    public string TryWrite(VolumeSample sample)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(sample, WslCareJsonContext.Default.VolumeSample);
            return files.WriteFileAtomically(File, bytes, new DeletionScope(paths.StateDirectory, Action)) is DeletionVerdict.Refused refused
                ? $"the deletion policy refused {File}: {refused.Reason}"
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{File} could not be written: {e.Message}";
        }
    }
}

/// <summary>What one Windows watch run did with the volume sample (E19.S2) — a closed set.</summary>
public abstract record VolumeSampleOutcome
{
    private VolumeSampleOutcome()
    {
    }

    /// <summary>This run takes no volume sample: the distro's watch, or a run that found the lock busy.</summary>
    public sealed record NotTaken : VolumeSampleOutcome;

    /// <summary>The sample was written.</summary>
    public sealed record Written(int Volumes, string File) : VolumeSampleOutcome;

    /// <summary>The sample was not written, and why (the table could not be read, or the file could not be written). The run goes on.</summary>
    public sealed record NotWritten(string Reason) : VolumeSampleOutcome;

    /// <summary>Reads the drives and writes the sample stamped <paramref name="now"/>. A table that could not be read writes NOTHING (an empty list
    /// would be read as "no volumes"); the previous file stays, and its age says how old it is.</summary>
    public static VolumeSampleOutcome Take(IWindowsVolumes volumes, VolumeSampleStore store, DateTimeOffset now) => volumes.Read() switch
    {
        Reading<IReadOnlyList<WindowsVolume>>.Available { Value: var list } => Write(store, VolumeSample.Of(now, list)),
        var unread => new NotWritten($"the drives could not be read: {unread.ReasonOrEmpty}"),
    };

    private static VolumeSampleOutcome Write(VolumeSampleStore store, VolumeSample sample) =>
        store.TryWrite(sample) is { Length: > 0 } why ? new NotWritten(why) : new Written(sample.Volumes.Count, store.File);
}

/// <summary>The wire shape of what the Windows watch did with the volume sample, in <c>watch --json</c>.</summary>
/// <param name="Written">Whether <c>volumes.json</c> was written this run.</param>
/// <param name="Volumes">How many volumes it holds; absent when it was not written.</param>
/// <param name="Reason">Why it was not written; absent when it was.</param>
public sealed record VolumeSampleReport(bool Written, int? Volumes, string? Reason)
{
    /// <summary>Absent for a run that took no sample.</summary>
    public static VolumeSampleReport? From(VolumeSampleOutcome outcome) => outcome switch
    {
        VolumeSampleOutcome.Written written => new(true, written.Volumes, null),
        VolumeSampleOutcome.NotWritten notWritten => new(false, null, notWritten.Reason),
        _ => null,
    };
}
