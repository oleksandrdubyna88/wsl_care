using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The release archive (plan §9, §15e #1; E4.S2 — E4.S1's open item): <c>.github/scripts/package-daemon.sh</c>, the
/// script release.yml packs every RID with, run here against a stub binary, and its archive LISTED — exactly the
/// members install.sh unpacks, regular files and folders only, under one top folder — then handed to the real
/// install.sh, which must install it. The members are derived from install.sh's own unpack loop, so the packer and the
/// installer cannot drift apart without this going red.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PackageFlows
{
    private const string Version = "0.1.0";

    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), ReleaseScripts.LinuxOnly);

    private static Task<(ChildResult Result, string Archive)> PackAsync(TempRoot root, string rid, byte[] binary, string binaryName = "wsl-care") =>
        PackagePathFlows.PackAsync(root, rid, binary, binaryName);

    private static List<TarEntry> Entries(string archive)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<TarEntry>();
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static string Text(TarEntry entry)
    {
        using var data = new StreamReader(entry.DataStream!, Encoding.UTF8);
        return data.ReadToEnd();
    }

    private static byte[] Bytes(TarEntry entry)
    {
        using var copy = new MemoryStream();
        entry.DataStream!.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public async Task A_linux_archive_holds_exactly_what_install_sh_unpacks_as_regular_files_under_one_folder(string rid)
    {
        Linux();
        using var root = new TempRoot($"package-{rid}");
        var binary = Encoding.UTF8.GetBytes("#!/bin/sh\necho stub wsl-care\n");

        var (result, archive) = await PackAsync(root, rid, binary);

        result.Exit.Should().Be(0, $"packing should succeed:\n{result.Stdout}\n{result.Stderr}");
        var name = await ReleaseScripts.ArchiveNameAsync(Version, rid);
        Path.GetFileName(archive).Should().Be(name, "the script prints the archive it wrote, named as install.sh downloads it");
        var folder = $"wsl-care-{Version}-{rid}";
        var files = ReleaseFiles.InstallerRequiredMembers.Concat(ReleaseFiles.InstallerOptionalMembers).Select(m => $"{folder}/{m}").ToList();
        var folders = files.Select(f => f[..f.LastIndexOf('/')]).Distinct().ToList();

        var entries = Entries(archive);
        entries.Select(e => e.Name.TrimEnd('/')).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo([.. files, .. folders], "exactly the members install.sh requires, the optional units it installs when shipped (E14 S2b), and their folders — nothing missing, nothing extra");
        entries.Should().OnlyContain(e => e.EntryType == TarEntryType.Directory || e.EntryType == TarEntryType.RegularFile,
            "install.sh refuses a link or a special file (only regular files and folders)");
        entries.Where(e => e.EntryType == TarEntryType.Directory).Select(e => e.Name.TrimEnd('/')).Should().BeEquivalentTo(folders);
        entries.Should().OnlyContain(e => e.Uid == 0 && e.Gid == 0, "owner 0:0, whoever packed it");

        var byName = entries.Where(e => e.EntryType == TarEntryType.RegularFile).ToDictionary(e => e.Name, StringComparer.Ordinal);
        Bytes(byName[$"{folder}/wsl-care"]).Should().Equal(binary, "the published binary, byte for byte");
        byName[$"{folder}/wsl-care"].Mode.Should().Be(InstallWorld.Executable, "0755");
        foreach (var unit in ShippedFiles.UnitNames)
        {
            Text(byName[$"{folder}/systemd/{unit}"]).Should().Be(File.ReadAllText(Path.Combine(ShippedFiles.SystemdDirectory, unit)), $"{unit} is this repository's unit");
            byName[$"{folder}/systemd/{unit}"].Mode.Should().Be(InstallWorld.Regular, "0644");
        }

        Text(byName[$"{folder}/config/machine.json"]).Should().Be(File.ReadAllText(ShippedFiles.MachineConfig), "the EMPTY machine layer of E4.S1, not the embedded defaults");

        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
        File.ReadAllText(archive + ".sha256").Should().Be($"{hash}  {name}\n", "sha256sum's own format, the one install.sh and sha256sum -c read");
    }

    [Fact]
    public async Task The_installer_installs_the_archive_the_release_script_packed()
    {
        Linux();
        using var root = new TempRoot("package-install");
        using var world = new InstallWorld("packed");
        var (result, archive) = await PackAsync(root, "linux-x64", Encoding.UTF8.GetBytes(world.StubScript()));
        result.Exit.Should().Be(0, result.Stderr);
        world.PublishFiles(InstallWorld.NewestDaemon, "linux-x64", archive, archive + ".sha256");

        var install = await world.RunAsync();

        install.Exit.Should().Be(0, $"install.sh must accept what release.yml packs:\n{install.Stdout}\n{install.Stderr}");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript(), "the packed binary is what was installed");
        foreach (var unit in ShippedFiles.UnitNames)
        {
            File.ReadAllBytes(world.At($"/etc/systemd/system/{unit}")).Should().Equal(File.ReadAllBytes(Path.Combine(ShippedFiles.SystemdDirectory, unit)));
        }

        File.ReadAllBytes(world.At("/etc/wsl-care/config.json")).Should().Equal(File.ReadAllBytes(ShippedFiles.MachineConfig));
        world.StubInvocations.Should().NotBeEmpty("the installed binary was run by the installer's verification");
    }

    [Fact]
    public async Task A_bad_version_an_unknown_rid_or_a_missing_binary_is_refused_and_nothing_is_written()
    {
        Linux();
        using var root = new TempRoot("package-refusals");
        var publish = root.Dir("publish");
        File.WriteAllText(Path.Combine(publish, "wsl-care"), "stub");
        var empty = root.Dir("empty");
        var out1 = root.Under("out");

        var cases = new (string[] Args, int Exit, string Says)[]
        {
            (["0.1", "linux-x64", publish, out1], 2, "not a release version"),
            (["0.1.0/../x", "linux-x64", publish, out1], 2, "not a release version"),
            (["0.1.0", "osx-arm64", publish, out1], 2, "not a RID a daemon release ships"),
            (["0.1.0", "linux-x64", empty, out1], 1, "no published binary"),
            (["0.1.0", "linux-x64", publish], 2, "expected 4 arguments"),
        };
        foreach (var (args, exit, says) in cases)
        {
            var result = await ReleaseScripts.RunAsync("package-daemon.sh", args, root.Path);
            result.Exit.Should().Be(exit, $"{string.Join(' ', args)}: {result.Stderr}");
            result.Stderr.Should().Contain(says);
        }

        (Directory.Exists(out1) ? Directory.EnumerateFileSystemEntries(out1) : []).Should().BeEmpty("a refused run writes no archive");
    }
}

