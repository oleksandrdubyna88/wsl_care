using System.Reflection;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// One scenario's world: a temporary directory holding the CLI's <c>WSL_CARE_ROOT</c>, a folder of
/// fake tools that is the CLI's WHOLE <c>PATH</c>, the fakes' argv log and their script.
/// </summary>
/// <remarks>
/// <para>The PATH holds the fakes and nothing else, so a scenario can reach only the tools it fakes:
/// a verb that shells out to an unfaked tool fails to start it rather than reaching the real one on
/// this machine (the ubuntu runner has a real <c>docker</c>; the owner's machine has everything).
/// <c>FakeToolFlows</c> proves both halves.</para>
/// <para>Every path the CLI uses is under <see cref="SandboxRoot"/> through <c>WSL_CARE_ROOT</c>; the
/// real profile, <c>/etc</c>, <c>/var</c> and the real log folder are never read or written.</para>
/// </remarks>
internal sealed class ScenarioHome : IDisposable
{
    private readonly TempRoot _root;
    private readonly List<FakeAnswer> _answers = [];

    public ScenarioHome(string purpose)
    {
        _root = new TempRoot($"scn-{purpose}");
        SandboxRoot = _root.Dir("root");
        FakeBin = _root.Dir("fakebin");
        CallsFile = _root.Under("fake-calls.jsonl");
        ScriptFile = _root.Under("fake-script.json");
        Paths = HostPaths.ForThisMachine(SandboxRoot);
        InstallFakes(FakeBin);
        FakeScript.Write(ScriptFile, _answers);
    }

    /// <summary>The value of <c>WSL_CARE_ROOT</c> for every run in this scenario.</summary>
    public string SandboxRoot { get; }

    /// <summary>The folder of fakes — the whole <c>PATH</c> of every run.</summary>
    public string FakeBin { get; }

    public string CallsFile { get; }

    public string ScriptFile { get; }

    /// <summary>The layout the CLI computes from the same root — what assertions read files through,
    /// rather than guessing where the CLI put them.</summary>
    public IHostPaths Paths { get; }

    /// <summary>The working directory of every run: the scenario's own folder, never the repository.</summary>
    public string WorkingDirectory => _root.Path;

    /// <summary>The environment every run gets, on top of this process's (which keeps
    /// <c>DOTNET_ROOT</c>, so the framework-dependent apphosts still find the runtime).</summary>
    public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [HostPaths.SandboxRootVariable] = SandboxRoot,
        // "PATH" on both families: ProcessStartInfo.Environment is case-insensitive on Windows, so
        // this replaces "Path" there rather than adding a second entry.
        ["PATH"] = FakeBin,
        [FakeToolProtocol.CallsVariable] = CallsFile,
        [FakeToolProtocol.ScriptVariable] = ScriptFile,
    };

    /// <summary>Every fake invocation so far, in order.</summary>
    public IReadOnlyList<FakeCall> Calls => FakeCallLog.ReadAll(CallsFile);

    /// <summary>Runs the BUILT <c>wsl-care</c> with this scenario's environment.</summary>
    public Task<ChildResult> RunAsync(params string[] args) => RunAsync((IReadOnlyList<string>)args);

    public Task<ChildResult> RunAsync(IReadOnlyList<string> args) =>
        ChildProcess.RunAsync(ChildProcess.BesideTheTests(CommandLine.BinaryName), args, Environment, WorkingDirectory);

    /// <summary>Scripts what <paramref name="tool"/> answers to exactly <paramref name="argv"/>.</summary>
    public ScenarioHome Script(string tool, IReadOnlyList<string> argv, int exitCode, string stdoutFixture = "", string stderr = "")
    {
        _answers.Add(new FakeAnswer(tool, argv, exitCode, stdoutFixture.Length == 0 ? string.Empty : Fixture(stdoutFixture), stderr));
        FakeScript.Write(ScriptFile, _answers);
        return this;
    }

    /// <summary>The absolute path of a file under <c>fixtures/</c>, copied beside the harness.</summary>
    public static string Fixture(string relativePath)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"fixture {relativePath} is not beside the harness at {path}", path);
        }

        return path;
    }

    /// <summary>A path the build stamped into this assembly (<c>AssemblyMetadata</c> in the project file).</summary>
    public static string Stamped(string key) =>
        typeof(ScenarioHome).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
            ?? throw new InvalidOperationException($"the harness project stamps {key} at build time");

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// One apphost, three names: a renamed apphost still loads <c>wsl-care-fake-tool.dll</c> (the name
    /// is embedded in it), so the dll, its runtime config and its deps file go beside the copies.
    /// </summary>
    private static void InstallFakes(string bin)
    {
        const string fake = "wsl-care-fake-tool";
        var apphost = ChildProcess.BesideTheTests(fake);
        foreach (var companion in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        {
            File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, fake + companion), System.IO.Path.Combine(bin, fake + companion));
        }

        foreach (var tool in FakeToolProtocol.Tools)
        {
            var target = System.IO.Path.Combine(bin, tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            File.Copy(apphost, target);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
