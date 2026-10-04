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

    /// <summary>Suffixes after an <c>@</c> that make a file or unit name, not an address: systemd unit types (a template
    /// instance, <c>&lt;name&gt;@&lt;instance&gt;.&lt;type&gt;</c>) and journald's archived files (<c>system@&lt;id&gt;.journal</c>).</summary>
    private static readonly HashSet<string> UnitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "service", "socket", "timer", "scope", "slice", "mount", "target", "path", "device", "swap", "automount", "journal",
    };

    /// <summary>Every file under a scanned directory of the repository at <paramref name="root"/>, as a path relative to it
    /// with forward slashes, in ordinal order.</summary>
    public static IReadOnlyList<string> ScannedFiles(string root) =>
        [.. ScannedDirectories(root)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>Every TEXT file of the repository at <paramref name="root"/> — what <c>git ls-files</c> lists when the root is
    /// a git checkout, else every file of a walk that skips the folders above (a copy without <c>.git</c>, as the WSL runs
    /// use) — as (path relative to it with forward slashes, text), in ordinal order. A file that is not valid UTF-8 (an
    /// image, a binary capture) holds no text to scan. The shared-rules submodule is another repository and is left out.</summary>
    public static IReadOnlyList<(string Path, string Text)> RepositoryTextFiles(string root) =>
        [.. (TrackedFiles(root) ?? WalkedFiles(root, root))
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Text: Utf8OrNull(Path.Combine(root, path))))
            .Where(f => f.Text is not null)
            .Select(f => (f.Path, f.Text!))];

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string? Utf8OrNull(string file)
    {
        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(file));
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The tracked files, or null when <paramref name="root"/> is not a git checkout or git cannot be started.</summary>
    private static IEnumerable<string>? TrackedFiles(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            return null;
        }

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
            start.ArgumentList.Add("ls-files");
            start.ArgumentList.Add("-z");
            using var git = System.Diagnostics.Process.Start(start)!;
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(60_000))
            {
                git.Kill(entireProcessTree: true);
                return null;
            }

            return git.ExitCode == 0
                ? output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(".agents/conventions", StringComparison.Ordinal))
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> WalkedFiles(string root, string directory) =>
        Directory.EnumerateFiles(directory).Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Concat(Directory.EnumerateDirectories(directory).Where(d => !Skipped.Contains(Path.GetFileName(d))).SelectMany(d => WalkedFiles(root, d)));

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
    public static IReadOnlyList<string> Findings(string file, string text, IReadOnlyList<string> machineNames) =>
        Findings(file, text, machineNames, NoSyntheticNames);

    /// <summary>As the strict overload, with <paramref name="syntheticNames"/> also accepted as a /home or profile name — the
    /// repository-wide scan's explicit allowlist of invented test accounts.</summary>
    public static IReadOnlyList<string> Findings(string file, string text, IReadOnlyList<string> machineNames, IReadOnlySet<string> syntheticNames)
    {
        var findings = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            findings.AddRange(LineFindings(lines[i], machineNames, syntheticNames).Select(what => $"{file}:{i + 1} holds {what}"));
        }

        return findings;
    }

    private static readonly IReadOnlySet<string> NoSyntheticNames = new HashSet<string>(StringComparer.Ordinal);

    private static IEnumerable<string> LineFindings(string line, IReadOnlyList<string> machineNames, IReadOnlySet<string> syntheticNames)
    {
        if (HomePath().Matches(line).Any(m => IsForeign(m, Users, syntheticNames)))
        {
            yield return "a /home/<name> path whose name is not 'user'";
        }

        if (ProfilePath().Matches(line).Any(m => IsForeign(m, WindowsOwnProfiles, syntheticNames)))
        {
            yield return "a Windows profile path (/mnt/<d>/Users/<name> or <D>:\\Users\\<name>) whose name is not 'user'";
        }

        if (Email().Matches(line).Any(m => !IsAllowedAddress(m)))
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

    private static readonly HashSet<string> Users = new(StringComparer.Ordinal) { "user" };

    /// <summary>A matched home / profile name that is a person's: not one of <paramref name="own"/> nor an allowlisted
    /// synthetic name. A sentence's full stop is not part of the name, and an ellipsis (<c>/mnt/c/Users/.../</c>) names no one.</summary>
    private static bool IsForeign(Match m, IReadOnlySet<string> own, IReadOnlySet<string> syntheticNames)
    {
        var name = m.Groups["name"].Value.TrimEnd('.');
        return name.Length > 0 && !own.Contains(name) && !syntheticNames.Contains(name);
    }

    /// <summary>Not a person's address: a systemd template instance (<c>getty@tty1.service</c>), an address at a domain
    /// RFC 2606 / RFC 6761 reserve for examples (<c>example.com</c>, <c>example.org</c>, <c>example.net</c>, <c>*.example</c>,
    /// <c>*.invalid</c>, <c>*.test</c> — the placeholders the tests and the anonymised data use), or one of
    /// <see cref="ServiceAddresses"/>.</summary>
    private static bool IsAllowedAddress(Match m)
    {
        var address = m.Value.ToLowerInvariant();
        var domain = address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        return UnitTypes.Contains(m.Groups["tld"].Value) || ServiceAddresses.Contains(address) || ReservedDomain().IsMatch(domain);
    }

    /// <summary>Addresses that belong to a service, not a person: the trailer every agent commit carries.</summary>
    private static readonly HashSet<string> ServiceAddresses = new(StringComparer.Ordinal) { "noreply@anthropic.com" };

    [GeneratedRegex(@"^(?:(?:[a-z0-9-]+\.)*example\.(?:com|org|net)|(?:[a-z0-9-]+\.)*(?:example|invalid|test))$", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDomain();

    [GeneratedRegex(@"/home/(?<name>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HomePath();

    /// <summary><c>/mnt/c/Users/x</c>, <c>C:\Users\x</c> and its JSON-escaped form <c>C:\\Users\\x</c>.</summary>
    [GeneratedRegex(@"(?:/mnt/[A-Za-z]/Users/|(?<![A-Za-z0-9])[A-Za-z]:\\{1,2}Users\\{1,2})(?<name>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.(?<tld>[A-Za-z]{2,})(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
