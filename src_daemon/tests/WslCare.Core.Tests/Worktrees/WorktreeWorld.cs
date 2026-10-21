using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// A Windows layout and a root of worktrees laid out under a <c>testsToDelete-</c> folder, with the product's own file system over it:
/// main clones, worktrees whose admin folders point back, orphans, WSL-made pointers, build output. The Windows legs only (the report
/// reads Windows paths and reparse points); every folder it makes is removed on dispose.
/// </summary>
internal sealed class WorktreeWorld : IDisposable
{
    public const string WindowsOnly = "the worktree report reads Windows drive paths and reparse points: the Windows legs";

    public WorktreeWorld(string purpose)
    {
        Sandbox = new ToDeleteRoot($"wt-{purpose}");
        Paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(Sandbox.Path));
        Files = new PhysicalFileSystem(Paths) { OwnersAreThisProcess = true };
        Work = Sandbox.Dir("work");
        Now = DateTimeOffset.UtcNow;
        Clock = new FixedTimeProvider(Now);
    }

    public ToDeleteRoot Sandbox { get; }

    public WindowsHostPaths Paths { get; }

    public PhysicalFileSystem Files { get; }

    /// <summary>The root the scans look at: <c>&lt;sandbox&gt;\work</c>.</summary>
    public string Work { get; }

    public DateTimeOffset Now { get; }

    public FixedTimeProvider Clock { get; }

    public TimeSpan Old { get; } = TimeSpan.FromDays(30);

    public static WorktreeSettings DefaultSettings() => WorktreeSettings.From(ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config);

    public WorktreeScanner Scanner(Func<char, DriveType>? drives = null, IFileSystem? files = null) =>
        new(Paths, files ?? Files, Clock, drives ?? (_ => DriveType.Fixed));

    /// <summary>A main clone: <c>work\&lt;name&gt;\.git\</c> a folder, and a project beside nothing.</summary>
    public string Main(string name)
    {
        Sandbox.Dir($"work/{name}/.git/worktrees");
        Sandbox.File($"work/{name}/.git/HEAD", "ref: refs/heads/main\n");
        Sandbox.File($"work/{name}/README.md", "main clone\n");
        return Path.Combine(Work, name);
    }

    /// <summary>A worktree <c>work\&lt;name&gt;</c> of main clone <paramref name="main"/>: its <c>.git</c> file names the admin folder, whose
    /// <c>gitdir</c> names the file back (git writes forward slashes); an admin <c>HEAD</c>, <c>index</c> and <c>logs/HEAD</c>.</summary>
    public string Worktree(string name, string main = "main", string? pointerSpelling = null, string? backPointer = null)
    {
        var admin = Path.Combine(Work, main, ".git", "worktrees", name);
        var folder = Path.Combine(Work, name);
        Sandbox.Dir($"work/{name}/src");
        Sandbox.File($"work/{name}/src/Program.cs", "class P {}\n");
        Sandbox.File($"work/{name}/.git", $"gitdir: {pointerSpelling ?? Forward(admin)}\n");
        Sandbox.File($"work/{main}/.git/worktrees/{name}/gitdir", $"{backPointer ?? Forward(Path.Combine(folder, ".git"))}\n");
        Sandbox.File($"work/{main}/.git/worktrees/{name}/HEAD", "ref: refs/heads/feature\n");
        Sandbox.File($"work/{main}/.git/worktrees/{name}/index", "index");
        Sandbox.File($"work/{main}/.git/worktrees/{name}/logs/HEAD", "log\n");
        return folder;
    }

    /// <summary>A folder whose <c>.git</c> file is the pointer text given — whatever it names.</summary>
    public string Pointing(string name, string gitFileText)
    {
        Sandbox.File($"work/{name}/.git", gitFileText);
        Sandbox.File($"work/{name}/src/Program.cs", "class P {}\n");
        return Path.Combine(Work, name);
    }

    public string PlainFolder(string name)
    {
        Sandbox.File($"work/{name}/notes.txt", "not a repository\n");
        return Path.Combine(Work, name);
    }

    public string AdminOf(string name, string main = "main") => Path.Combine(Work, main, ".git", "worktrees", name);

    /// <summary>A project file beside a <c>bin</c> holding build artifacts: <c>&lt;folder&gt;\App.csproj</c>, <c>bin\Debug\net10.0\App.dll</c> and <c>.pdb</c>.</summary>
    public string BuildOutput(string folder, string project = "App.csproj", string kind = "bin")
    {
        var relative = RelativeToSandbox(folder);
        Sandbox.File($"{relative}/{project}", "<Project />\n");
        Sandbox.File($"{relative}/{kind}/Debug/net10.0/App.dll", new string('d', 1000));
        Sandbox.File($"{relative}/{kind}/Debug/net10.0/App.pdb", new string('p', 500));
        Sandbox.File($"{relative}/{kind}/Debug/net10.0/App.deps.json", "{}");
        return Path.Combine(folder, kind);
    }

    /// <summary>Stamps every file and folder at or under <paramref name="path"/> <see cref="Old"/> before now (files first, folders bottom-up).</summary>
    public void Age(string path) => Age(path, Old);

    public void Age(string path, TimeSpan age)
    {
        var when = (Now - age).UtcDateTime;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        {
            Stamp(entry, when);
        }

        Stamp(path, when);
    }

    /// <summary>One file or folder written <paramref name="ago"/> before now.</summary>
    public void Stamp(string path, TimeSpan ago) => Stamp(path, (Now - ago).UtcDateTime);

    private static void Stamp(string path, DateTime when)
    {
        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, when);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, when);
        }
    }

    public static string Forward(string path) => path.Replace('\\', '/');

    private string RelativeToSandbox(string folder) => Path.GetRelativePath(Sandbox.Path, folder).Replace('\\', '/');

    public void Dispose() => Sandbox.Dispose();
}
