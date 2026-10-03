using System.Text.RegularExpressions;

namespace WslCare.Scenarios;

/// <summary>
/// Where the release pipeline lives in this repository (E4.S2) and the small readings its tests share: the workflows,
/// the release scripts, the asset contract they source, the release-please files and the owner-applied rulesets — read
/// where they are, from the repository root the build stamped, never copied.
/// </summary>
internal static partial class ReleaseFiles
{
    public static string Root => ShippedFiles.RepositoryRoot;

    public static string WorkflowDirectory => Path.Combine(Root, ".github", "workflows");

    public static string Workflow(string name) => Path.Combine(WorkflowDirectory, name);

    public static string Script(string name) => Path.Combine(Root, ".github", "scripts", name);

    public static string AssetContract => Script(Path.Combine("lib", "daemon-assets.sh"));

    public static string Ruleset(string name) => Path.Combine(Root, ".github", "rulesets", name);

    public static string ReleasePleaseConfig => Path.Combine(Root, "release-please-config.json");

    public static string ReleasePleaseManifest => Path.Combine(Root, ".release-please-manifest.json");

    public static string VersionFile => Path.Combine(Root, "src_daemon", "version.txt");

    public static IReadOnlyList<string> AllWorkflows => [.. Directory.EnumerateFiles(WorkflowDirectory, "*.yml").Order(StringComparer.Ordinal)];

    /// <summary>The RIDs a daemon release ships, as the asset contract spells them (<c>DAEMON_RIDS</c>).</summary>
    public static IReadOnlyList<string> DaemonRids =>
        Single(DaemonRidsLine(), File.ReadAllText(AssetContract), "DAEMON_RIDS").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string DaemonVersionPattern => Single(DaemonPatternLine(), File.ReadAllText(AssetContract), "DAEMON_VERSION_PATTERN");

    public static string InstallerVersionPattern => Single(InstallerPatternLine(), File.ReadAllText(ShippedFiles.InstallScript), "install.sh VERSION_PATTERN");

    /// <summary>The RIDs install.sh maps a machine to (<c>RID=linux-…</c>).</summary>
    public static IReadOnlyList<string> InstallerRids => [.. InstallerRid().Matches(File.ReadAllText(ShippedFiles.InstallScript)).Select(m => m.Groups[1].Value).Distinct()];

    /// <summary>The files install.sh requires inside an archive's top folder — its unpack loop, <c>for file in …; do</c>.</summary>
    public static IReadOnlyList<string> InstallerRequiredMembers =>
        Single(InstallerMembers(), File.ReadAllText(ShippedFiles.InstallScript), "install.sh unpack loop").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string TrimmedVersion => File.ReadAllText(VersionFile).Trim();

    private static string Single(Regex pattern, string text, string what)
    {
        var matches = pattern.Matches(text);
        return matches.Count == 1 ? matches[0].Groups[1].Value : throw new InvalidOperationException($"expected exactly one {what}, found {matches.Count}");
    }

    [GeneratedRegex("""^readonly DAEMON_RIDS="([^"]+)"$""", RegexOptions.Multiline)]
    private static partial Regex DaemonRidsLine();

    [GeneratedRegex("""^readonly DAEMON_VERSION_PATTERN='([^']+)'$""", RegexOptions.Multiline)]
    private static partial Regex DaemonPatternLine();

    [GeneratedRegex("""^readonly VERSION_PATTERN='([^']+)'$""", RegexOptions.Multiline)]
    private static partial Regex InstallerPatternLine();

    [GeneratedRegex("""RID=(linux-[a-z0-9]+)""")]
    private static partial Regex InstallerRid();

    [GeneratedRegex("""^\s*for file in ([^;]+); do\s*$""", RegexOptions.Multiline)]
    private static partial Regex InstallerMembers();
}

/// <summary>Readings of one parsed workflow file.</summary>
internal static class WorkflowShape
{
    public static YamlMap Jobs(YamlMap workflow) => workflow["jobs"].Map;

    public static IReadOnlyList<YamlMap> Steps(YamlMap job) => [.. job["steps"].Items.Select(i => i.Map)];

    public static string Uses(YamlMap step) => step.Find("uses")?.Text ?? string.Empty;

    public static string Run(YamlMap step) => step.Find("run")?.Text ?? string.Empty;

    /// <summary>A permissions block as scope → access; a scalar (<c>write-all</c>) becomes <c>* → value</c>; none is empty.</summary>
    public static IReadOnlyDictionary<string, string> Permissions(YamlMap owner) => owner.Find("permissions") switch
    {
        null => new Dictionary<string, string>(),
        YamlScalar s => new Dictionary<string, string> { ["*"] = s.Value },
        YamlMap m => m.Scalars(),
        _ => throw new InvalidOperationException("permissions is neither a map nor a scalar"),
    };

    /// <summary>The matrix's include entries of a job, each as key → value.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> MatrixInclude(YamlMap job) =>
        [.. job["strategy"].Map["matrix"].Map["include"].Items.Select(i => i.Map.Scalars())];

    public static IReadOnlyDictionary<string, string> RunnerPerRid(YamlMap job) =>
        MatrixInclude(job).ToDictionary(e => e["rid"], e => e["os"], StringComparer.Ordinal);

    /// <summary>The check names a job reports: its name with every <c>${{ matrix.X }}</c> filled per include entry.</summary>
    public static IReadOnlyList<string> CheckNames(string id, YamlMap job)
    {
        var name = job.Find("name")?.Text ?? id;
        if (!name.Contains("${{ matrix.", StringComparison.Ordinal))
        {
            return [name];
        }

        return [.. MatrixInclude(job).Select(entry => entry.Aggregate(name, (text, pair) => text.Replace($"${{{{ matrix.{pair.Key} }}}}", pair.Value, StringComparison.Ordinal)))];
    }

    /// <summary>The index of the first step whose <c>run</c> contains <paramref name="text"/> or whose <c>uses</c> starts with it.</summary>
    public static int StepIndex(YamlMap job, string text)
    {
        var steps = Steps(job);
        for (var i = 0; i < steps.Count; i++)
        {
            if (Run(steps[i]).Contains(text, StringComparison.Ordinal) || Uses(steps[i]).StartsWith(text, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
