namespace WslCare.Core.Processes.Policy;

/// <summary>The command a <c>runuser</c> argv wraps: whose, which file, and its arguments.</summary>
/// <param name="User">The account after <c>-u</c>.</param>
/// <param name="ExecutablePath">The FULL path of the tool, resolved in the user's bin folders before the start.</param>
/// <param name="Arguments">Everything after the executable.</param>
public sealed record WrappedCommand(string User, string ExecutablePath, IReadOnlyList<string> Arguments)
{
    /// <summary>The tool's bare name (<c>npm</c>) — what a user-scoped template declares.</summary>
    public string ExecutableName => NeverList.Exe([ExecutablePath]);

    /// <summary>The wrapped argv, executable first — what the never-list judges a second time.</summary>
    public IReadOnlyList<string> Argv => [ExecutablePath, .. Arguments];
}

/// <summary>
/// The ONE shape in which the product runs a tool as the target user (plan §15c #2):
/// <c>runuser -u &lt;user&gt; -- &lt;full path&gt; &lt;args…&gt;</c> — argv, no shell, no <c>-l</c>, no <c>-c</c>, no
/// <c>-m</c> — with a clean environment (<see cref="CommandEnvironment.Clean"/>) the builder fills with the user's
/// <c>HOME</c>, <c>USER</c>, <c>LOGNAME</c> and a <c>PATH</c> of the fixed bin folders.
/// </summary>
/// <remarks>
/// <para>Not observed here (it needs root, and no test or smoke runs as root): per util-linux's <c>su-common.c</c>,
/// <c>runuser</c> without <c>-l</c> and without <c>-m</c> sets <c>HOME</c>, <c>SHELL</c>, <c>USER</c> and <c>LOGNAME</c>
/// to the target account and keeps <c>PATH</c> unless <c>/etc/login.defs</c> says <c>ALWAYS_SET_PATH yes</c>. The tool is
/// started by its FULL path either way, so <c>PATH</c> only matters to what the tool itself starts (<c>npm</c>'s
/// <c>#!/usr/bin/env node</c>).</para>
/// <para>The executable must be a full path inside one of the bin folders of <see cref="BinFolderSuffixes"/> (or an
/// nvm node version's <c>bin</c>), with no <c>..</c> — the policy refuses any other file even when its name matches.</para>
/// </remarks>
public static class TargetUserArgv
{
    public const string Runuser = "runuser";

    /// <summary>The bin folders a user-scoped tool may live in, as path endings (plan §15c #2), apart from nvm's
    /// <c>.nvm/versions/node/v&lt;version&gt;/bin</c>, which <see cref="IsInABinFolder"/> recognises by shape.</summary>
    public static readonly IReadOnlyList<string> BinFolderSuffixes = ["/.local/bin", "/.cargo/bin", "/usr/local/bin", "/usr/bin"];

    private static readonly SlotKind.UserName UserNames = new();

    public static IReadOnlyList<string> Build(string user, string executablePath, IReadOnlyList<string> arguments) =>
        [Runuser, "-u", user, "--", executablePath, .. arguments];

    /// <summary>The wrapped command of an argv in the one shape; <c>null</c> for anything else.</summary>
    public static WrappedCommand? Parse(IReadOnlyList<string> argv) =>
        HasTheOneShape(argv) ? new WrappedCommand(argv[2], argv[4], [.. argv.Skip(5)]) : null;

    /// <summary><c>runuser -u &lt;valid name&gt; -- &lt;full path&gt; …</c> exactly.</summary>
    public static bool HasTheOneShape(IReadOnlyList<string> argv) =>
        argv.Count >= 5
        && string.Equals(argv[0], Runuser, StringComparison.Ordinal)
        && argv[1] == "-u"
        && UserNames.Accepts(argv[2])
        && argv[3] == "--"
        && IsFullPath(argv[4]);

    /// <summary>Whether <paramref name="executablePath"/> is a file directly inside a permitted bin folder.</summary>
    public static bool IsInABinFolder(string executablePath)
    {
        var normal = executablePath.Replace('\\', '/');
        var folder = normal[..Math.Max(normal.LastIndexOf('/'), 0)];
        var segments = normal.Split('/');
        return IsFullPath(executablePath)
            && !segments.Any(s => s is ".." or ".")
            && (BinFolderSuffixes.Any(s => folder.EndsWith(s, StringComparison.Ordinal)) || IsNvmBin(segments));
    }

    /// <summary><c>…/.nvm/versions/node/v22.11.0/bin/&lt;tool&gt;</c>.</summary>
    private static bool IsNvmBin(IReadOnlyList<string> segments) =>
        segments.Count >= 6
        && segments[^2] == "bin"
        && segments[^4] == "node" && segments[^5] == "versions" && segments[^6] == ".nvm"
        && segments[^3].Length > 1 && segments[^3][0] == 'v' && segments[^3][1..].All(c => char.IsAsciiDigit(c) || c == '.');

    private static bool IsFullPath(string path) => path.StartsWith('/') || Path.IsPathFullyQualified(path);
}