/// <summary>
/// The packaging script on EVERY operating system the release builds on (E4 review B1/B2): the Windows release leg runs
/// <c>package-daemon.sh</c> under Git Bash and hands the path it prints to <c>attest-build-provenance</c> and
/// <c>upload-artifact</c> — Windows programs, which read an MSYS path such as <c>/d/a/…</c> as <c>D:\d\a\…</c>. Run here
/// under the bash the workflows use (Git for Windows' on Windows), the printed path is opened by .NET, a program that is
/// not bash, exactly as the actions would open it.
/// </summary>
public sealed class PackagePathFlows
{
    private const string Version = "0.1.0";

    internal static async Task<(ChildResult Result, string Archive)> PackAsync(TempRoot root, string rid, byte[] binary, string binaryName = "wsl-care")
    {
        var publish = root.Dir("publish");
        File.WriteAllBytes(Path.Combine(publish, binaryName), binary);
        var result = await ReleaseScripts.RunAsync("package-daemon.sh", [Version, rid, publish, root.Under("out")], root.Path);
        return (result, result.StdoutLines.LastOrDefault() ?? string.Empty);
    }

    [Fact]
    public async Task The_printed_archive_path_is_one_a_program_that_is_not_bash_can_open()
    {
        Assert.SkipWhen(ReleaseScripts.Bash.Length == 0, ReleaseScripts.NoBash);
        using var root = new TempRoot("package-path");

        var (result, archive) = await PackAsync(root, "linux-x64", Encoding.UTF8.GetBytes("#!/bin/sh\necho stub\n"));

        result.Exit.Should().Be(0, $"packing should succeed:\n{result.Stdout}\n{result.Stderr}");
        File.Exists(archive).Should().BeTrue($"the path the script prints ({archive}) is what release.yml hands to attest-build-provenance and upload-artifact, which are not bash");
        File.Exists(archive + ".sha256").Should().BeTrue("the .sha256 travels beside it under the same spelling");
    }

    [Fact]
    public async Task The_windows_archive_holds_the_exe_alone_under_its_folder()
    {
        Assert.SkipWhen(ReleaseScripts.Bash.Length == 0, ReleaseScripts.NoBash);
        Assert.SkipWhen(ExecutableResolver.Resolve("7z", TestContext.Current.CancellationToken) is not ResolvedExecutable.Found, "7-Zip (7z) is not on PATH here; the GitHub Ubuntu and Windows images carry it, so every CI leg runs this");
        using var root = new TempRoot("package-win");
        var exe = Encoding.UTF8.GetBytes("MZ stand-in for wsl-care.exe");

        var (result, archive) = await PackAsync(root, "win-x64", exe, "wsl-care.exe");

        result.Exit.Should().Be(0, $"{result.Stdout}\n{result.Stderr}");
        File.Exists(archive).Should().BeTrue($"the printed path ({archive}) opens outside bash — on the Windows leg it is what the attestation is made of");
        var name = await ReleaseScripts.ArchiveNameAsync(Version, "win-x64");
        Path.GetFileName(archive).Should().Be(name);
        var folder = $"wsl-care-{Version}-win-x64";
        using var zip = ZipFile.OpenRead(archive);
        var files = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        files.Select(e => e.FullName).Should().Equal([$"{folder}/wsl-care.exe"], "the Windows probe ships no units and no distro machine layer");
        zip.Entries.Where(e => e.FullName.EndsWith('/')).Select(e => e.FullName.TrimEnd('/')).Should().BeSubsetOf([folder]);
        using (var stream = files[0].Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            copy.ToArray().Should().Equal(exe);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
        File.ReadAllText(archive + ".sha256").Should().Be($"{hash}  {name}\n");
    }
}
