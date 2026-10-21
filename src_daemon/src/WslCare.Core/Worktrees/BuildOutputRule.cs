using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// What build output is (E19.S3, PLAN_disk_hygiene_windows.md §3.3 "Build output", E19 stories §3 decision 11, and the M6 census of 321
/// real worktrees). A <c>bin</c>/<c>obj</c> is build output only when EVERY file in it is a build artifact — an EXTENSION on the
/// configured list (never a glob: <c>*.sqlite*</c> matched <c>Microsoft.Data.Sqlite.dll</c> in 17 real bins), a file with none when the
/// list says <c>none</c>, <c>.cs</c> only under <c>obj</c> when it says <c>obj:.cs</c> — and none is one of the never-artifacts
/// (databases, keys, <c>.env</c> files) or was written more than <c>worktrees.artifactSkewMinutes</c> after the newest compiled output
/// (<c>.dll</c>, <c>.exe</c>, <c>.pdb</c>) of its folder: a name is no proof of regenerability. <c>node_modules</c> is exempt — its contents
/// are packages <c>package.json</c> and the lockfile restore, and that is the owner's rule. Pure over the files a walk listed.
/// </summary>
public static class BuildOutputRule
{
    /// <summary>The folder names a build writes into: beside a project file for the first two, beside a <c>package.json</c> for the last.</summary>
    public const string Bin = "bin";

    public const string Obj = "obj";

    public const string NodeModules = "node_modules";

    private const string NoExtension = "none";

    private const string ObjOnlyMark = "obj:";

    private static readonly string[] Compiled = [".dll", ".exe", ".pdb"];

    private static readonly string[] NeverArtifactExtensions = [".db", ".sqlite", ".sqlite3", ".db-wal", ".db-shm", ".mdf", ".ldf", ".pfx", ".key", ".pem"];

    private const string EnvName = ".env";

    private const string NotAnArtifact = "holds a file that is not a build artifact";

    public static bool IsProjectFolderName(string name) =>
        name.Equals(Bin, StringComparison.OrdinalIgnoreCase) || name.Equals(Obj, StringComparison.OrdinalIgnoreCase);

    public static bool IsNodeModules(string name) => name.Equals(NodeModules, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a <c>bin</c>/<c>obj</c>/<c>node_modules</c> named <paramref name="folderName"/> at <paramref name="candidateRoot"/>, holding
    /// <paramref name="files"/>, is NOT wholly build output — the sentence names the first offender, in path order; empty when it is.</summary>
    public static string Retention(string folderName, string candidateRoot, IReadOnlyList<TreeFile> files, WorktreeSettings settings)
    {
        if (IsNodeModules(folderName) || files.Count == 0)
        {
            return string.Empty;
        }

        var rules = PathRules.Windows;
        var newestOfFolder = NewestCompiledByFolder(files, rules);
        var newestOverall = newestOfFolder.Count == 0 ? (DateTimeOffset?)null : newestOfFolder.Values.Max();
        return newestOverall is null
            ? $"holds no compiled output ({string.Join(", ", Compiled)}) to date its files against"
            : files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => Offence(f, folderName, candidateRoot, settings, rules, newestOfFolder, newestOverall.Value)).FirstOrDefault(r => r.Length > 0) ?? string.Empty;
    }

    private static Dictionary<string, DateTimeOffset> NewestCompiledByFolder(IReadOnlyList<TreeFile> files, PathRules rules) =>
        files.Where(f => IsCompiled(NameOf(f.Path, rules)))
            .GroupBy(f => rules.Parent(f.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(f => f.LastWriteUtc), StringComparer.OrdinalIgnoreCase);

    private static string Offence(TreeFile file, string folderName, string candidateRoot, WorktreeSettings settings, PathRules rules, Dictionary<string, DateTimeOffset> newestOfFolder, DateTimeOffset newestOverall)
    {
        var name = NameOf(file.Path, rules);
        var shown = Shown(file.Path, candidateRoot, rules);
        return IsOutsideTheList(name, folderName, settings) ? $"{NotAnArtifact}: {shown}"
            : IsCompiled(name) || !WrittenAfterTheBuild(file, settings, newestOfFolder.GetValueOrDefault(rules.Parent(file.Path), newestOverall)) ? string.Empty
            : $"holds a file written after the build: {shown}";
    }

    private static bool IsOutsideTheList(string name, string folderName, WorktreeSettings settings) =>
        IsNeverArtifact(name) || !IsListedArtifact(name, folderName, settings.ArtifactPatterns);

    private static bool WrittenAfterTheBuild(TreeFile file, WorktreeSettings settings, DateTimeOffset newestCompiled) =>
        file.LastWriteUtc - newestCompiled > TimeSpan.FromMinutes(settings.ArtifactSkewMinutes);

    private static string NameOf(string path, PathRules rules) => rules.Segments(path) is { Count: > 0 } segments ? segments[^1] : path;

    private static string Shown(string path, string candidateRoot, PathRules rules) =>
        rules.IsStrictlyUnder(path, candidateRoot) ? rules.Normalize(path)[(rules.Normalize(candidateRoot).Length + 1)..] : NameOf(path, rules);

    private static string ExtensionOf(string name) => Path.GetExtension(name);

    private static bool IsCompiled(string name) => Compiled.Contains(ExtensionOf(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Databases, keys and <c>.env</c> files are never artifacts, whatever their folder or time (E19 stories §3 decision 11; the
    /// M6 census: by EXTENSION or exact name, never a glob).</summary>
    private static bool IsNeverArtifact(string name) =>
        NeverArtifactExtensions.Contains(ExtensionOf(name), StringComparer.OrdinalIgnoreCase)
        || name.Equals(EnvName, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(EnvName + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsListedArtifact(string name, string folderName, IReadOnlyList<string> patterns)
    {
        var extension = ExtensionOf(name);
        return extension.Length == 0
            ? patterns.Contains(NoExtension, StringComparer.Ordinal)
            : patterns.Contains(extension, StringComparer.OrdinalIgnoreCase) || (folderName.Equals(Obj, StringComparison.OrdinalIgnoreCase) && patterns.Contains(ObjOnlyMark + extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The reasons grouped for the report by their kind — the text before the file's name — so thousands of files share a line.</summary>
    public static string GroupOf(string reason) => reason.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? reason[..colon] : reason;
}
