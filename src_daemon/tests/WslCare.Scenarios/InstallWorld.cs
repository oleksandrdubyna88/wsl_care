using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using WslCare.Core.Doctor;
using WslCare.Core.Json;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// One run of <c>install.sh</c>'s world (E4.S1): the REAL script, run by the real <c>/bin/sh</c>, over a temporary
/// prefix (<c>WSL_CARE_INSTALL_ROOT</c>) that stands in for <c>/</c>, with every tool that would change the machine or
/// reach the network faked on its <c>PATH</c>.
/// </summary>
/// <remarks>
/// <para>The <c>PATH</c> is two folders, nothing else. <c>fakebin/</c> holds the fake tool under the names of everything
/// that could change this machine or reach the network — <see cref="FakedTools"/>: curl, gh, systemctl, apt-get,
/// debconf, runuser, sudo, and the identity questions (<c>id</c>, <c>uname</c>) so a test can be root, or arm64,
/// without being either. <c>realbin/</c> holds links to <see cref="RealTools"/> — the text and file tools the script
/// runs on its own temporary folder and the prefix (tar, sha256sum, install, ln, rm …). A command outside both lists
/// is not found, so a script change that reaches for a new tool fails here first, on purpose.</para>
/// <para>Nothing real is touched: every file the script writes is under <see cref="Root"/> or its temporary folder
/// (<c>TMPDIR</c> is the world's own), and it is run as the test user — a path that escaped the prefix would be
/// refused by the operating system before it could change <c>/etc</c> or <c>/opt</c>.</para>
/// <para>The released binary is a two-line shell stub that appends the path it was started as to a log, then hands its
/// argv to a fake named <c>wsl-care</c> — so a test sees both that the installer ran the binary by its ABSOLUTE path
/// and what it asked of it.</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class InstallWorld : IDisposable
{
    public const string Repo = "oleksandrdubyna88/wsl_care";
    public const string SignerWorkflow = Repo + "/.github/workflows/release.yml";
    public const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=100";
    public const string NewestDaemon = "0.1.0";
    public const string BinaryPath = "/opt/wsl-care/bin/wsl-care";
    public const string LinkPath = "/usr/local/bin/wsl-care";

    /// <summary>The tools faked on the script's PATH: everything that changes the machine, reaches the network, or
    /// answers who and where the script runs.</summary>
    public static readonly IReadOnlyList<string> FakedTools =
        ["curl", "gh", "systemctl", "apt-get", "debconf-set-selections", "dpkg-reconfigure", "runuser", "sudo", "id", "uname", "sar", "atop"];

    /// <summary>The real tools linked onto the script's PATH: text and file tools only, acting on the temporary folder
    /// and the prefix.</summary>
    public static readonly IReadOnlyList<string> RealTools =
        ["awk", "cat", "chmod", "cut", "grep", "gzip", "install", "ln", "ls", "mkdir", "mktemp", "mv", "readlink", "rm", "rmdir", "sed", "sha256sum", "sleep", "tar", "timeout", "tr"];

    private readonly TempRoot _root;
    private readonly List<FakeAnswer> _answers = [];

    public InstallWorld(string purpose, IReadOnlyList<string>? withoutTools = null)
    {
        _root = new TempRoot($"install-{purpose}");
        Root = _root.Dir("root");
        FakeBin = _root.Dir("fakebin");
        RealBin = _root.Dir("realbin");
        StubBin = _root.Dir("stubbin");
        Temp = _root.Dir("tmp");
        CallsFile = _root.Under("fake-calls.jsonl");
        ScriptFile = _root.Under("fake-script.json");
        StubLog = _root.Under("stub-invocations.log");
        ScenarioHome.InstallFakes(FakeBin, [.. FakedTools.Except(withoutTools ?? [])]);
        ScenarioHome.InstallFakes(StubBin, ["wsl-care"]);
        LinkRealTools(RealBin);
        SeedMachine();
        ScriptTheHappyPath();
    }

    /// <summary>The value of <c>WSL_CARE_INSTALL_ROOT</c>: the stand-in for <c>/</c>.</summary>
    public string Root { get; }

    public string FakeBin { get; }

    public string RealBin { get; }

    public string StubBin { get; }

    /// <summary>The script's <c>TMPDIR</c>: its temporary folder is made here, and must be gone after every run.</summary>
    public string Temp { get; }

    public string CallsFile { get; }

    public string ScriptFile { get; }

    public string StubLog { get; }

    /// <summary>The <c>SUDO_USER</c> the script sees; none unless a test sets it.</summary>
    public string SudoUser { get; set; } = string.Empty;

    public IReadOnlyList<FakeCall> Calls => FakeCallLog.ReadAll(CallsFile);

    public IReadOnlyList<FakeCall> CallsOf(string tool) => [.. Calls.Where(c => c.Tool == tool)];

    /// <summary>Every path the installed binary was started as, in order.</summary>
    public IReadOnlyList<string> StubInvocations => File.Exists(StubLog) ? [.. File.ReadAllLines(StubLog)] : [];

    /// <summary>A path under the prefix, written as the absolute path it stands for (<c>/etc/wsl.conf</c>).</summary>
    public string At(string absolute) => Path.Combine(Root, absolute.TrimStart('/'));

    public void Write(string absolute, string content)
    {
        var path = At(absolute);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static string ReleaseName(string version, string rid) => $"wsl-care-{version}-{rid}";

    public static string ReleaseUrl(string version, string file) => $"https://github.com/{Repo}/releases/download/daemon-v{version}/{file}";

    /// <summary>Puts <paramref name="answer"/> in FRONT of every answer scripted so far: the fake takes the first match.</summary>
    public InstallWorld Override(FakeAnswer answer)
    {
        _answers.Insert(0, answer);
        FakeScript.Write(ScriptFile, _answers);
        return this;
    }

    public InstallWorld Override(string tool, IReadOnlyList<string> argv, int exitCode, string stdout = "", bool prefix = false) =>
        Override(new FakeAnswer(tool, argv, exitCode, stdout.Length == 0 ? string.Empty : _root.File($"answers/{Guid.NewGuid():N}.out", stdout), string.Empty) { Prefix = prefix });

    /// <summary>Publishes a release the fake curl serves: its archive (built by <paramref name="build"/>, or the real one)
    /// and its <c>.sha256</c> (<paramref name="sha256Line"/> replaces the true line when given).</summary>
    public void Publish(string version, string rid, Action<TarWriter, string>? build = null, string? sha256Line = null)
    {
        var name = ReleaseName(version, rid);
        var archive = _root.Under($"release/{name}.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false))
        {
            (build ?? WriteRealRelease)(tar, name);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
        var sha = _root.File($"release/{name}.tar.gz.sha256", sha256Line ?? $"{hash}  {name}.tar.gz\n");
        Override(Download(ReleaseUrl(version, $"{name}.tar.gz"), archive));
        Override(Download(ReleaseUrl(version, $"{name}.tar.gz.sha256"), sha));
    }

    /// <summary>Publishes a release whose archive and <c>.sha256</c> were made elsewhere — by the release workflow's own
    /// packaging script (E4.S2, <c>PackageFlows</c>) — served by the fake curl exactly as GitHub would serve them.</summary>
    public void PublishFiles(string version, string rid, string archive, string sha256File)
    {
        var name = ReleaseName(version, rid);
        Override(Download(ReleaseUrl(version, $"{name}.tar.gz"), archive));
        Override(Download(ReleaseUrl(version, $"{name}.tar.gz.sha256"), sha256File));
    }

    /// <summary>A curl answer: the file's bytes written where the script's <c>--output</c> says.</summary>
    public static FakeAnswer Download(string url, string file) =>
        new("curl", ["--url", url], 0, file, string.Empty) { Prefix = true, OutputFlag = "--output" };

    /// <summary>The archive a release would carry (plan §15e #1): the binary, the three units and the machine layer from
    /// this repository, under one top folder.</summary>
    public void WriteRealRelease(TarWriter tar, string name)
    {
        AddDirectory(tar, $"{name}/");
        AddFile(tar, $"{name}/wsl-care", Encoding.UTF8.GetBytes(StubScript()), Executable);
        AddDirectory(tar, $"{name}/systemd/");
        foreach (var unit in ShippedFiles.UnitNames)
        {
            AddFile(tar, $"{name}/systemd/{unit}", File.ReadAllBytes(Path.Combine(ShippedFiles.SystemdDirectory, unit)), Regular);
        }

        AddDirectory(tar, $"{name}/config/");
        AddFile(tar, $"{name}/config/machine.json", File.ReadAllBytes(ShippedFiles.MachineConfig), Regular);
    }

    public const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public const UnixFileMode Regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static void AddDirectory(TarWriter tar, string name) =>
        tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, name) { Mode = Executable });

    public static void AddFile(TarWriter tar, string name, byte[] bytes, UnixFileMode mode) =>
        tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = new MemoryStream(bytes) });

    /// <summary>The bytes of the binary a release carries here: the stub.</summary>
    public string StubScript() =>
        $"#!/bin/sh\nprintf '%s\\n' \"$0\" >> '{StubLog}'\nexec '{Path.Combine(StubBin, "wsl-care")}' \"$@\"\n";

    /// <summary>What <c>doctor --json</c> prints, written by the product's own serializer — never a guessed shape.</summary>
    public static string DoctorJson(bool healthy) =>
        JsonSerializer.Serialize(
            new DoctorReport(
                1,
                "wsl",
                new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
                healthy,
                false,
                [],
                [new DoctorCheck("eventsFollower", healthy ? DoctorRun.Ok : DoctorRun.Problem, healthy ? "container starts recorded" : "the events follower has recorded nothing")],
                [new VersionReport("wsl-care", true, NewestDaemon, null)]),
            WslCareJsonContext.Default.DoctorReport);

    /// <summary>Runs the real <c>install.sh</c> with <paramref name="args"/> under this world.</summary>
    public Task<ChildResult> RunAsync(params string[] args) =>
        ChildProcess.RunAsync("/bin/sh", [ShippedFiles.InstallScript, .. args], Environment, _root.Path, TimeSpan.FromSeconds(60));

    public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["PATH"] = $"{FakeBin}:{RealBin}",
        ["WSL_CARE_INSTALL_ROOT"] = Root,
        ["WSL_CARE_INSTALL_DOCTOR_SECONDS"] = "0",
        ["TMPDIR"] = Temp,
        [FakeToolProtocol.CallsVariable] = CallsFile,
        [FakeToolProtocol.ScriptVariable] = ScriptFile,
        ["SUDO_USER"] = SudoUser.Length == 0 ? null : SudoUser,
    };

    /// <summary>Every entry under the prefix: path → kind, mode, link target or content hash. Equal before and after a
    /// run = the run changed nothing there.</summary>
    public IReadOnlyDictionary<string, string> Tree() =>
        new DirectoryInfo(Root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .ToDictionary(i => Path.GetRelativePath(Root, i.FullName), Describe, StringComparer.Ordinal);

    public void Dispose() => _root.Dispose();

    private static string Describe(FileSystemInfo info) => info switch
    {
        _ when info.LinkTarget is { } target => $"link -> {target}",
        DirectoryInfo => $"dir {File.GetUnixFileMode(info.FullName)}",
        _ => $"file {File.GetUnixFileMode(info.FullName)} {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(info.FullName)))}",
    };

    private static void LinkRealTools(string bin)
    {
        foreach (var tool in RealTools)
        {
            var real = new[] { "/usr/bin", "/bin" }.Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException($"the installer's tests need the real {tool} in /usr/bin or /bin");
            File.CreateSymbolicLink(Path.Combine(bin, tool), real);
        }
    }

    /// <summary>The machine the default world installs on: systemd running, two login users, sysstat collecting.</summary>
    private void SeedMachine()
    {
        Directory.CreateDirectory(At("/run/systemd/system"));
        Write("/etc/passwd", "root:x:0:0:root:/root:/bin/bash\nalice:x:1000:1000:Alice:/home/alice:/bin/bash\nzed:x:1001:1001:Zed:/home/zed:/bin/bash\n");
        Write("/etc/default/sysstat", "# sysstat\nENABLED=\"true\"\n");
    }

    /// <summary>A root x86_64 machine, the newest daemon release published, every system call answering yes.</summary>
    private void ScriptTheHappyPath()
    {
        var answers = new List<FakeAnswer>
        {
            Answer("uname", ["-s"], "Linux\n"),
            Answer("uname", ["-m"], "x86_64\n"),
            Answer("id", ["-u"], "0\n"),
            Download(ReleasesApi, _root.File("release/releases.json", ReleasesJson())),
            new("gh", ["attestation", "verify", "--repo", Repo, "--signer-workflow", SignerWorkflow], 0, string.Empty, string.Empty) { Prefix = true },
            Answer("systemctl", ["daemon-reload"]),
            Answer("systemctl", ["enable", "--now", "wsl-care.timer", "wsl-care-events.service"]),
            Answer("systemctl", ["enable", "--now", "sysstat.service", "atop.service"]),
            Answer("systemctl", ["is-active", "--quiet", "wsl-care.timer"]),
            Answer("systemctl", ["is-active", "--quiet", "wsl-care-events.service"]),
            Answer("wsl-care", ["collect"], "collect: recorded\n"),
            Answer("wsl-care", ["doctor", "--json"], DoctorJson(healthy: true)),
            Answer("wsl-care", ["doctor"], "wsl-care doctor (wsl): healthy\n"),
        };
        _answers.AddRange(answers);
        FakeScript.Write(ScriptFile, _answers);
        Publish(NewestDaemon, "linux-x64");
    }

    private FakeAnswer Answer(string tool, IReadOnlyList<string> argv, string stdout = "") =>
        new(tool, argv, 0, stdout.Length == 0 ? string.Empty : _root.File($"answers/{Guid.NewGuid():N}.out", stdout), string.Empty);

    /// <summary>The releases API's answer, newest first: the EXTENSION's release on top — what releases/latest would
    /// hand out — then two daemon releases. Shaped like GitHub's (an array of objects, <c>tag_name</c> among others).</summary>
    private static string ReleasesJson() =>
        """
        [
          {
            "url": "https://api.github.com/repos/oleksandrdubyna88/wsl_care/releases/3",
            "tag_name": "extension-v0.2.0",
            "name": "extension 0.2.0",
            "draft": false,
            "prerelease": false
          },
          {
            "url": "https://api.github.com/repos/oleksandrdubyna88/wsl_care/releases/2",
            "tag_name": "daemon-v0.1.0",
            "name": "daemon 0.1.0",
            "draft": false,
            "prerelease": false
          },
          {
            "url": "https://api.github.com/repos/oleksandrdubyna88/wsl_care/releases/1",
            "tag_name": "daemon-v0.0.9",
            "name": "daemon 0.0.9",
            "draft": false,
            "prerelease": false
          }
        ]
        """;
}
