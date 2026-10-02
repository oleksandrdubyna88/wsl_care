using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;

namespace WslCare.Core.Folders;

/// <summary>One folder the daily walk measures.</summary>
/// <param name="CountOnlyUnder">Only files below a folder of one of these names count (<c>bin</c>, <c>obj</c>); empty = all.</param>
/// <param name="NeverEnter">Folders of these names are not walked at all.</param>
public sealed record FolderTarget(string Id, string Path, IReadOnlySet<string> CountOnlyUnder, IReadOnlySet<string> NeverEnter);

/// <summary>
/// The daily folder sizes of plan §4.4 and the A8 / A9 figures of §4.3 — bounded walks through
/// <see cref="IFileSystem.MeasureTree"/> (links never followed, contents never read, a ceiling per folder), plus
/// <c>snap list --all</c> for A9's disabled revisions, whose sizes are one stat each.
/// </summary>
/// <remarks>
/// <para><b>Once a day</b> ("because it is expensive", plan §4.4): a run measures them when the newest recorded sample
/// is older than <see cref="Interval"/>, and the other runs of the day carry none — readers take the newest line that
/// does (<see cref="LastFullRun"/>), with its age.</para>
/// <para><b>AI-agent folders are not walked here.</b> <c>~/.cache</c> holds Antigravity's cache, and its size is part
/// of <c>~/.cache</c>'s — a size, which plan §4.6 allows; nothing is read, moved or deleted.</para>
/// <para>Under the root timer <c>$HOME</c> is root's; whose home the daily walk reads is the installer's decision (E4.S1).</para>
/// </remarks>
public sealed class FolderSizes(IFileSystem files, ICommandRunner commands, TimeProvider clock)
{
    /// <summary>A run measures the folders when the newest recorded sample is older than this.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    /// <summary>One folder's ceiling: two million entries or two minutes, whichever comes first.</summary>
    public static readonly TreeLimits Limits = new(2_000_000, TimeSpan.FromMinutes(2));

    public const string NpmCache = "npm-cache";
    public const string AptCache = "apt-cache";
    public const string SnapDisabled = "snap-disabled";

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether a run at <paramref name="now"/> measures the folders, given the newest recorded sample.</summary>
    public static bool Due(Reading<AgedPart<FolderSizesSample>> last, DateTimeOffset now) =>
        last is not Reading<AgedPart<FolderSizesSample>>.Available { Value: var part } || now - part.SampledAt >= Interval;

    /// <summary>Plan §4.4's list and A8 / A9, for a Linux layout.</summary>
    public static IReadOnlyList<FolderTarget> Targets(LinuxHostPaths paths)
    {
        var rules = paths.Rules;
        var home = paths.Home;
        return
        [
            new(NpmCache, rules.Join(home, ".npm"), None, None),
            new(AptCache, paths.AptCacheDirectory, None, None),
            new("git-worktrees", rules.Join(home, "git", "_wt"), None, None),
            new("nuget-packages", rules.Join(home, ".nuget", "packages"), None, None),
            new("user-cache", rules.Join(home, ".cache"), None, None),
            new("vscode-server", rules.Join(home, ".vscode-server"), None, None),
            new("git-build-output", rules.Join(home, "git"), new HashSet<string>(["bin", "obj"], StringComparer.Ordinal), new HashSet<string>(["node_modules", ".git"], StringComparer.Ordinal)),
        ];
    }

    public async Task<FolderSizesSample> MeasureAsync(LinuxHostPaths paths, CancellationToken cancellationToken)
    {
        var sizes = Targets(paths).Select(t => Measure(t, cancellationToken)).ToList();
        sizes.Add(await SnapAsync(paths, cancellationToken).ConfigureAwait(false));
        return new FolderSizesSample(clock.GetUtcNow(), sizes);
    }

    private FolderSize Measure(FolderTarget target, CancellationToken cancellationToken) =>
        files.MeasureTree(target.Path, Limits, target.CountOnlyUnder, target.NeverEnter, cancellationToken) switch
        {
            TreeMeasure.Measured m => new FolderSize(target.Id, target.Path, m.Bytes, m.Files, m.Complete, string.Empty),
            TreeMeasure.Missing => new FolderSize(target.Id, target.Path, 0, 0, true, $"{target.Path} does not exist"),
            TreeMeasure.Unreadable u => new FolderSize(target.Id, target.Path, 0, 0, false, u.Reason),
            _ => throw new System.Diagnostics.UnreachableException("TreeMeasure is a closed set"),
        };

    /// <summary>A9's disabled snap revisions: <c>snap list --all</c> names them, each one's size is its
    /// <c>{name}_{revision}.snap</c> file under <c>/var/lib/snapd/snaps</c>.</summary>
    private async Task<FolderSize> SnapAsync(LinuxHostPaths paths, CancellationToken cancellationToken)
    {
        var listed = await ToolAnswers.RunAsync(commands, HealthCommands.SnapList, cancellationToken).ConfigureAwait(false);
        if (listed is not Reading<string>.Available { Value: var stdout })
        {
            return new FolderSize(SnapDisabled, paths.SnapFilesDirectory, 0, 0, false, listed.ReasonOrEmpty);
        }

        var sizes = HealthParsers.DisabledSnapRevisions(stdout)
            .Select(r => files.FileSize(paths.Rules.Join(paths.SnapFilesDirectory, $"{r.Name}_{r.Revision}.snap")))
            .ToList();
        var unsized = sizes.Count(s => s is not FileSizeResult.Measured);
        return new FolderSize(SnapDisabled, paths.SnapFilesDirectory, sizes.OfType<FileSizeResult.Measured>().Sum(m => m.Bytes), sizes.Count, unsized == 0, string.Empty);
    }
}
