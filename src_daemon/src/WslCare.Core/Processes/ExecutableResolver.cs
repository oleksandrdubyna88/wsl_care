using WslCare.Core.Collectors;

namespace WslCare.Core.Processes;

/// <summary>What looking up a command's executable produced: the full path to start, or why there is none.</summary>
public abstract record ResolvedExecutable
{
    private ResolvedExecutable()
    {
    }

    /// <summary>The file to start — a full path, so the operating system is never asked to search.</summary>
    public sealed record Found(string Path) : ResolvedExecutable;

    /// <summary>Nothing on <c>PATH</c> answers to the name (or the name is a relative path); the reason says where it looked.</summary>
    public sealed record NotFound(string Reason) : ResolvedExecutable;
}

/// <summary>
/// The product's own answer to "which file does <c>docker</c> mean": a bare name is looked up on <c>PATH</c> — and,
/// inside the distro, only for the closed list of Windows programs in <see cref="WindowsSystemDrive.Programs"/>, in its
/// folder on the mounted Windows system drive when <c>PATH</c> does not hold it — and the FULL path is what the runner
/// starts.
/// </summary>
/// <remarks>
/// <para>Why it exists (CI run 37045304356, win-x64): a bare name handed to the operating system is not a
/// <c>PATH</c> lookup. Windows <c>CreateProcess</c> searches the application's directory, the CURRENT directory, the
/// 32-bit System directory and the Windows directory BEFORE <c>PATH</c>, and .NET's Unix launcher also tries the
/// application's and the current directory first. GitHub's Windows image ships <c>docker.exe</c> in
/// <c>C:\Windows\System32</c>, so every scenario's fake on <c>PATH</c> lost to the runner's real Docker — and on an
/// owner's machine a <c>docker.exe</c> dropped into whatever directory the daemon was started from would win the same
/// way. The family learned the same lesson for <c>gh</c> (<c>dew_flow_conventions</c>,
/// <c>.github/scripts/lib/resolved.mjs</c>).</para>
/// <para>The rules: an absolute path passes through unchanged; a relative path with a directory in it is refused (it
/// would mean whatever the current directory makes it); a bare name is tried in each <c>PATH</c> entry in order, skipping
/// empty and relative entries (both mean "the current directory"). On Windows only <c>.exe</c> and <c>.com</c> are
/// candidates — a <c>.cmd</c> or <c>.bat</c> needs a shell, and no shell is ever involved (<see cref="CommandRequest"/>);
/// on Linux the file must carry an execute bit.</para>
/// <para>The one fallback (live, 2026-10-04): a systemd service's <c>PATH</c> holds no Windows folder — WSL appends them only
/// for interactive and login sessions — so under the timer <c>powershell.exe</c> was never found and the clock probe and
/// A16 never ran. A name <see cref="WindowsSystemDrive.Programs"/> lists is then looked for in its one fixed folder on the
/// drive <see cref="WindowsSystemDrive"/> finds in the mount table, and started only as a regular file with an execute bit
/// and the Windows program header. Nothing else changes: <c>PATH</c> still wins, every other name is PATH-only, and the
/// argv the <see cref="Policy.CommandPolicy"/> judged still names the bare program.</para>
/// </remarks>
public static class ExecutableResolver
{
    /// <summary>The extensions a Windows program can be started under without a shell.</summary>
    public static readonly IReadOnlyList<string> WindowsExtensions = [".exe", ".com"];

    /// <summary>Resolves against this process's <c>PATH</c>, by this platform's rules — inside the distro falling back to
    /// the mounted Windows system drive for the programs <see cref="WindowsSystemDrive.Programs"/> names.</summary>
    public static ResolvedExecutable Resolve(string name) =>
        Resolve(name, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(), WindowsSystemDrive.MountPointHere);

    /// <summary>Resolves <paramref name="name"/> against <paramref name="pathVariable"/> ALONE by the rules of the platform
    /// <paramref name="windows"/> names (the file checks are this machine's) — the Windows system drive is not consulted.</summary>
    public static ResolvedExecutable Resolve(string name, string? pathVariable, bool windows) =>
        Resolve(name, pathVariable, windows, NotConsulted);

    /// <summary>
    /// Resolves <paramref name="name"/> against <paramref name="pathVariable"/>; when PATH does not hold it, the platform is
    /// Linux and the name is one of <see cref="WindowsSystemDrive.Programs"/>, it is looked for in its folder on the Windows
    /// system drive mounted where <paramref name="systemDriveMountPoint"/> says — asked only then, so the mount table is read
    /// for no other tool. A copy found there must be a regular file (never a symbolic link), carry an execute bit and start
    /// with the <c>MZ</c> header of a Windows program: only WSL interop runs such a file, on Windows as the Windows user,
    /// where an ELF or a script under that name would run natively — as root, under the service.
    /// </summary>
    public static ResolvedExecutable Resolve(string name, string? pathVariable, bool windows, Func<Reading<string>> systemDriveMountPoint)
    {
        if (Path.IsPathFullyQualified(name))
        {
            return new ResolvedExecutable.Found(name);
        }

        if (HasADirectory(name, windows))
        {
            return new ResolvedExecutable.NotFound($"{name} is a relative path; only a bare name (looked up on PATH) or an absolute path is started");
        }

        var onPath = ResolveIn(name, Directories(pathVariable, windows), windows);
        return onPath is ResolvedExecutable.NotFound notOnPath ? OrOnTheSystemDrive(name, windows, systemDriveMountPoint, notOnPath) : onPath;
    }

