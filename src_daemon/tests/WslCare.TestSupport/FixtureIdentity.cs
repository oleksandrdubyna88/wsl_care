using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WslCare.TestSupport;

/// <summary>One named rule of the identity substitution list: what it finds, and what it puts in its place.</summary>
/// <param name="Name">The rule's name, as a reviewer and the golden writer's list call it.</param>
/// <param name="Finds">The shape it rewrites.</param>
/// <param name="Becomes">What that shape becomes.</param>
public sealed record IdentityRule(string Name, string Finds, string Becomes);

/// <summary>
/// THE identity substitution list (E5 code round, 2026-10-04): the one reviewed list that turns a capture of a real machine
/// into a fixture this PUBLIC repository may carry, and the golden writer's identity rules (<c>GoldenContracts</c>), so a
/// regenerated golden is anonymised by the same code. It is DETERMINISTIC and CONSISTENT across files — a mapping is
/// learnt once over the whole corpus (<see cref="Learn"/>) and applied to every file (<see cref="Apply"/>), so cross-file
/// joins hold: a process's cwd in <c>links.txt</c> and its argv in <c>cmdline</c> name the same <c>project-a</c>. Every
/// number, id, size, time and structural property is kept; only names change. It holds NO original value: every rule is
/// a shape, and every mapping is derived from the data it is applied to — so this file cannot leak what it removes.
/// </summary>
/// <remarks>
/// Applied to the captured fixtures by <c>FixtureAnonymisationTests</c> (<c>WSL_CARE_ANONYMISE_FIXTURES=1</c> rewrites them;
/// otherwise it fails when a fixture is not already anonymised). What it does NOT do, because a capture's own redactor
/// already does it: Docker's container / volume / network / local-image names (<c>research/diagnostics/docker_fixture_redact.mjs</c>)
/// and the secret-looking argv values (<c>--connection-token=</c>, <c>--csrf_token=</c> — the procfs capture's
/// SOURCE.txt). The repository-wide detector is <c>FixturePrivacyTests</c>, written apart from this.
/// </remarks>
public sealed partial class FixtureIdentity
{
    /// <summary>The neutral account name every Linux and Windows user name becomes.</summary>
    public const string User = "user";

    /// <summary>What a temporary or scratchpad folder becomes.</summary>
    public const string Temp = "/tmp/x";

    public static IReadOnlyList<IdentityRule> Rules { get; } =
    [
        new("tempFolder", "a Windows temporary folder seen from the distro (/mnt/<d>/Users/<name>/AppData/Local/Temp/…) or Claude's Linux temporary folder (/tmp/claude…/…), down to its deepest folder", $"{Temp}, keeping a final file name"),
        new("linuxHome", "/home/<name> for any name but 'user'", $"/home/{User}"),
        new("windowsProfile", "/mnt/<d>/Users/<name> and <D>:\\Users\\<name> (JSON-escaped too) for any name but 'user' and Windows' own Public / Default", $"…/Users/{User}"),
        new("passwdAccount", "the name of an /etc/passwd line whose uid is a login account's (1000–65533)", User),
        new("accountToken", "a whole JSON string or argv element equal to a user name learnt from the corpus (passwd, /home/<name>, Users/<name>)", User),
        new("projectDirectory", "the folder under /home/user/git/ — a project or repository name", "project-a, project-b, … in ordinal order of the original names, the same in every file (only project-<one or two letters> counts as already neutral: a real project may well be called project-<word>)"),
        new("extensionId", "an editor extension's <publisher>.<name>[-<version>][-<platform>] folder under extensions/, globalStorage/ or workspaceStorage/<hash>/", "vendor.extension-a[-1.0.0][-<platform>], … in ordinal order, the same in every file — and the same id wherever else it recurs as a word (an extension host's log folder)"),
        new("email", "an e-mail address (not a systemd template instance such as getty@tty1.service)", "user@example.invalid"),
    ];

    private static readonly HashSet<string> WindowsOwnProfiles = new(StringComparer.OrdinalIgnoreCase) { User, "Public", "Default" };

