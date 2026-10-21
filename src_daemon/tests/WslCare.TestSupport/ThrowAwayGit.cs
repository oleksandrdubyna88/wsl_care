using System.Diagnostics;
using System.Text;

namespace WslCare.TestSupport;

/// <summary>
/// A folder of this test's own — a <see cref="ToDeleteRoot"/> (named <c>testsToDelete-…</c>, so the family's sweep finds a leftover) — removed on dispose (E19.S4, decision 6: a real <c>git.exe</c> runs in tests ONLY against a
/// throw-away repository the test creates and removes). The removal clears read-only attributes first — git writes its object files
/// read-only, and a plain recursive delete refuses them on Windows.
/// </summary>
public class ThrowAwayFolder : IDisposable
{
    private readonly ToDeleteRoot _root;

    public ThrowAwayFolder(string purpose)
    {
        _root = new ToDeleteRoot(purpose);
    }

    public string Path => _root.Path;

    /// <summary>The folder that holds this one — a root every folder of a test may be placed beneath.</summary>
    public string Parent => System.IO.Path.GetDirectoryName(Path)!;

    public string Under(string relativePath) => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Writes <paramref name="content"/> (UTF-8, no BOM) to <paramref name="relativePath"/>, creating folders.</summary>
    public string Write(string relativePath, string content)
    {
        var full = Under(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(Path))
        {
            ClearReadOnly(Path);
        }

        _root.Dispose();
    }

    private static void ClearReadOnly(string folder)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}

/// <summary>The real <c>git.exe</c> as a test arranges a throw-away repository with it — never the product's own launcher.</summary>
public static class RealGit
{
    /// <summary>The Program Files install; empty when this machine has none (the test then skips).</summary>
    public static string Executable { get; } = Locate();

    public static bool Available => OperatingSystem.IsWindows() && Executable.Length > 0;

    private static string Locate()
    {
        var root = Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty;
        var path = System.IO.Path.Combine(root, "Git", "cmd", "git.exe");
        return root.Length > 0 && File.Exists(path) ? path : string.Empty;
    }

    /// <summary>Runs git in <paramref name="workingDirectory"/> with this process's environment (plus <paramref name="extra"/>) and answers
    /// what it printed. For arranging a throw-away repository and for the controls that show a leak would be real.</summary>
    public static (int ExitCode, string Stdout, string Stderr) Run(string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? extra = null)
    {
        var info = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', arguments)} did not end in 60 s");
        }

        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>A repository <c>git init</c> made inside a <see cref="ThrowAwayFolder"/>. Nothing is committed (no identity is ever set): a
/// file is TRACKED by being in the index, which is all <c>ls-files --cached</c> reads.</summary>
public sealed class ThrowAwayGitRepo : ThrowAwayFolder
{
    private static readonly Dictionary<string, string> Isolated = new() { ["GIT_CONFIG_GLOBAL"] = "NUL", ["GIT_CONFIG_NOSYSTEM"] = "1" };

    public ThrowAwayGitRepo(string purpose)
        : base(purpose)
    {
        Git("init", "-q");
    }

    public string IndexFile => Under(".git/index");

    /// <summary>Stages <paramref name="relativePath"/> (<c>-f</c> when it is ignored).</summary>
    public void Track(string relativePath, bool force = false)
    {
        Git(force ? ["add", "-f", "--", relativePath] : ["add", "--", relativePath]);
    }

    public (int ExitCode, string Stdout, string Stderr) Git(params string[] arguments)
    {
        var result = RealGit.Run(Path, arguments, Isolated);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} in the throw-away repository failed ({result.ExitCode}): {result.Stderr}");
        }

        return result;
    }
}
