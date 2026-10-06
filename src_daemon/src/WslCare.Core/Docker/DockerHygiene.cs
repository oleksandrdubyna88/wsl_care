using WslCare.Core.Config;
using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Docker;

/// <summary>A container whose json-file log has no <c>max-size</c>, and how big that log is — when this side can see it.</summary>
public sealed record UnboundedLog(string Container, string State, Reading<long> LogBytes);

/// <summary>Docker Desktop's builder garbage collection as <c>daemon.json</c> configures it.</summary>
/// <param name="Present">Whether <c>daemon.json</c> has a <c>builder.gc</c> section at all.</param>
/// <param name="Enabled"><c>builder.gc.enabled</c> as written (<c>true</c>, <c>false</c>), or empty.</param>
/// <param name="DefaultKeepStorage"><c>builder.gc.defaultKeepStorage</c> as written (<c>20GB</c>), or empty.</param>
public sealed record BuilderGc(bool Present, string Enabled, string DefaultKeepStorage);

/// <summary>What a <c>docker-container</c> buildx builder leaves: its <c>buildx_buildkit_*</c> container and
/// its <c>buildx_buildkit_*_state</c> volume.</summary>
/// <param name="Kind"><c>container</c> or <c>volume</c>.</param>
/// <param name="State">The container's state, or <c>attached</c> / <c>unattached</c> for a volume.</param>
public sealed record BuildkitLeftover(string Name, string Kind, string State, Reading<long> Bytes);

/// <summary>The Docker hygiene audit of plan §4.5 — report only, nothing here acts.</summary>
public sealed record DockerHygieneAudit(
    Reading<IReadOnlyList<UnboundedLog>> UnboundedLogs,
    Reading<BuilderGc> BuilderGc,
    Reading<IReadOnlyList<BuildkitLeftover>> Buildkit);

/// <summary>
/// Plan §4.5 "Docker hygiene audit": containers logging to <c>json-file</c> without <c>max-size</c> and their
/// log sizes; whether Docker Desktop's <c>daemon.json</c> (the Windows-side file, not <c>/etc/docker</c>) has a
/// builder GC; forgotten <c>docker-container</c> buildx builders and their state volumes. Read-only: the
/// inputs are the snapshot already taken, one <c>stat</c> per unbounded log, and one file read.
/// </summary>
public static class DockerHygiene
{
    private const string BuildkitPrefix = "buildx_buildkit_";
    private const string StateSuffix = "_state";

    /// <summary>Docker Desktop's <c>daemon.json</c> is a small JSON object.</summary>
    private static int MaxDaemonJsonBytes => Tuning.Current.Int(ConfigKeys.Docker.MaxDaemonJsonBytes);

    public static DockerHygieneAudit Audit(DockerSnapshot snapshot, IHostPaths paths, IFileSystem files) =>
        Audit(snapshot, paths, files, paths.DockerDesktopConfigFile);

    /// <summary>The audit with Docker Desktop's <c>daemon.json</c> at <paramref name="dockerDesktopConfigFile"/> — inside the
    /// distro, the Windows profile the full run's clock probe found, seen through <c>/mnt</c> (E2.S3); empty = unknown.</summary>
    public static DockerHygieneAudit Audit(DockerSnapshot snapshot, IHostPaths paths, IFileSystem files, string dockerDesktopConfigFile) =>
        new(
            snapshot.Details.Map<IReadOnlyList<UnboundedLog>>(details => [.. details.Where(d => d.UnboundedLog).Select(d => new UnboundedLog(d.Name, d.State, LogSize(d.LogPath, paths, files)))]),
            ReadBuilderGc(dockerDesktopConfigFile, files),
            Reading.Combine(snapshot.Inventory, snapshot.Dangling, Buildkit));

    private static Reading<long> LogSize(string logPath, IHostPaths paths, IFileSystem files)
    {
        var visible = logPath.Length == 0 ? string.Empty : paths.DistroPath(logPath);
        if (visible.Length == 0)
        {
            return Reading.Missing<long>(logPath.Length == 0 ? "Docker reported no log path" : "the Windows binary does not read the distro's filesystem; the Linux binary reports log sizes");
        }

        return files.FileSize(visible) switch
        {
            FileSizeResult.Measured m => Reading.Of(m.Bytes),
            FileSizeResult.Missing => Reading.Missing<long>($"{logPath} is not on this filesystem: Docker Desktop keeps container logs inside its own VM"),
            FileSizeResult.Unreadable u => Reading.Missing<long>($"{logPath} could not be measured: {u.Reason}"),
            _ => throw new System.Diagnostics.UnreachableException("FileSizeResult is a closed set"),
        };
    }

    private static Reading<BuilderGc> ReadBuilderGc(string file, IFileSystem files)
    {
        if (file.Length == 0)
        {
            return Reading.Missing<BuilderGc>("Docker Desktop's daemon.json is on the Windows side (%USERPROFILE%\\.docker\\daemon.json); wsl-care.exe reads it, and the distro once a full run has found the Windows profile");
        }

        // The Windows profile through drvfs (plan §15q R1.1, review M2): no link, no wait, a cap — and no owner or mode check.
        return files.ReadNoFollowFile(file, MaxDaemonJsonBytes) switch
        {
            FileReadResult.Content content => ParseBuilderGc(content.Bytes, file),
            FileReadResult.Missing => Reading.Of(new BuilderGc(false, string.Empty, string.Empty)),
            FileReadResult.Unreadable u => Reading.Missing<BuilderGc>($"{file} could not be read: {u.Reason}"),
            _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
        };
    }

    private static Reading<BuilderGc> ParseBuilderGc(byte[] bytes, string file)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var gc = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("builder", out var builder)
                && builder.ValueKind == JsonValueKind.Object && builder.TryGetProperty("gc", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
            return Reading.Of(gc.ValueKind == JsonValueKind.Object
                ? new BuilderGc(true, DockerText.Field(gc, "enabled"), DockerText.Field(gc, "defaultKeepStorage"))
                : new BuilderGc(false, string.Empty, string.Empty));
        }
        catch (JsonException e)
        {
            return Reading.Missing<BuilderGc>($"{file} is not JSON: {e.Message}");
        }
    }

    private static IReadOnlyList<BuildkitLeftover> Buildkit(DockerInventory inventory, IReadOnlySet<string> dangling) =>
    [
        .. inventory.Containers.Where(c => c.Name.StartsWith(BuildkitPrefix, StringComparison.Ordinal))
            .Select(c => new BuildkitLeftover(c.Name, "container", c.State, c.SizeBytes)),
        .. inventory.Volumes.Where(v => v.Name.StartsWith(BuildkitPrefix, StringComparison.Ordinal) && v.Name.EndsWith(StateSuffix, StringComparison.Ordinal))
            .Select(v => new BuildkitLeftover(v.Name, "volume", dangling.Contains(v.Name) ? "unattached" : "attached", v.SizeBytes)),
    ];
}
