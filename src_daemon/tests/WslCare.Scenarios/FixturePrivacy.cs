using System.Text.RegularExpressions;

namespace WslCare.Scenarios;

/// <summary>
/// The repository-wide privacy scan (E5 code round, 2026-10-04): what a COMMITTED fixture or golden must never carry,
/// because this repository is public. It is a detector, written apart from the anonymiser it checks
/// (<c>WslCare.TestSupport.FixtureIdentity</c>), so the two cannot share a blind spot by construction:
/// <list type="bullet">
///   <item>a <c>/home/&lt;name&gt;</c> path whose name is not <c>user</c>;</item>
///   <item>a Windows profile path — <c>/mnt/&lt;d&gt;/Users/&lt;name&gt;</c> or <c>&lt;D&gt;:\Users\&lt;name&gt;</c> — whose name is not
///   <c>user</c> (Windows' own <c>Public</c> and <c>Default</c> profiles excepted);</item>
///   <item>an e-mail address (a systemd template instance such as <c>getty@tty1.service</c> is not one);</item>
///   <item>the user name of the machine RUNNING the test — derived now from the environment and the home folder, as
///   <c>src_vs_code/scripts/check-vsix.mjs</c> derives it, and never written into the repository.</item>
/// </list>
/// A finding names the file, the line and the rule — never the value, which is the thing that must not be repeated.
/// </summary>
internal static partial class FixturePrivacy
{
    /// <summary>The directory names whose whole content is scanned, wherever they sit in the repository.</summary>
    public static IReadOnlyList<string> ScannedDirectoryNames => ["fixtures", "golden", "goldens"];

    /// <summary>Folders never descended into: build output, dependencies, the downloaded editors of the extension-host
    /// tests, and the shared-rules submodule (another repository, with its own checks).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "out", "dist", "artifacts", ".vscode-test", ".agents",
    };

    /// <summary>Names a machine account may legitimately share with the anonymised data or the system.</summary>
    private static readonly HashSet<string> NotPersonal = new(StringComparer.OrdinalIgnoreCase) { "user", "root" };

    private static readonly HashSet<string> WindowsOwnProfiles = new(StringComparer.OrdinalIgnoreCase) { "user", "Public", "Default" };

    /// <summary>systemd unit types: <c>name@instance.type</c> is a template instance, not an address.</summary>
    private static readonly HashSet<string> UnitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "service", "socket", "timer", "scope", "slice", "mount", "target", "path", "device", "swap", "automount",
    };

    /// <summary>Every file under a scanned directory of the repository at <paramref name="root"/>, as a path relative to it
    /// with forward slashes, in ordinal order.</summary>
    public static IReadOnlyList<string> ScannedFiles(string root) =>
        [.. ScannedDirectories(root)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<string> ScannedDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (Skipped.Contains(name))
            {
                continue;
            }

            if (ScannedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                yield return child;
                continue;
            }

            foreach (var nested in ScannedDirectories(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The user names of the machine running this test: the process's account, <c>USERNAME</c>, <c>USER</c> and the
    /// home folder's name — the same four places check-vsix.mjs reads. Blank values and <see cref="NotPersonal"/> names
    /// are left out.</summary>
    public static IReadOnlyList<string> MachineUserNames() =>
        Personal([
            Environment.UserName,
            Environment.GetEnvironmentVariable("USERNAME"),
            Environment.GetEnvironmentVariable("USER"),
            Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\')),
        ]);

    /// <summary>The distinct, non-blank names of <paramref name="candidates"/> that are not <see cref="NotPersonal"/>.</summary>
    public static IReadOnlyList<string> Personal(IEnumerable<string?> candidates) =>
        [.. candidates.Select(c => c?.Trim() ?? string.Empty).Where(c => c.Length > 0 && !NotPersonal.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>What <paramref name="text"/> (the content of <paramref name="file"/>) must not carry, one finding per rule
    /// and line: <c>file:line holds …</c>. Empty is clean.</summary>
    public static IReadOnlyList<string> Findings(string file, string text, IReadOnlyList<string> machineNames)
    {
        var findings = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            findings.AddRange(LineFindings(lines[i], machineNames).Select(what => $"{file}:{i + 1} holds {what}"));
        }

        return findings;
    }

    private static IEnumerable<string> LineFindings(string line, IReadOnlyList<string> machineNames)
    {
        if (HomePath().Matches(line).Any(m => m.Groups["name"].Value != "user"))
        {
            yield return "a /home/<name> path whose name is not 'user'";
        }

        if (ProfilePath().Matches(line).Any(m => !WindowsOwnProfiles.Contains(m.Groups["name"].Value)))
        {
            yield return "a Windows profile path (/mnt/<d>/Users/<name> or <D>:\\Users\\<name>) whose name is not 'user'";
        }

        if (Email().Matches(line).Any(m => !UnitTypes.Contains(m.Groups["tld"].Value)))
        {
            yield return "an e-mail address";
        }

        for (var n = 0; n < machineNames.Count; n++)
        {
            if (Regex.IsMatch(line, $"(?<![A-Za-z0-9]){Regex.Escape(machineNames[n])}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                yield return $"the user name #{n + 1} of the machine running this test (not printed)";
            }
        }
    }

    [GeneratedRegex(@"/home/(?<name>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HomePath();

    /// <summary><c>/mnt/c/Users/x</c>, <c>C:\Users\x</c> and its JSON-escaped form <c>C:\\Users\\x</c>.</summary>
    [GeneratedRegex(@"(?:/mnt/[A-Za-z]/Users/|(?<![A-Za-z0-9])[A-Za-z]:\\{1,2}Users\\{1,2})(?<name>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.(?<tld>[A-Za-z]{2,})(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
