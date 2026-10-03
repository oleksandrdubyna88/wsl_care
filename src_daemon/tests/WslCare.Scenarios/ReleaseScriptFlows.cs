using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The two refusals release.yml rests on (E4.S2), run as it runs them: the guard every leg waits for
/// (<c>release-guard.sh</c> — the tag, version.txt, main) and the completeness check before and after the draft is filled
/// (<c>verify-release-assets.sh</c> — every RID, its archive, a matching <c>.sha256</c>, nothing else). A check that
/// prints a complaint and exits 0 stops nothing in a workflow, so each refusal is asserted by its exit code.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ReleaseScriptFlows
{
    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), ReleaseScripts.LinuxOnly);

    /// <summary>A checkout of the tag: just src_daemon/version.txt.</summary>
    private static string Checkout(TempRoot root, string version)
    {
        var dir = root.Dir("checkout");
        root.File("checkout/src_daemon/version.txt", version + "\n");
        return dir;
    }

    [Fact]
    public async Task The_guard_admits_the_tag_whose_version_txt_agrees_and_hands_the_version_on()
    {
        Linux();
        using var root = new TempRoot("guard-ok");
        var output = root.File("github-output", string.Empty);

        var result = await ReleaseScripts.RunAsync("release-guard.sh", ["daemon-v0.1.0"], Checkout(root, "0.1.0"), new Dictionary<string, string?> { ["GITHUB_OUTPUT"] = output });

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("version=0.1.0");
        File.ReadAllText(output).Should().Be("version=0.1.0\n", "the legs read the approved version from the job output");
    }

    [Theory]
    [InlineData("daemon-v0.1.1", "0.1.0", "src_daemon/version.txt at the tag says '0.1.0'")]
    [InlineData("extension-v0.1.0", "0.1.0", "not daemon-v<version>")]
    [InlineData("daemon-v0.1", "0.1.0", "does not carry a release version")]
    [InlineData("daemon-v0.1.0/../x", "0.1.0", "does not carry a release version")]
    [InlineData("daemon-v", "0.1.0", "does not carry a release version")]
    public async Task The_guard_refuses_a_tag_it_cannot_release(string tag, string recorded, string says)
    {
        Linux();
        using var root = new TempRoot("guard-refuse");

        var result = await ReleaseScripts.RunAsync("release-guard.sh", [tag], Checkout(root, recorded));

        result.Exit.Should().Be(1, $"{tag} against version.txt {recorded} must be refused:\n{result.Stdout}");
        result.Stdout.Should().Contain("::error::release guard:").And.Contain(says);
    }

    [Fact]
    public async Task The_guard_refuses_a_tagged_commit_that_is_not_on_main()
    {
        Linux();
        using var root = new TempRoot("guard-main");
        var dir = Checkout(root, "0.1.0");
        // An identity for THIS throwaway repository's commits only, by environment — no git configuration is written.
        var env = new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = "test",
            ["GIT_AUTHOR_EMAIL"] = "test@example.invalid",
            ["GIT_COMMITTER_NAME"] = "test",
            ["GIT_COMMITTER_EMAIL"] = "test@example.invalid",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        async Task Git(params string[] args)
        {
            var git = await ChildProcess.RunAsync("git", args, env, dir);
            git.Exit.Should().Be(0, $"git {string.Join(' ', args)}: {git.Stderr}");
        }

        await Git("init", "-q", "-b", "main");
        await Git("add", ".");
        await Git("commit", "-q", "-m", "on main");
        await Git("checkout", "-q", "-b", "side");
        await Git("commit", "-q", "--allow-empty", "-m", "off main");

        var off = await ReleaseScripts.RunAsync("release-guard.sh", ["daemon-v0.1.0", "main"], dir, env);
        await Git("checkout", "-q", "main");
        var on = await ReleaseScripts.RunAsync("release-guard.sh", ["daemon-v0.1.0", "main"], dir, env);

        off.Exit.Should().Be(1, off.Stdout);
        off.Stdout.Should().Contain("is not on main");
        on.Exit.Should().Be(0, on.Stdout + on.Stderr);
    }

    /// <summary>A complete asset set as release.yml's matrix makes it: per RID its archive and a true .sha256.</summary>
    private static async Task<string> CompleteSetAsync(TempRoot root, string version)
    {
        var dir = root.Dir("assets");
        foreach (var rid in ReleaseFiles.DaemonRids)
        {
            var name = await ReleaseScripts.ArchiveNameAsync(version, rid);
            var bytes = Encoding.UTF8.GetBytes($"archive of {rid}");
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            File.WriteAllText(Path.Combine(dir, name + ".sha256"), $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {name}\n");
        }

        return dir;
    }

    [Fact]
    public async Task A_complete_set_passes_the_completeness_check()
    {
        Linux();
        using var root = new TempRoot("assets-ok");
        var dir = await CompleteSetAsync(root, "0.1.0");

        var result = await ReleaseScripts.RunAsync("verify-release-assets.sh", ["0.1.0", dir], root.Path);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        Directory.EnumerateFiles(dir).Should().HaveCount(ReleaseFiles.DaemonRids.Count * 2, "the positive: the fixture really is every RID's pair");
    }

    [Fact]
    public async Task An_incomplete_or_wrong_set_is_refused_naming_every_problem()
    {
        Linux();
        var linux = await ReleaseScripts.ArchiveNameAsync("0.1.0", "linux-arm64");
        var windows = await ReleaseScripts.ArchiveNameAsync("0.1.0", "win-x64");
        var x64 = await ReleaseScripts.ArchiveNameAsync("0.1.0", "linux-x64");
        var damage = new (string What, Action<string> Break, string[] Says)[]
        {
            ("a RID without its archive", dir => File.Delete(Path.Combine(dir, windows)), ["win-x64: the archive"]),
            ("a RID without its .sha256", dir => File.Delete(Path.Combine(dir, linux + ".sha256")), ["linux-arm64:", ".sha256 is missing"]),
            ("an archive that does not match", dir => File.AppendAllText(Path.Combine(dir, x64), "tampered"), ["linux-x64:", "does not match its .sha256"]),
            ("a .sha256 naming another file", dir => File.WriteAllText(Path.Combine(dir, linux + ".sha256"), new string('0', 64) + "  other.tar.gz\n"), ["linux-arm64:", "is not '<sha-256>"]),
            ("an asset no release ships", dir => File.WriteAllText(Path.Combine(dir, "notes.txt"), "x"), ["an asset no daemon release ships: notes.txt"]),
            ("a whole RID missing", dir =>
            {
                File.Delete(Path.Combine(dir, x64));
                File.Delete(Path.Combine(dir, x64 + ".sha256"));
            }, ["linux-x64: the archive", "linux-x64:", ".sha256 is missing"]),
        };

        foreach (var (what, breakIt, says) in damage)
        {
            using var root = new TempRoot("assets-bad");
            var dir = await CompleteSetAsync(root, "0.1.0");
            breakIt(dir);

            var result = await ReleaseScripts.RunAsync("verify-release-assets.sh", ["0.1.0", dir], root.Path);

            result.Exit.Should().Be(1, $"{what} must be refused:\n{result.Stdout}");
            foreach (var text in says)
            {
                result.Stdout.Should().Contain(text, what);
            }
        }
    }
}