    private static readonly HashSet<string> UnitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "service", "socket", "timer", "scope", "slice", "mount", "target", "path", "device", "swap", "automount",
    };

    private readonly IReadOnlySet<string> _accounts;
    private readonly IReadOnlyDictionary<string, string> _projects;
    private readonly IReadOnlyDictionary<string, string> _extensions;

    private FixtureIdentity(IReadOnlySet<string> accounts, IReadOnlyDictionary<string, string> projects, IReadOnlyDictionary<string, string> extensions)
    {
        _accounts = accounts;
        _projects = projects;
        _extensions = extensions;
    }

    /// <summary>How many project folders and extension ids the corpus named — for a reviewer's summary, never their values.</summary>
    public (int Accounts, int Projects, int Extensions) Counts => (_accounts.Count, _projects.Count, _extensions.Count);

    /// <summary>Learns the mappings from the whole corpus, so every file is rewritten the same way.</summary>
    public static FixtureIdentity Learn(IEnumerable<string> texts)
    {
        var corpus = texts.ToList();
        var accounts = corpus.SelectMany(LearntAccounts).Where(n => n != User).ToHashSet(StringComparer.Ordinal);
        var paths = corpus.Select(t => Profiles(Homes(TempFolders(t)))).ToList();
        var projects = Letters(paths.SelectMany(t => ProjectDirectory().Matches(t).Select(m => m.Groups["name"].Value)), ProjectName(), "project-");
        var extensions = Letters(corpus.SelectMany(t => ExtensionFolder().Matches(t).Select(m => m.Groups["id"].Value)), NeutralExtension(), "vendor.extension-");
        return new FixtureIdentity(accounts, projects, extensions);
    }

    /// <summary>Learns the mappings from every text file under <paramref name="root"/> (<see cref="TextFiles"/>).</summary>
    public static FixtureIdentity LearnFrom(string root) => Learn(TextFiles(root).Select(f => f.Text));

    /// <summary>Every file under <paramref name="root"/> that is valid UTF-8, as (path relative to it with forward slashes, text),
    /// in ordinal order. A file that is not — <c>proc/self/auxv</c>, a sigstore bundle — is binary and holds no name to
    /// rewrite; it is left out.</summary>
    public static IReadOnlyList<(string Path, string Text)> TextFiles(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (Path: System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'), Text: Utf8OrNone(File.ReadAllBytes(file))))
            .Where(f => f.Text is not null)
            .Select(f => (f.Path, f.Text!))
            .OrderBy(f => f.Path, StringComparer.Ordinal)];

    private static string? Utf8OrNone(byte[] bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The text with every rule applied, in the order of <see cref="Rules"/>.</summary>
    public string Apply(string text)
    {
        var paths = Profiles(Homes(TempFolders(text)));
        var accounts = AccountTokens(PasswdAccount().Replace(paths, m => IsLoginUid(m.Groups["uid"].Value) ? $"{User}:x:{m.Groups["uid"].Value}:" : m.Value));
        var projects = ProjectDirectory().Replace(accounts, m => m.Groups["prefix"].Value + _projects.GetValueOrDefault(m.Groups["name"].Value, m.Groups["name"].Value));
        var extensions = ExtensionIds(ExtensionFolder().Replace(projects, Extension));
        return Email().Replace(extensions, m => UnitTypes.Contains(m.Groups["tld"].Value) ? m.Value : $"{User}@example.invalid");
    }

    /// <summary>A learnt extension id wherever else it recurs as a whole word — an extension host's log folder
    /// (<c>…/exthost1/&lt;publisher&gt;.&lt;name&gt;</c>), a setting — by the same mapping as its folder.</summary>
    private string ExtensionIds(string text) =>
        _extensions.Aggregate(text, (current, pair) => Regex.Replace(current, $"(?<![A-Za-z0-9.-]){Regex.Escape(pair.Key)}(?![A-Za-z0-9.])", pair.Value, RegexOptions.CultureInvariant));

    private string AccountTokens(string text) =>
        _accounts.Aggregate(text, (current, name) => Regex.Replace(current, $"(?<=^|[\\0\"'])({Regex.Escape(name)})(?=$|[\\0\"'])", User, RegexOptions.Multiline | RegexOptions.CultureInvariant));

    private string Extension(Match m)
    {
        var id = m.Groups["id"].Value;
        var version = m.Groups["version"].Success ? "-1.0.0" : string.Empty;
        return _extensions.TryGetValue(id, out var neutral) ? $"{m.Groups["lead"].Value}{neutral}{version}{m.Groups["platform"].Value}" : m.Value;
    }

    private static string TempFolders(string text) => TempFolder().Replace(text, m =>
    {
        var last = m.Groups["rest"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return last.Contains('.', StringComparison.Ordinal) ? $"{Temp}/{last}" : Temp;
    });

    private static string Homes(string text) => LinuxHome().Replace(text, m => m.Groups["name"].Value == User ? m.Value : $"/home/{User}");

    private static string Profiles(string text) =>
        WindowsProfile().Replace(text, m => WindowsOwnProfiles.Contains(m.Groups["name"].Value) ? m.Value : m.Groups["prefix"].Value + User);

    private static IEnumerable<string> LearntAccounts(string text) =>
        PasswdAccount().Matches(text).Where(m => IsLoginUid(m.Groups["uid"].Value)).Select(m => m.Groups["name"].Value)
            .Concat(LinuxHome().Matches(text).Select(m => m.Groups["name"].Value))
            .Concat(WindowsProfile().Matches(text).Select(m => m.Groups["name"].Value).Where(n => !WindowsOwnProfiles.Contains(n)));

    private static bool IsLoginUid(string uid) =>
        int.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1000 and <= 65533;

    /// <summary>Each distinct original (in ordinal order) mapped to <paramref name="prefix"/> + the next free letters; a value
    /// already neutral (<paramref name="neutral"/>) keeps its name and its letters are never handed out again.</summary>
    private static IReadOnlyDictionary<string, string> Letters(IEnumerable<string> names, Regex neutral, string prefix)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var taken = distinct.Where(n => neutral.IsMatch(n)).ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var name in distinct.Where(n => !taken.Contains(n)))
        {
            string candidate;
            do
            {
                candidate = prefix + LetterName(next++);
            }
            while (taken.Contains(candidate));

            map[name] = candidate;
        }

        return map;
    }

    /// <summary>0 → a, 25 → z, 26 → aa, …</summary>
    public static string LetterName(int index) =>
        index < 26 ? ((char)('a' + index)).ToString() : LetterName((index / 26) - 1) + (char)('a' + (index % 26));

    [GeneratedRegex(@"(?:/mnt/[a-z]/Users/[^/\s\0""']+/AppData/Local/Temp|/tmp/claude(?:-\d+)?)(?=[/\s\0""']|$)(?<rest>(?:/[^/\s\0""']+)*)", RegexOptions.CultureInvariant)]
    private static partial Regex TempFolder();

    [GeneratedRegex(@"/home/(?<name>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxHome();

    [GeneratedRegex(@"(?<prefix>/mnt/[A-Za-z]/Users/|(?<![A-Za-z0-9])[A-Za-z]:\\{1,2}Users\\{1,2})(?<name>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsProfile();

    [GeneratedRegex(@"^(?<name>[a-z_][a-z0-9_-]*):x:(?<uid>\d+):", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PasswdAccount();

    [GeneratedRegex(@"(?<prefix>/home/user/git/)(?<name>[^/\s\0""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectDirectory();

    [GeneratedRegex(@"^project-[a-z]{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectName();

    [GeneratedRegex(@"(?<lead>/(?:extensions|globalStorage|workspaceStorage/[0-9a-f]+)/)(?<id>[a-z0-9][a-z0-9-]*\.[a-z0-9][a-z0-9-]*?)(?<version>-\d+\.\d+\.\d+)?(?<platform>-(?:linux|win32|darwin|alpine)-(?:x64|arm64|armhf|ia32))?(?=[/\s\0""']|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionFolder();

    [GeneratedRegex(@"^vendor\.extension-[a-z]{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex NeutralExtension();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.(?<tld>[A-Za-z]{2,})(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
