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
    public const string AttestationsApi = "https://api.github.com/repos/" + Repo + "/attestations/sha256:";
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
        ["awk", "cat", "chmod", "cut", "grep", "gzip", "head", "install", "ln", "ls", "mkdir", "mktemp", "mv", "od", "readlink", "rm", "rmdir", "sed", "sha256sum", "sleep", "sort", "tail", "tar", "timeout", "tr", "wc"];

    private readonly TempRoot _root;
    private readonly List<FakeAnswer> _answers = [];

    /// <summary>The variables every fake call records (<see cref="FakeToolProtocol.RecordEnvironmentVariable"/>): what the
    /// installer hands <c>gh</c> — whose configuration, whose cache, whose token.</summary>
    public static readonly IReadOnlyList<string> RecordedVariables =
        ["HOME", "GH_CONFIG_DIR", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>The certificate identity a genuine release attestation of <paramref name="version"/> carries: release.yml
    /// of this repository, run for the tag <c>daemon-v&lt;version&gt;</c>.</summary>
    public static string SignerIdentity(string version) => $"https://github.com/{SignerWorkflow}@refs/tags/daemon-v{version}";

    /// <summary>What a genuine release of <paramref name="version"/> is attested by.</summary>
    public static AttestationBundles.Signer GenuineSigner(string version) =>
        AttestationBundles.Signer.ReleaseWorkflow(Repo, $"refs/tags/daemon-v{version}");

    /// <param name="purpose">Names the world's temporary folder.</param>
    /// <param name="withoutTools">Faked tools left OFF the PATH — a tool that is not installed.</param>
    /// <param name="awk">The awk the script runs (<c>/usr/bin/mawk</c>, <c>/usr/bin/gawk</c>); empty = the system's
    /// <c>awk</c>. Ubuntu ships mawk, a runner image may point <c>awk</c> at gawk: the snappy decoder must give both the
    /// same bytes.</param>
    public InstallWorld(string purpose, IReadOnlyList<string>? withoutTools = null, string awk = "")
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
        LinkRealTools(RealBin, awk);
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

    /// <summary>A <c>GH_TOKEN</c> in the script's environment; none unless a test sets it.</summary>
    public string GhToken { get; set; } = string.Empty;

    /// <summary><c>WSL_CARE_INSTALL_RUN_WAIT_SECONDS</c>: how long an upgrade waits for a run in flight; the script's own
    /// 10 minutes unless a test sets it.</summary>
    public string RunWaitSeconds { get; set; } = string.Empty;

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

    /// <summary>A symbolic link at <paramref name="absolute"/> under the prefix, pointing at <paramref name="target"/>; its
    /// folder made by the TempRoot (the shared test helper).</summary>
    public void Link(string absolute, string target)
    {
        _root.Dir(Path.GetDirectoryName(Path.GetRelativePath(_root.Path, At(absolute)))!);
        File.CreateSymbolicLink(At(absolute), target);
    }

    public static string ReleaseName(string version, string rid) => $"wsl-care-{version}-{rid}";

    public static string ReleaseUrl(string version, string file) => $"https://github.com/{Repo}/releases/download/daemon-v{version}/{file}";

    /// <summary>A fixture file of this world holding <paramref name="content"/>, for an answer to serve.</summary>
    public string Answer(string name, string content) => _root.File($"answers/{name}", content);

    /// <summary>Puts <paramref name="answer"/> in FRONT of every answer scripted so far: the fake takes the first match.</summary>
    public InstallWorld Override(FakeAnswer answer)
    {
        _answers.Insert(0, answer);
        FakeScript.Write(ScriptFile, _answers);
        return this;
    }

    public InstallWorld Override(string tool, IReadOnlyList<string> argv, int exitCode, string stdout = "", bool prefix = false) =>
        Override(new FakeAnswer(tool, argv, exitCode, stdout.Length == 0 ? string.Empty : _root.File($"answers/{Guid.NewGuid():N}.out", stdout), string.Empty) { Prefix = prefix });

    /// <summary>Publishes a release the fake curl serves: its archive (built by <paramref name="build"/>, or the real one),
    /// its <c>.sha256</c> (<paramref name="sha256Line"/> replaces the true line when given) and its attestations — by
    /// default ONE, by release.yml at the version's tag on a hosted runner; <paramref name="signers"/> replaces them (an
    /// empty list: GitHub knows no attestation for the archive).</summary>
    /// <returns>The archive's SHA-256, the digest its attestations are looked up by.</returns>
    public string Publish(string version, string rid, Action<TarWriter, string>? build = null, string? sha256Line = null, IReadOnlyList<AttestationBundles.Signer>? signers = null)
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
        Attest(hash, signers ?? [GenuineSigner(version)]);
        return hash;
    }

    /// <summary>Serves, for the archive of SHA-256 <paramref name="digest"/>, one bundle per signer: the attestation API's
    /// answer naming each bundle's URL, and each URL's snappy-compressed bundle. The newest set is also what the fake gh
    /// verifies in its ONLINE mode (no <c>--bundle</c>) — what GitHub's API would hand an installer that asks gh to fetch.</summary>
    public void Attest(string digest, IReadOnlyList<AttestationBundles.Signer> signers) =>
        ServeAttestations(digest, [.. signers.Select(s => AttestationBundles.Snappy(AttestationBundles.Utf8(AttestationBundles.Bundle(s, digest))))], signers);

    /// <summary>Serves exactly these compressed bundles (a captured one, a corrupt one) for <paramref name="digest"/>;
    /// <paramref name="onlineSigners"/> is what the fake gh's online mode sees (the first signer's bundle).</summary>
    public void ServeAttestations(string digest, IReadOnlyList<byte[]> compressedBundles, IReadOnlyList<AttestationBundles.Signer>? onlineSigners = null)
    {
        var urls = new List<string>();
        for (var i = 0; i < compressedBundles.Count; i++)
        {
            var url = $"https://tmaproduction.blob.core.windows.net/attestations/1/{digest}-{i}.json.sn?se=2026-10-03T18%3A00%3A00Z&sig=a%2Bb%3D";
            var blob = _root.Under($"attestations/{digest}-{i}.json.sn");
            Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
            File.WriteAllBytes(blob, compressedBundles[i]);
            Override(Download(url, blob));
            urls.Add(url);
        }

        Override(Download(AttestationsApi + digest, _root.File($"attestations/{digest}.api.json", AttestationBundles.ApiAnswer(urls))));
        var online = onlineSigners is [var first, ..] ? _root.File($"attestations/{digest}.online.json", AttestationBundles.Bundle(first, digest)) : _root.File("attestations/none.json", "{}");
        Override(new FakeAnswer("gh", ["attestation", "verify"], 0, online, string.Empty) { Prefix = true, VerifiesAttestation = true });
        Override("gh", ["attestation", "verify", "--help"], 0, GhVerifyHelp);
    }

    /// <summary>Every verification the fake gh is asked for exits 1, as gh does for a refused attestation; its
    /// <c>--help</c> still answers, so the preflight passes and the refusal is the verification's.</summary>
    public void RefuseEveryVerification()
    {
        Override("gh", ["attestation", "verify"], 1, prefix: true);
        Override("gh", ["attestation", "verify", "--help"], 0, GhVerifyHelp);
    }

    /// <summary>The lines of <c>gh attestation verify --help</c> (gh 2.97.0) the installer's preflight reads: the flags it uses.</summary>
    public const string GhVerifyHelp =
        """
        Verify the integrity and provenance of an artifact using its associated
        cryptographically signed attestations.

        USAGE
          gh attestation verify [<file-path> | oci://<image-uri>] [--owner | --repo] [flags]

        FLAGS
          -b, --bundle string                Path to bundle on disk, either a single bundle in a JSON file or a JSON lines file with multiple bundles
              --cert-identity string         Enforce that the certificate's SubjectAlternativeName matches the provided value exactly
          -i, --cert-identity-regex string   Enforce that the certificate's SubjectAlternativeName matches the provided regex
              --deny-self-hosted-runners     Fail verification for attestations generated on self-hosted runners
          -R, --repo string                  Repository name in the format <owner>/<repo>
              --signer-workflow string       Enforce that the workflow that signed the attestation matches the provided value ([host/]<owner>/<repo>/<path>/<to>/<workflow>)

        """;

    /// <summary>Publishes a release whose archive and <c>.sha256</c> were made elsewhere — by the release workflow's own
    /// packaging script (E4.S2, <c>PackageFlows</c>) — served by the fake curl exactly as GitHub would serve them.</summary>
    public void PublishFiles(string version, string rid, string archive, string sha256File)
    {
        var name = ReleaseName(version, rid);
        Override(Download(ReleaseUrl(version, $"{name}.tar.gz"), archive));
        Override(Download(ReleaseUrl(version, $"{name}.tar.gz.sha256"), sha256File));
        Attest(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), [GenuineSigner(version)]);
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
        [FakeToolProtocol.RecordEnvironmentVariable] = string.Join(',', RecordedVariables),
        ["SUDO_USER"] = SudoUser.Length == 0 ? null : SudoUser,
        ["GH_TOKEN"] = GhToken.Length == 0 ? null : GhToken,
        ["WSL_CARE_INSTALL_RUN_WAIT_SECONDS"] = RunWaitSeconds.Length == 0 ? null : RunWaitSeconds,
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

    private static void LinkRealTools(string bin, string awk)
    {
        foreach (var tool in RealTools)
        {
            var real = (tool == "awk" && awk.Length > 0 ? awk : null)
                ?? new[] { "/usr/bin", "/bin" }.Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists)
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
            Answer("gh", ["--version"], "gh version 2.97.0 (2026-07-31)\nhttps://github.com/cli/cli/releases/tag/v2.97.0\n"),
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
