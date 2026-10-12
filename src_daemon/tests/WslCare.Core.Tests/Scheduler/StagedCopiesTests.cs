using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Scheduler;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Scheduler;

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.2, §3.7): a render is <c>registrable</c> only when this process IS a staged copy —
/// <c>&lt;stagingRoot&gt;\&lt;64-hex digest&gt;\wsl-care.exe</c> — whose bytes hash to that digest and whose <c>stage.json</c> agrees,
/// so no task XML can ever name the extension folder (the bundle plan's §4 rule, enforced in the daemon too).
/// </summary>
public sealed class StagedCopiesTests
{
    private const int Cap = 65536;

    private const string Version = "0.4.0";

    private static readonly byte[] ExeBytes = Encoding.UTF8.GetBytes("not a real executable: the bytes a staged copy hashes");

    private static readonly string ExeDigest = Convert.ToHexStringLower(SHA256.HashData(ExeBytes));

    [Theory]
    [InlineData("C:\\Users\\me\\.vscode\\extensions\\oleksandrdubyna.wsl-care-0.68.0-win32-x64\\bin\\wsl-care.exe", "the extension folder")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\wsl-care\\bin\\wsl-care.exe", "directly in the staging root")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\wsl-care\\bin\\not-a-digest\\wsl-care.exe", "a folder that is no digest")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\wsl-care\\bin\\A14AD2EB0C6F1D9E8B7A6C5D4E3F2A1B0C9D8E7F6A5B4C3D2E1F0A9B8C7D6E5F\\wsl-care.exe", "an upper-case digest (sha256 is written lower-case)")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\wsl-care\\bin\\a14ad2eb0c6f1d9e8b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f\\other.exe", "another file name")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\wsl-care\\bin\\a14ad2eb0c6f1d9e8b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f\\..\\..\\..\\wsl-care.exe", "a path that climbs out")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\wsl-care\\bin\\a14ad2eb0c6f1d9e8b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f\\deeper\\wsl-care.exe", "a level too deep")]
    [InlineData("", "an unknown own path")]
    public void The_render_refuses_a_copy_outside_the_staging_root(string processPath, string why)
    {
        using var sandbox = new SandboxHost("staged-outside");
        const string root = "C:\\Users\\me\\AppData\\Local\\wsl-care\\bin";

        var check = StagedCopies.Check(root, processPath, Version, sandbox.Files, sandbox.Files, Cap);

        check.Registrable.Should().BeFalse(why);
        check.Digest.Should().BeEmpty(why);
        check.Reason.Should().Contain("not a staged copy", why).And.Contain(root, "the reason names where a staged copy lives");
    }

    /// <summary>The positive case with the same checks: a copy in its digest folder, hashing to it, with an agreeing stage.json — the
    /// test that fails when the fixture is wrong (family testing rule: a file of refusals needs one positive).</summary>
    [Fact]
    public void A_verified_staged_copy_is_registrable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a staged copy is a Windows file read through the Windows open (the distro's binary renders no task)");
        using var world = new StagedWorld();
        var exe = world.Stage(ExeDigest, ExeBytes, Stage(ExeDigest, Version));

        var check = StagedCopies.Check(world.Root, exe, $"{Version}+7aeb02dd39a9841915112456532d3803e4cb6026", world.Files, world.Files, Cap);

        check.Registrable.Should().BeTrue(check.Reason);
        check.Digest.Should().Be(ExeDigest);
        check.Version.Should().Be(Version);
    }

    [Fact]
    public void A_copy_whose_bytes_do_not_hash_to_its_folder_is_not_registrable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a staged copy is a Windows file read through the Windows open");
        using var world = new StagedWorld();
        var otherDigest = new string('c', 64);
        var exe = world.Stage(otherDigest, ExeBytes, Stage(otherDigest, Version));

        var check = StagedCopies.Check(world.Root, exe, Version, world.Files, world.Files, Cap);

        check.Registrable.Should().BeFalse();
        check.Reason.Should().Contain("hashes to").And.Contain(ExeDigest);
    }

    [Theory]
    [InlineData("missing", "no stage.json")]
    [InlineData("digest", "stage.json names another digest")]
    [InlineData("version", "stage.json names another version")]
    [InlineData("torn", "stage.json is not JSON")]
    public void A_copy_whose_stage_json_does_not_agree_is_not_registrable(string fault, string why)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a staged copy is a Windows file read through the Windows open");
        using var world = new StagedWorld();
        var record = fault switch
        {
            "missing" => string.Empty,
            "digest" => Stage(new string('d', 64), Version),
            "version" => Stage(ExeDigest, "0.3.9"),
            _ => "{ \"version\": ",
        };
        var exe = world.Stage(ExeDigest, ExeBytes, record);

        var check = StagedCopies.Check(world.Root, exe, Version, world.Files, world.Files, Cap);

        check.Registrable.Should().BeFalse(why);
        check.Reason.Should().Contain(StageRecord.FileName, why);
    }

    [Fact]
    public void The_staging_root_is_the_state_directory_s_bin()
    {
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed("C:\\sandbox"));

        StagedCopies.Root(paths).Should().Be("C:\\sandbox\\Users\\me\\AppData\\Local\\wsl-care\\bin");
    }

    [Theory]
    [InlineData("0.4.0+7aeb02dd", "0.4.0")]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("unknown", "unknown")]
    public void The_release_of_a_version_text_drops_the_commit(string text, string release) =>
        StagedCopies.ReleaseOf(text).Should().Be(release);

    private static string Stage(string sha, string version) =>
        $$"""{ "version": "{{version}}", "sha256": "{{sha}}", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\Users\\me\\.vscode\\extensions\\x\\bin\\wsl-care.exe" }""";

    /// <summary>A Windows layout under a temporary root with the product's own file system over it.</summary>
    private sealed class StagedWorld : IDisposable
    {
        private readonly TempRoot _temp = new("staged-copy");

        public StagedWorld()
        {
            var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_temp.Path));
            Files = new PhysicalFileSystem(paths) { OwnersAreThisProcess = true };
            Root = StagedCopies.Root(paths);
        }

        public PhysicalFileSystem Files { get; }

        public string Root { get; }

        /// <summary>The exe at <c>&lt;root&gt;\&lt;folder&gt;\wsl-care.exe</c> and, unless empty, its stage.json; the exe's path.</summary>
        public string Stage(string folder, byte[] bytes, string record)
        {
            var dir = Path.Combine(Root, folder);
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, StagedCopies.ExeName);
            File.WriteAllBytes(exe, bytes);
            if (record.Length > 0)
            {
                File.WriteAllText(Path.Combine(dir, StageRecord.FileName), record);
            }

            return exe;
        }

        public void Dispose() => _temp.Dispose();
    }
}
