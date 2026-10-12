using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Scheduler;

/// <summary>Whether this process may be registered as a task's action, and why (or why not).</summary>
/// <param name="Registrable">True only for a verified staged copy.</param>
/// <param name="Digest">The digest its folder names; empty when the path is not a staged copy's.</param>
/// <param name="Version">The version its <c>stage.json</c> names; empty when there is none to read.</param>
/// <param name="Reason">One sentence: what was checked, or the first check that failed.</param>
public sealed record StagedCheck(bool Registrable, string Digest, string Version, string Reason)
{
    /// <summary>When its <c>stage.json</c> says the copy was staged; <see cref="DateTimeOffset.MinValue"/> when there is none to read
    /// (WR-D2's keep rule chooses the newest copy by it, so a sweep reads each <c>stage.json</c> once).</summary>
    public DateTimeOffset StagedAtUtc { get; init; } = DateTimeOffset.MinValue;
}

/// <summary>
/// The staged copies of the Windows binary (PLAN_windows_runner.md §3.2, §3.7): <c>&lt;stagingRoot&gt;\&lt;64-hex digest&gt;\wsl-care.exe</c>
/// with <c>stage.json</c> beside it, the staging root <see cref="WindowsHostPaths.StateDirectory"/> + <c>bin</c> (the shell-folder
/// API's <c>%LOCALAPPDATA%</c>, never the environment's).
/// </summary>
/// <remarks><see cref="Check"/> is what makes a render <c>registrable</c>: this process's own path is a staged copy's — so no task
/// XML can ever name the extension folder (the bundle plan's §4 rule, enforced in the daemon too) — its bytes hash to the digest
/// its folder names, and <c>stage.json</c> beside it names that digest and this build's release. The bytes are read through the
/// archive's no-link open (<see cref="IArchiveFiles.OpenSource"/>: a regular file of this account with one link, no reparse point
/// followed). Checked in that order, cheapest first; the first failure is the reason.</remarks>
public static class StagedCopies
{
    public const string ExeName = "wsl-care.exe";

    /// <summary>The staging folder's name under the state directory.</summary>
    private const string RootName = "bin";

    /// <summary>The staging root of a Windows layout.</summary>
    public static string Root(WindowsHostPaths paths) => paths.Rules.Join(paths.StateDirectory, RootName);

    /// <summary>The release number of a version text: what is before <c>+</c> (the commit the SDK appends), the whole text else —
    /// what <c>stage.json</c>'s <c>version</c> holds and the extension compares with its bundled daemon's.</summary>
    public static string ReleaseOf(string versionText) => versionText.Split('+')[0];

    /// <summary>Whether <paramref name="processPath"/> is a verified staged copy under <paramref name="stagingRoot"/>.</summary>
    /// <param name="ownVersion">This build's version text; <c>stage.json</c> must name its release.</param>
    /// <param name="maxStageBytes">The most bytes of <c>stage.json</c> read (<c>records.maxStateFileBytes</c>).</param>
    public static StagedCheck Check(string stagingRoot, string processPath, string ownVersion, IFileSystem files, IArchiveFiles archive, int maxStageBytes)
    {
        var digest = DigestOf(stagingRoot, processPath);
        return digest.Length == 0
            ? new StagedCheck(false, string.Empty, string.Empty, NotStaged(stagingRoot, processPath))
            : Verified(new Copy(stagingRoot, processPath, digest), ReleaseOf(ownVersion), files, archive, maxStageBytes);
    }

    /// <summary>Whether the folder <paramref name="digest"/> under <paramref name="stagingRoot"/> holds a verified copy of ANY release
    /// (WR-D2's keep rule: an older copy a registered task may still name) — the same checks as <see cref="Check"/> but the release.</summary>
    public static StagedCheck Verify(string stagingRoot, string digest, IFileSystem files, IArchiveFiles archive, int maxStageBytes) =>
        Verified(new Copy(stagingRoot, PathRules.Windows.Join(stagingRoot, digest, ExeName), digest), AnyRelease, files, archive, maxStageBytes);

    /// <summary>The release <see cref="Verify"/> passes: no release is asked of <c>stage.json</c>.</summary>
    private const string AnyRelease = "";

    /// <summary>A path already known to be <c>&lt;root&gt;\&lt;digest&gt;\wsl-care.exe</c>.</summary>
    private sealed record Copy(string Root, string Path, string Digest);