    /// <summary>
    /// <paramref name="name"/> — a BARE name — looked up in <paramref name="directories"/>, in order, by the same rules: what
    /// the target user's tool is resolved against (a fixed list of that user's bin folders, plan §15c #2), where a
    /// <c>PATH</c> string would split a Windows sandbox path at its drive letter. Relative directories are skipped.
    /// </summary>
    public static ResolvedExecutable ResolveIn(string name, IReadOnlyList<string> directories, bool windows)
    {
        if (!IsBareName(name, windows))
        {
            return new ResolvedExecutable.NotFound($"\"{name}\" is not a bare name; only a bare name is looked up in a list of folders");
        }

        var usable = directories.Where(IsAbsoluteFolder).ToList();
        var found = usable.SelectMany(d => Candidates(name, windows).Select(c => Path.Combine(d, c))).FirstOrDefault(c => IsStartable(c, windows));
        return found is not null
            ? new ResolvedExecutable.Found(found)
            : new ResolvedExecutable.NotFound(NotFoundReason(name, usable.Count, windows));
    }

    private static bool IsBareName(string name, bool windows) => name.Length > 0 && !HasADirectory(name, windows) && !Path.IsPathFullyQualified(name);

    private static bool IsAbsoluteFolder(string directory) => directory.Length > 0 && Path.IsPathFullyQualified(directory);

    /// <summary>A relative path rather than a bare name: <c>./docker</c>, <c>bin\docker</c>, <c>C:docker</c>.</summary>
    private static bool HasADirectory(string name, bool windows) =>
        name.Contains('/') || (windows && name.IndexOfAny(['\\', ':']) >= 0);

    /// <summary>The usable <c>PATH</c> entries, in order: trimmed, unquoted, empty and relative ones dropped.</summary>
    private static IReadOnlyList<string> Directories(string? pathVariable, bool windows) =>
        [.. (pathVariable ?? string.Empty)
            .Split(windows ? ';' : ':')
            .Select(e => windows ? e.Trim().Trim('"') : e)
            .Where(e => e.Length > 0 && Path.IsPathFullyQualified(e))];

    private static IReadOnlyList<string> Candidates(string name, bool windows) =>
        !windows || WindowsExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            ? [name]
            : [.. WindowsExtensions.Select(e => name + e)];

    private static bool IsStartable(string candidate, bool windows)
    {
        try
        {
            return File.Exists(candidate) && MayExecute(candidate, windows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A directory this account cannot inspect holds nothing it may start.
            return false;
        }
    }

    /// <summary>Windows starts any existing <c>.exe</c>; Linux needs an execute bit (the closest the file mode says to <c>access(X_OK)</c>).</summary>
    private static bool MayExecute(string candidate, bool windows) =>
        windows || OperatingSystem.IsWindows() || HasExecuteBit(candidate);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool HasExecuteBit(string candidate) =>
        (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>What a copy on the Windows system drive must be, in the order a refusal names the first it fails. The link
    /// check comes first: a link is refused whatever it points at.</summary>
    private static readonly (Func<FileInfo, bool> Fails, string Problem)[] SystemDriveChecks =
    [
        (f => f.LinkTarget is not null, "is a symbolic link, which is never followed there"),
        (f => !f.Exists, "does not exist"),
        (f => !MayExecute(f.FullName, windows: false), "has no execute bit"),
        (f => !StartsWithTheWindowsProgramHeader(f.FullName), "is not a Windows program (no MZ header); only WSL interop may start a file found there"),
    ];

    /// <summary>For the 3-argument overload: PATH alone.</summary>
    private static Reading<string> NotConsulted() => Reading.Missing<string>("this lookup consults PATH alone");

    /// <summary>The system drive's copy when PATH had none and the name is a Windows program the product starts from the
    /// distro; <paramref name="notOnPath"/> unchanged for every other name, and always on Windows.</summary>
    private static ResolvedExecutable OrOnTheSystemDrive(string name, bool windows, Func<Reading<string>> mountPoint, ResolvedExecutable.NotFound notOnPath) =>
        !windows && WindowsSystemDrive.Programs.TryGetValue(name, out var folder)
            ? OnTheSystemDrive(name, folder, mountPoint(), notOnPath.Reason)
            : notOnPath;

    private static ResolvedExecutable OnTheSystemDrive(string name, string folder, Reading<string> mountPoint, string notOnPath) => mountPoint switch
    {
        Reading<string>.Available { Value: var root } => CheckedOnTheSystemDrive(Path.Combine(root, folder, name), notOnPath),
        _ => new ResolvedExecutable.NotFound($"{notOnPath}; the Windows system drive was not searched: {mountPoint.ReasonOrEmpty}"),
    };

    private static ResolvedExecutable CheckedOnTheSystemDrive(string candidate, string notOnPath) =>
        SystemDriveProblem(candidate) is { Length: > 0 } problem
            ? new ResolvedExecutable.NotFound($"{notOnPath}; on the Windows system drive {candidate} {problem}")
            : new ResolvedExecutable.Found(candidate);

    /// <summary>Why <paramref name="candidate"/> is not started, or empty when it may be.</summary>
    private static string SystemDriveProblem(string candidate)
    {
        try
        {
            var file = new FileInfo(candidate);
            return SystemDriveChecks.FirstOrDefault(c => c.Fails(file)).Problem ?? string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"could not be inspected: {e.Message}";
        }
    }

    /// <summary>The two bytes every Windows executable starts with (the DOS header's <c>MZ</c>) — what WSL's interop
    /// handler is registered for, so such a file never runs as a Linux program.</summary>
    private static bool StartsWithTheWindowsProgramHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[2];
        return stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }

    private static string NotFoundReason(string name, int searched, bool windows) =>
        searched == 0
            ? $"{name} was not found: PATH holds no absolute directory to search"
            : $"{name} was not found on PATH ({searched} {(searched == 1 ? "directory" : "directories")} searched{(windows ? ", .exe and .com only" : ", executable files only")})";
}
