using System.Text.RegularExpressions;

namespace WslCare.Core.Archive;

/// <summary>
/// Names the archive can hold (plan §15r D2.1, E9.S1): every archive must be writable on NTFS and on an SMB share — the owner's
/// base is a Windows network drive — so a name a Linux folder allows and NTFS does not refuses its session in the preview, with
/// the reason, rather than failing halfway through a copy. And Claude Code's project folder for a working directory.
/// </summary>
public static partial class ArchiveNames
{
    /// <summary>The characters NTFS and SMB refuse in a name (besides the control characters).</summary>
    private static readonly char[] NotOnNtfs = ['<', '>', ':', '"', '\\', '|', '?', '*'];

    /// <summary>The device names Windows reserves, with or without an extension.</summary>
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    /// <summary>The mark of a file an interrupted removal renamed aside (§15r D2.8, review M3): <c>&lt;name&gt;.wsl-care-q-&lt;runId&gt;</c>.</summary>
    public const string QuarantineMark = ".wsl-care-q-";

    /// <summary>Why <paramref name="name"/> (one segment) cannot be held by the archive; empty when it can. A name the distro's bytes
    /// did not decode holds U+FFFD or a lone surrogate — both enumerate as the replacement rune; a surrogate PAIR is one valid character.</summary>
    public static string Problem(string name) =>
        Rules.FirstOrDefault(rule => rule.Breaks(name)) is { } broken ? $"\"{Shown(name)}\" {broken.Says}" : string.Empty;

    /// <summary>One rule a name must keep, and what a refusal says after the name.</summary>
    private sealed record NameRule(Func<string, bool> Breaks, string Says);

    private static readonly NameRule[] Rules =
    [
        new(name => name.EnumerateRunes().Any(r => r == System.Text.Rune.ReplacementChar), "is not valid UTF-8"),
        new(name => name.Any(c => char.IsControl(c) || NotOnNtfs.Contains(c)), "holds a character NTFS refuses (< > : \" \\ | ? * or a control character)"),
        new(name => name.EndsWith('.') || name.EndsWith(' '), "ends in a dot or a space, which NTFS drops"),
        new(name => Reserved.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase), "is a name Windows reserves for a device"),
    ];

    /// <summary>The first two of <paramref name="relativePaths"/> that differ by case only — one file on a case-blind filesystem;
    /// empty when none do.</summary>
    public static string CaseCollision(IEnumerable<string> relativePaths) =>
        relativePaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Distinct(StringComparer.Ordinal).Skip(1).Any()) is { } clash
            ? $"\"{Shown(clash.First())}\" and \"{Shown(clash.Distinct(StringComparer.Ordinal).ElementAt(1))}\" differ by case only — one name on NTFS"
            : string.Empty;

    /// <summary>Claude Code's project folder for a working directory: every character that is not a letter or a digit becomes
    /// <c>-</c> (observed on this machine: <c>D:\rsd\ClaudeRag</c> → <c>d--rsd-ClaudeRag</c>, its drive letter lowercased) — compared
    /// case-insensitively.</summary>
    public static string ClaudeProjectOf(string workingDirectory) => NotAlphanumeric().Replace(workingDirectory, "-");

    /// <summary>A name as a sentence may quote it: control characters shown as <c>?</c>.</summary>
    private static string Shown(string name) => new([.. name.Select(c => char.IsControl(c) ? '?' : c)]);

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotAlphanumeric();
}

/// <summary>
/// The archive's side folder (plan §15r D4, review M1): <c>windows-&lt;host&gt;</c> or <c>wsl-&lt;host&gt;-&lt;distribution&gt;</c>,
/// lowercased to <c>[a-z0-9._-]</c>, so two PCs on one share — or two distributions of one PC — never write one folder.
/// </summary>
public static partial class SideName
{
    /// <summary>The side folder of this process: its machine name and, in the distribution, <c>WSL_DISTRO_NAME</c>.</summary>
    public static string OfThisProcess(Hosting.HostSide side) =>
        Of(side, Environment.MachineName, Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") ?? string.Empty);

    public static string Of(Hosting.HostSide side, string host, string distribution) =>
        side == Hosting.HostSide.Windows
            ? $"windows-{Clean(host)}"
            : $"wsl-{Clean(host)}-{(distribution.Length == 0 ? "unknown" : Clean(distribution))}";

    private static string Clean(string text) => Unsafe().Replace(text.ToLowerInvariant(), "-");

    [GeneratedRegex("[^a-z0-9._-]")]
    private static partial Regex Unsafe();
}