    /// <summary>The copy's <c>stage.json</c> agrees, then its bytes hash to its digest — or the first that does not.</summary>
    private static StagedCheck Verified(Copy copy, string release, IFileSystem files, IArchiveFiles archive, int maxStageBytes)
    {
        var read = StageRecords.Read(files, PathRules.Windows.Join(copy.Root, copy.Digest, StageRecord.FileName), maxStageBytes);
        var version = read is StageRead.Found found ? found.Record.Version : string.Empty;
        var problem = RecordProblem(read, copy.Digest, release) is { Length: > 0 } bad ? bad : HashProblem(archive, copy);
        var check = problem.Length > 0 ? new StagedCheck(false, copy.Digest, version, problem) : VerifiedCopy(copy.Path, copy.Digest, version);
        return read is StageRead.Found { Record.StagedAtUtc: var at } ? check with { StagedAtUtc = at } : check;
    }

    /// <summary>The answer for a copy every check passed — one sentence, built here only (the render's golden builds its registrable
    /// examples through it rather than retyping it).</summary>
    public static StagedCheck VerifiedCopy(string path, string digest, string version) =>
        new(true, digest, version, $"a verified staged copy: {path} hashes to its folder's digest and {StageRecord.FileName} names it and release {version}");

    /// <summary>The digest folder <paramref name="processPath"/> sits in when it is exactly <c>&lt;root&gt;\&lt;64-hex&gt;\wsl-care.exe</c>
    /// (no <c>.</c> or <c>..</c> on the way); empty otherwise. Read from the path alone, nothing hashed: the <c>tasks run</c> start
    /// names the copy it runs from to the sweep this way (the sweep verifies every folder it keeps itself).</summary>
    public static string DigestOf(string stagingRoot, string processPath)
    {
        var rules = PathRules.Windows;
        if (!rules.IsAbsolute(processPath) || PathSpelling.HasBadSegment(processPath))
        {
            return string.Empty;
        }

        var folder = rules.Parent(processPath);
        var name = rules.Segments(folder) is { Count: > 0 } segments ? segments[^1] : string.Empty;
        return IsExeName(rules, processPath) && rules.PathEquals(rules.Parent(folder), stagingRoot) && StageRecords.Sha256Shape().IsMatch(name) ? name : string.Empty;
    }

    private static bool IsExeName(PathRules rules, string path) => rules.Segments(path) is { Count: > 0 } segments && string.Equals(segments[^1], ExeName, rules.Comparison);

    private static string NotStaged(string stagingRoot, string processPath) =>
        processPath.Length == 0
            ? $"this process's own path is not known, so it is not a staged copy (a task runs only {stagingRoot}\\<digest>\\{ExeName})"
            : $"{processPath} is not a staged copy: a task runs only {stagingRoot}\\<digest>\\{ExeName} (tasks stage makes one)";

    /// <summary>Why <c>stage.json</c> does not agree with the folder and this build; empty when it does.</summary>
    private static string RecordProblem(StageRead read, string digest, string release) => read switch
    {
        StageRead.Missing => $"there is no {StageRecord.FileName} beside the copy (tasks stage writes one)",
        StageRead.Refused refused => refused.Why,
        StageRead.Found { Record.Sha256: var sha } when sha != digest => $"{StageRecord.FileName} names digest {sha}, not its folder's {digest}",
        StageRead.Found { Record.Version: var version } when release != AnyRelease && version != release => $"{StageRecord.FileName} names release {version}, not this build's {release}",
        _ => string.Empty,
    };

    /// <summary>Why the copy's bytes are not its digest's; empty when they hash to it.</summary>
    private static string HashProblem(IArchiveFiles archive, Copy copy) => archive.OpenSource(copy.Root, copy.Path) switch
    {
        SourceOpen.Opened opened => Compared(opened.Stream, copy),
        SourceOpen.Gone => $"{copy.Path} is not there to hash",
        SourceOpen.Refused refused => $"{copy.Path} could not be hashed: {refused.Why}",
        _ => $"{copy.Path} could not be hashed",
    };

    /// <summary>The opened copy hashed (the stream is disposed by the hash) and compared with its digest.</summary>
    private static string Compared(Stream stream, Copy copy)
    {
        try
        {
            var sha = ArchiveCopy.Sha256Of(stream);
            return sha == copy.Digest ? string.Empty : $"{copy.Path} hashes to {sha}, not its folder's digest {copy.Digest}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{copy.Path} could not be read to the end: {e.Message}";
        }
    }
}
