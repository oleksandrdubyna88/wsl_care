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

    /// <param name="purpose">Names the temporary directory.</param>
    /// <param name="tools">The fakes to install; every tool of the protocol when omitted. A scenario leaves one out
    /// to stand for a tool that is not installed — then nothing on the PATH answers to that name.</param>
    public ScenarioHome(string purpose, IReadOnlyList<string>? tools = null)
    {
        _root = new TempRoot($"scn-{purpose}");
        SandboxRoot = _root.Dir("root");
        FakeBin = _root.Dir("fakebin");
        CallsFile = _root.Under("fake-calls.jsonl");
        ScriptFile = _root.Under("fake-script.json");
        Paths = HostPaths.ForThisMachine(SandboxRoot);
        InstallFakes(FakeBin, tools ?? FakeToolProtocol.Tools);
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
        // Removed, so the CLI meets the lookup a default Windows session has: when this variable is set (an agent's
        // shell sets it), CreateProcess skips the current directory for a bare name, and a scenario run from such a
        // shell would hide exactly the lookup ToolResolutionFlows exists to catch. Meaningless on Linux.
        [NoCurrentDirectoryLookupVariable] = null,
    };

    /// <summary>The Windows variable that makes <c>CreateProcess</c> skip the current directory for a bare name.</summary>
    internal const string NoCurrentDirectoryLookupVariable = "NoDefaultCurrentDirectoryInExePath";

    /// <summary>Every fake invocation so far, in order.</summary>
    public IReadOnlyList<FakeCall> Calls => FakeCallLog.ReadAll(CallsFile);

    /// <summary>Runs the BUILT <c>wsl-care</c> with this scenario's environment.</summary>
    public Task<ChildResult> RunAsync(params string[] args) => RunAsync((IReadOnlyList<string>)args);

    public Task<ChildResult> RunAsync(IReadOnlyList<string> args) =>
        ChildProcess.RunAsync(ChildProcess.BesideTheTests(CommandLine.BinaryName), args, Environment, WorkingDirectory);

    /// <summary>Scripts what <paramref name="tool"/> answers to exactly <paramref name="argv"/>.</summary>
    public ScenarioHome Script(string tool, IReadOnlyList<string> argv, int exitCode, string stdoutFixture = "", string stderr = "", int delayMilliseconds = 0)
    {
        _answers.Add(new FakeAnswer(tool, argv, exitCode, stdoutFixture.Length == 0 ? string.Empty : Fixture(stdoutFixture), stderr, delayMilliseconds));
        FakeScript.Write(ScriptFile, _answers);
        return this;
    }

    /// <summary>Scripts one answer as given — its <c>StdoutFile</c> an ABSOLUTE path (a fixture through <see cref="Fixture"/>,
    /// or a file the scenario wrote with <see cref="WriteFile"/>), with the prefix / up-to / hang-after options of E2.S3.</summary>
    public ScenarioHome Answer(FakeAnswer answer)
    {
        _answers.Add(answer);
        FakeScript.Write(ScriptFile, _answers);
        return this;
    }

    /// <summary>A file of the scenario's own (outside the sandbox root) — a scripted answer's stdout.</summary>
    public string WriteFile(string name, string content) => _root.File(name, content);

    /// <summary>Starts the BUILT <c>wsl-care</c> and leaves it running — for a verb that runs until a signal.</summary>
    public RunningChild Start(params string[] args) =>
        RunningChild.Start(ChildProcess.BesideTheTests(CommandLine.BinaryName), args, Environment, WorkingDirectory);

    /// <summary>
    /// Plants a copy of the fake under <paramref name="tool"/>'s name in the scenario's WORKING DIRECTORY — the CLI's
    /// current directory, which is NOT on its <c>PATH</c>. A launcher that lets the operating system resolve a bare
    /// name finds it there first (Windows <c>CreateProcess</c> and .NET's Unix lookup both search the current
    /// directory before <c>PATH</c>); the product must not. Its calls record this folder as their location.
    /// </summary>
    public string PlantDecoyInWorkingDirectory(string tool)
    {
        InstallFakes(WorkingDirectory, [tool]);
        return WorkingDirectory;
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
    /// One apphost, one name per tool: a renamed apphost still loads <c>wsl-care-fake-tool.dll</c> (the name
    /// is embedded in it), so the dll, its runtime config and its deps file go beside the copies.
    /// </summary>
    private static void InstallFakes(string bin, IReadOnlyList<string> tools)
    {
        const string fake = "wsl-care-fake-tool";
        var apphost = ChildProcess.BesideTheTests(fake);
        foreach (var companion in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        {
            File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, fake + companion), System.IO.Path.Combine(bin, fake + companion));
        }

        foreach (var tool in tools)
        {
            var target = System.IO.Path.Combine(bin, FakeToolProtocol.FileName(tool, OperatingSystem.IsWindows()));
            File.Copy(apphost, target);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
