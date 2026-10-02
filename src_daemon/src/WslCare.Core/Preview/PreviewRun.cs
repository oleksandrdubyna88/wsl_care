using System.Diagnostics;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;

namespace WslCare.Core.Preview;

/// <summary>
/// <c>preview --all</c> (plan §6): one Docker snapshot (read commands only), the first-sighting record observed
/// against it IN MEMORY, the cleanup rows, the kept named volumes, Docker's totals and the hygiene audit. It starts no
/// cleanup and writes NOTHING — not <c>volume-seen.json</c> either, whatever this process's privilege (plan §15b #3:
/// <c>preview</c> only reads the state; gate finding #3/#6/#10 found a root preview recording sightings). Only the full
/// run (<c>collect</c>, <see cref="CollectAsync"/> with <see cref="PreviewExtras.MayRecord"/>) records them.
/// </summary>
public static class PreviewRun
{
    /// <summary>Why a preview's first sightings are not written: it is a look, never a record.</summary>
    public const string PreviewOnlyReads = "read-only: preview only reads volume-seen.json; the full run (collect) records first sightings";

    /// <summary><c>preview --all</c>: the A8 / A9 folders and Docker Desktop's <c>daemon.json</c> come from the newest
    /// full run that measured them (the folders) or found the Windows profile (the file).</summary>
    public static async Task<PreviewReport> RunAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, TimeProvider clock, ConfigLoadResult loaded, CancellationToken cancellationToken)
    {
        var last = LastFullRun.Read(paths, files, clock);
        var profile = last.WindowsClock.Map(c => c.Value.Profile).ValueOr(string.Empty);
        var extras = new PreviewExtras(last.Folders, WindowsProfiles.DockerDesktopConfig(paths, files, profile)) { MayRecord = false, NotRecordedReason = PreviewOnlyReads };
        return (await CollectAsync(paths, files, commands, clock, loaded, extras, cancellationToken).ConfigureAwait(false)).Report;
    }

    /// <summary>The preview with its parts — what <c>collect</c> evaluates its Docker thresholds over.</summary>
    public static async Task<PreviewResult> CollectAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, TimeProvider clock, ConfigLoadResult loaded, PreviewExtras extras, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var snapshot = await new DockerCollector(new DockerCli(commands), clock).CollectAsync(cancellationToken).ConfigureAwait(false);
        var seen = Record(snapshot, new VolumeSeenStore(paths, files), extras);
        var preview = CleanupPreviews.Build(snapshot, seen.Record, loaded.Config, snapshot.SampledAt, extras.Folders);
        var hygiene = DockerHygiene.Audit(snapshot, paths, files, extras.DockerDesktopConfigFile);
        return new PreviewResult(snapshot, preview, PreviewReports.From(paths.Side, snapshot, preview, hygiene, seen, loaded, watch.Elapsed));
    }

    /// <summary>The stored record observed against this snapshot and written back — or, when Docker did not
    /// list its volumes, left exactly as it was (an outage must not drop every first sighting).</summary>
    private static VolumeSeenOutcome Record(DockerSnapshot snapshot, VolumeSeenStore store, PreviewExtras extras)
    {
        var load = store.Read();
        if (snapshot.UnattachedAnonymous is not Collectors.Reading<IReadOnlyList<string>>.Available { Value: var names })
        {
            return new VolumeSeenOutcome(store.File, load.Record, new VolumeSeenWrite.NotWritten($"not recorded: Docker did not list its volumes ({snapshot.UnattachedAnonymous.ReasonOrEmpty})"), load.Problem);
        }

        var observed = load.Record.Observe(names, snapshot.SampledAt);
        var write = extras.MayRecord ? store.TryWrite(observed) : new VolumeSeenWrite.NotWritten(extras.NotRecordedReason);
        return new VolumeSeenOutcome(store.File, observed, write, load.Problem);
    }
}

/// <summary>What a preview takes from outside Docker: the newest folder sample (A8, A9) and where Docker Desktop's
/// <c>daemon.json</c> is seen from this process (empty = unknown).</summary>
public sealed record PreviewExtras(Reading<AgedPart<FolderSizesSample>> Folders, string DockerDesktopConfigFile)
{
    /// <summary>Whether the first sightings may be written: a privileged <c>collect</c> attempts the write and lets the
    /// operating system answer; a read-only <c>collect</c> and every <c>preview</c> write nothing.</summary>
    public bool MayRecord { get; init; } = true;

    /// <summary>What the report says when <see cref="MayRecord"/> is false.</summary>
    public string NotRecordedReason { get; init; } = "read-only: this run may not write the state directory, so it records nothing";
}

/// <summary>A preview and the parts it was built from.</summary>
public sealed record PreviewResult(DockerSnapshot Snapshot, CleanupPreview Preview, PreviewReport Report);
