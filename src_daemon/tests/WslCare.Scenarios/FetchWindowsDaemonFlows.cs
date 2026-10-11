using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.Core.Processes;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The ONE fetch of the Windows daemon the extension bundles (<c>fetch-windows-daemon.sh</c>, E7.S5a,
/// research/PLAN_bundle_windows_binary.md §3.2, §5), run as the release build, CI and the attest job run it: the real script
/// under bash (the Linux legs; Git for Windows' bash on the Windows leg and a Windows workstation), a fake <c>gh</c> on PATH
/// that serves a release's assets from a folder and answers the attestation as told, zips made here in 7z's shape (the exe
/// under its folder, with that folder's own directory entry) and in every shape the script must refuse. Each refusal is
/// asserted by its exit code AND by an empty <c>bin/</c> — a stale exe a previous run left there is removed too.
/// </summary>
public sealed class FetchWindowsDaemonFlows
{
    private const string Version = "0.3.0";
    private const string Folder = "wsl-care-0.3.0-win-x64";
    private const string Member = Folder + "/wsl-care.exe";
    private const string Zip = "wsl-care-0.3.0-win-x64.zip";
    private static readonly byte[] Exe = Encoding.UTF8.GetBytes("MZ the attested wsl-care.exe stand-in");
    private static readonly byte[] Twin = Encoding.UTF8.GetBytes("MZ a second executable the build slipped in beside the attested one");

    private static void Runnable()
    {
        Assert.SkipWhen(ReleaseScripts.Bash.Length == 0, ReleaseScripts.NoBash);
        Assert.SkipWhen(Python.Length == 0, "no python with zipfile here (python3 or python); every CI image carries one");
    }

    /// <summary>The python the script will pick, or empty — python3, else python (as the script tries them).</summary>
    private static string Python => new[] { "python3", "python" }.FirstOrDefault(name => ExecutableResolver.Resolve(name) is ResolvedExecutable.Found found && PythonRuns(found.Path)) ?? string.Empty;

    private static bool PythonRuns(string path) =>
        ChildProcess.RunAsync(path, ["-c", "import zipfile"], new Dictionary<string, string?>(), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(20)).GetAwaiter().GetResult().Exit == 0;

    private sealed record World(TempRoot Root, string Assets, string Out, string Log, Dictionary<string, string?> Env);

    /// <summary>A release folder the fake gh serves, an output folder with a STALE exe in bin/, and the fake on PATH.</summary>
    private static World Make(TempRoot root, int attestExit = 0)
    {
        var assets = root.Dir("release");
        var output = root.Dir("out");
        root.File("out/bin/wsl-care.exe", "a stale exe from a previous run");
        root.File("out/bin/wsl-care.exe.sha256", "stale");
        var log = root.Under("gh.log");
        var gh = root.File("fake/gh", """
            #!/bin/sh
            echo "$*" >> "$FAKE_GH_LOG"
            case "$1 $2" in
              "release download")
                dir=""; prev=""
                for arg in "$@"; do [ "$prev" = "--dir" ] && dir="$arg"; prev="$arg"; done
                prev=""
                for arg in "$@"; do
                  if [ "$prev" = "-p" ] && [ ! -f "$FAKE_GH_ASSETS/$arg" ]; then echo "no assets match the file pattern" >&2; exit 1; fi
                  prev="$arg"
                done
                mkdir -p "$dir" && cp "$FAKE_GH_ASSETS"/* "$dir"/ ;;
              "attestation verify") exit "$FAKE_GH_ATTEST" ;;
              *) echo "unexpected gh call" >&2; exit 9 ;;
            esac

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var env = new Dictionary<string, string?>
        {
            ["PATH"] = $"{Path.GetDirectoryName(gh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["FAKE_GH_LOG"] = log,
            ["FAKE_GH_ASSETS"] = assets,
            ["FAKE_GH_ATTEST"] = attestExit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return new World(root, assets, output, log, env);
    }

    private sealed record Entry(string Name, byte[]? Data, int UnixMode = 0);

    /// <summary>A zip of <paramref name="entries"/> (a null Data is a directory entry), and its .sha256 naming <paramref name="shaName"/>.</summary>
    private static void Publish(World world, IReadOnlyList<Entry> entries, string? shaName = Zip, string? shaOverride = null)
    {
        var path = Path.Combine(world.Assets, Zip);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Name);
                if (entry.UnixMode != 0)
                {
                    zipEntry.ExternalAttributes = entry.UnixMode << 16;
                }

                if (entry.Data is not null)
                {
                    using var stream = zipEntry.Open();
                    stream.Write(entry.Data);
                }
            }
        }

        var hash = shaOverride ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (shaName is not null)
        {
            File.WriteAllText(path + ".sha256", $"{hash}  {shaName}\n");
        }
    }

    /// <summary>7z's shape: the folder's own directory entry, then the exe under it.</summary>
    private static IReadOnlyList<Entry> SevenZipShape(byte[]? exe = null) => [new Entry(Folder + "/", null), new Entry(Member, exe ?? Exe)];

    private static Task<ChildResult> FetchAsync(World world) =>
        ReleaseScripts.RunAsync("fetch-windows-daemon.sh", [Version, world.Out.Replace('\\', '/')], world.Root.Path, world.Env);

    private static void NothingIn(World world)
    {
        File.Exists(Path.Combine(world.Out, "bin", "wsl-care.exe")).Should().BeFalse("a refused fetch leaves no exe — not even the stale one");
        File.Exists(Path.Combine(world.Out, "bin", "wsl-care.exe.sha256")).Should().BeFalse("nor a digest");
    }

    [Fact]
    public async Task The_attested_member_is_written_with_its_digest_and_the_attestation_is_asked_with_the_exact_identity()
    {
        Runnable();
        using var root = new TempRoot("fetch-ok");
        var world = Make(root);
        Publish(world, SevenZipShape());

        var result = await FetchAsync(world);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        File.ReadAllBytes(Path.Combine(world.Out, "bin", "wsl-care.exe")).Should().Equal(Exe, "the bytes of the one member, read by its exact name");
        var digest = Convert.ToHexStringLower(SHA256.HashData(Exe));
        File.ReadAllText(Path.Combine(world.Out, "bin", "wsl-care.exe.sha256")).Should().Be($"{digest}  wsl-care.exe\n");
        var log = File.ReadAllText(world.Log);
        log.Should().Contain($"release download daemon-v{Version} --repo oleksandrdubyna88/wsl_care -p {Zip} -p {Zip}.sha256 --dir ");
        log.Should().Contain("attestation verify ").And.Contain("--repo oleksandrdubyna88/wsl_care --cert-identity https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v0.3.0 --source-ref refs/tags/daemon-v0.3.0 --deny-self-hosted-runners");
        log.Should().NotContain("--signer-workflow", "the identity is exact, never a prefix (module_tests O4)");
    }

    [Fact]
    public async Task The_zip_without_its_directory_entry_is_accepted_too()
    {
        Runnable();
        using var root = new TempRoot("fetch-nodir");
        var world = Make(root);
        Publish(world, [new Entry(Member, Exe)]);

        var result = await FetchAsync(world);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
    }

    public static TheoryData<string> RefusedShapes => ["symlink", "absolute", "dotdot", "backslash", "case", "second-file", "missing", "other-dir"];

    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public async Task A_zip_that_is_not_exactly_the_one_member_is_refused_and_leaves_nothing(string shape)
    {
        Runnable();
        using var root = new TempRoot($"fetch-{shape}");
        var world = Make(root);
        Publish(world, shape switch
        {
            "symlink" => [new Entry(Folder + "/", null), new Entry(Member, Encoding.UTF8.GetBytes("/usr/bin/evil"), 0b1010_000_111_111_111)],
            "absolute" => [new Entry("/" + Member, Exe)],
            "dotdot" => [new Entry(Folder + "/../wsl-care.exe", Exe)],
            "backslash" => [new Entry(Folder + "\\wsl-care.exe", Exe)],
            "case" => [new Entry("WSL-CARE-0.3.0-win-x64/wsl-care.exe", Exe)],
            "second-file" => [.. SevenZipShape(), new Entry(Folder + "/extra.dll", Exe)],
            "missing" => [new Entry(Folder + "/", null), new Entry(Folder + "/readme.txt", Exe)],
            _ => [new Entry("other/", null), new Entry(Member, Exe)],
        });

        var result = await FetchAsync(world);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("::error::fetch-windows-daemon:");
        NothingIn(world);
    }

    [Fact]
    public async Task A_checksum_that_does_not_match_is_refused_before_the_attestation_is_asked()
    {
        Runnable();
        using var root = new TempRoot("fetch-sha");
        var world = Make(root);
        Publish(world, SevenZipShape(), shaOverride: new string('a', 64));

        var result = await FetchAsync(world);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        NothingIn(world);
        File.ReadAllText(world.Log).Should().NotContain("attestation verify");
    }

    [Fact]
    public async Task A_checksum_line_naming_another_file_is_refused()
    {
        Runnable();
        using var root = new TempRoot("fetch-shaname");
        var world = Make(root);
        Publish(world, SevenZipShape(), shaName: "wsl-care-0.2.0-win-x64.zip");

        var result = await FetchAsync(world);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        NothingIn(world);
    }

    [Fact]
    public async Task A_failed_attestation_is_refused()
    {
        Runnable();
        using var root = new TempRoot("fetch-attest");
        var world = Make(root, attestExit: 1);
        Publish(world, SevenZipShape());

        var result = await FetchAsync(world);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("carries no attestation by release.yml at daemon-v0.3.0");
        NothingIn(world);
    }

    [Fact]
    public async Task A_stray_third_asset_in_the_download_is_refused()
    {
        Runnable();
        using var root = new TempRoot("fetch-stray");
        var world = Make(root);
        Publish(world, SevenZipShape());
        File.WriteAllText(Path.Combine(world.Assets, "notes.txt"), "a third file");

        var result = await FetchAsync(world);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        NothingIn(world);
    }

    [Fact]
    public async Task A_pinned_release_without_the_win_x64_asset_is_refused()
    {
        Runnable();
        using var root = new TempRoot("fetch-noasset");
        var world = Make(root);

        var result = await FetchAsync(world);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("is daemon 0.3.0 published with its win-x64 asset?");
        NothingIn(world);
    }

    [Theory]
    [InlineData("0.3", 2)]
    [InlineData("0.3.0-rc.1", 2)]
    public async Task A_version_that_is_not_x_y_z_is_a_usage_error(string version, int exit)
    {
        Runnable();
        using var root = new TempRoot("fetch-usage");
        var world = Make(root);

        var result = await ReleaseScripts.RunAsync("fetch-windows-daemon.sh", [version, world.Out.Replace('\\', '/')], root.Path, world.Env);

        result.Exit.Should().Be(exit, result.Stdout + result.Stderr);
    }

    // ---- §3.5, the plan of record's S5 test: the attest job re-derives the bytes before it signs (verify-bundled-exe.sh) ----

    /// <summary>A package as vsce writes one: the extension's members under <c>extension/</c>, the exe among them as given.</summary>
    private static string Package(World world, params (string Name, byte[] Data)[] members)
    {
        var path = Path.Combine(world.Root.Path, "from-build", "ai-os-care-win32-x64-0.5.0.vsix");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in members.Prepend(("extension/package.json", Encoding.UTF8.GetBytes("{}"))))
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(data);
        }

        return path;
    }

    private static Task<ChildResult> VerifyAsync(World world, string package) =>
        ReleaseScripts.RunAsync("verify-bundled-exe.sh", [Version, package.Replace('\\', '/')], world.Root.Path, world.Env);

    [Fact]
    public async Task The_bundled_exe_is_the_attested_asset_byte_for_byte()
    {
        Runnable();
        using var root = new TempRoot("bundled-same");
        var world = Make(root);
        Publish(world, SevenZipShape());

        var result = await VerifyAsync(world, Package(world, ("extension/bin/wsl-care.exe", Exe)));

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("extension/bin/wsl-care.exe is byte for byte the attested member");
        File.ReadAllText(world.Log).Should().Contain("attestation verify ", "the attest job asks the attestation itself — never the build's word");
    }

    /// <summary>
    /// The refused packages. Besides the wrong bytes, a missing or doubled member: a TWIN that VS Code's installer would write onto
    /// the same <c>bin\wsl-care.exe</c> on case-insensitive NTFS (the last entry wins), and a DLL the exe would load from its own
    /// folder (own Fable review of the code, finding 1). The build's allowlist refuses them too, but the build is the untrusted domain.
    /// </summary>
    public static TheoryData<string> RefusedPackages =>
        ["different", "missing", "twice", "case-twin", "folder-twin", "dot-twin", "slash-twin", "backslash-twin", "bin-dll", "manifest-twin", "other-pe"];

    [Theory]
    [MemberData(nameof(RefusedPackages))]
    public async Task A_package_whose_exe_is_not_the_attested_member_is_refused_before_signing(string shape)
    {
        Runnable();
        using var root = new TempRoot($"bundled-{shape}");
        var world = Make(root);
        Publish(world, SevenZipShape());
        (string, byte[])[] members = shape switch
        {
            "different" => [("extension/bin/wsl-care.exe", Encoding.UTF8.GetBytes("MZ rewritten after the fetch by a build step"))],
            "missing" => [],
            "twice" => [("extension/bin/wsl-care.exe", Exe), ("extension/bin/wsl-care.exe", Exe)],
            "case-twin" => [("extension/bin/wsl-care.exe", Exe), ("extension/bin/WSL-CARE.EXE", Twin)],
            "folder-twin" => [("extension/bin/wsl-care.exe", Exe), ("extension/BIN/wsl-care.exe", Twin)],
            "dot-twin" => [("extension/bin/wsl-care.exe", Exe), ("extension/bin/./wsl-care.exe", Twin)],
            "slash-twin" => [("extension/bin/wsl-care.exe", Exe), ("extension/bin//wsl-care.exe", Twin)],
            "backslash-twin" => [("extension/bin/wsl-care.exe", Exe), (@"extension\bin\wsl-care.exe", Twin)],
            "bin-dll" => [("extension/bin/wsl-care.exe", Exe), ("extension/bin/version.dll", Twin)],
            "manifest-twin" => [("extension/bin/wsl-care.exe", Exe), ("extension/PACKAGE.JSON", Twin)],
            _ => [("extension/bin/wsl-care.exe", Exe), ("extension/dist/helper.exe", Twin)],
        };

        var result = await VerifyAsync(world, Package(world, members));

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("does not carry exactly the attested daemon-v0.3.0 member — nothing is signed");
    }

    [Fact]
    public async Task A_refused_fetch_refuses_the_package_too_even_when_the_bytes_would_match()
    {
        Runnable();
        using var root = new TempRoot("bundled-unattested");
        var world = Make(root, attestExit: 1);
        Publish(world, SevenZipShape());

        var result = await VerifyAsync(world, Package(world, ("extension/bin/wsl-care.exe", Exe)));

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("could not be fetched and verified — nothing is signed");
    }
}
