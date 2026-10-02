using System.Diagnostics;

using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;

namespace WslCare.Core.Preview;

/// <summary>
/// <c>preview --all</c> (plan §6): one Docker snapshot (read commands only), the first-sighting record updated
/// with it, the cleanup rows, the kept named volumes, Docker's totals and the hygiene audit. It starts no
/// cleanup and writes nothing but <c>volume-seen.json</c> — and that only when this process may write the state
/// directory (plan §15b #3; see <see cref="VolumeSeenStore"/>).
/// </summary>
public static class PreviewRun
{
    public static async Task<PreviewReport> RunAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, TimeProvider clock, ConfigLoadResult loaded, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var snapshot = await new DockerCollector(new DockerCli(commands), clock).CollectAsync(cancellationToken).ConfigureAwait(false);
        var seen = Record(snapshot, new VolumeSeenStore(paths, files));
        var preview = CleanupPreviews.Build(snapshot, seen.Record, loaded.Config, snapshot.SampledAt);
        var hygiene = DockerHygiene.Audit(snapshot, paths, files);
        return PreviewReports.From(paths.Side, snapshot, preview, hygiene, seen, loaded, watch.Elapsed);
    }

    /// <summary>The stored record observed against this snapshot and written back — or, when Docker did not
    /// list its volumes, left exactly as it was (an outage must not drop every first sighting).</summary>
    private static VolumeSeenOutcome Record(DockerSnapshot snapshot, VolumeSeenStore store)
    {
        var load = store.Read();
        if (snapshot.UnattachedAnonymous is not Collectors.Reading<IReadOnlyList<string>>.Available { Value: var names })
        {
            return new VolumeSeenOutcome(store.File, load.Record, new VolumeSeenWrite.NotWritten($"not recorded: Docker did not list its volumes ({snapshot.UnattachedAnonymous.ReasonOrEmpty})"), load.Problem);
        }

        var observed = load.Record.Observe(names, snapshot.SampledAt);
        return new VolumeSeenOutcome(store.File, observed, store.TryWrite(observed), load.Problem);
    }
}
