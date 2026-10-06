using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The two refusals release-extension.yml rests on (E5.S3), run as it runs them: the guard
/// (<c>release-extension-guard.sh</c> — the tag, package.json, the publisher, main, and THE MECHANICAL LIVE GATE: the
/// minimum daemon published and verified in POST_DEPLOY.md, plan §15g M4) and the asset set
/// (<c>verify-extension-assets.sh</c> — the .vsix and its .sha256, nothing else). Each refusal is asserted by its exit code:
/// a check that prints and exits 0 stops nothing. GitHub is a fake <c>gh</c> on PATH that answers the release query and
/// records what it was asked.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ReleaseExtensionScriptFlows
{
    private const string Published = "false\tdaemon-v0.1.0";
    private const string Verified = "Last verified: 2026-10-05 · the owner's installation · daemon 0.1.0";

    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), ReleaseScripts.LinuxOnly);

    private sealed record Checkout(string Dir, IReadOnlyDictionary<string, string?> Env, string GhLog);

    /// <summary>A checkout of the tag: package.json, the handshake's minimum, POST_DEPLOY.md — and a fake gh.</summary>
    /// <summary>The checked-in artefact the guard reads the minimum daemon from (E5 code round #2/#5).</summary>
    private static string MinDaemonJson(string min) => $"{{\n  \"minDaemonForRender\": \"{min}\"\n}}\n";

    private static Checkout Make(TempRoot root, string version = "0.1.0", string publisher = "wsl-care-dev", string min = "0.1.0", string stamp = Verified, string? ghAnswer = Published, string? handshake = null, string? minDaemonJson = "")
    {
        var dir = root.Dir("checkout");
        root.File("checkout/src_vs_code/package.json", $"{{\n  \"name\": \"{ReleaseFiles.ExtensionName}\",\n  \"version\": \"{version}\",\n  \"publisher\": \"{publisher}\",\n  \"scripts\": {{\n    \"version\": \"not-the-top-level-one\"\n  }}\n}}\n");
        root.File("checkout/src_vs_code/src/client/handshake.ts", handshake ?? $"export const SUPPORTED_SCHEMA: readonly number[] = [1];\n\nexport const MIN_DAEMON_FOR_RENDER = '{min}';\n");
        if (minDaemonJson is not null)
        {
            root.File("checkout/src_vs_code/min-daemon.json", minDaemonJson.Length == 0 ? MinDaemonJson(min) : minDaemonJson);
        }

        root.File("checkout/POST_DEPLOY.md", $"# Post-deploy checks\n\nTarget: x\n{stamp}\n\n| # | a | b | c |\n");
        var log = root.Under("gh.log");
        var gh = root.File("bin/gh", "#!/bin/sh\necho \"$@\" >> \"$FAKE_GH_LOG\"\n[ -n \"$FAKE_GH_FAIL\" ] && { echo 'HTTP 404: Not Found' >&2; exit 1; }\nprintf '%s\\n' \"$FAKE_GH_ANSWER\"\n");
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = $"{Path.GetDirectoryName(gh)}:{Environment.GetEnvironmentVariable("PATH")}",
            ["GH_REPO"] = "oleksandrdubyna88/wsl_care",
            ["GH_TOKEN"] = "read-only",
            ["FAKE_GH_LOG"] = log,
            ["FAKE_GH_ANSWER"] = ghAnswer ?? string.Empty,
            ["FAKE_GH_FAIL"] = ghAnswer is null ? "1" : null,
            ["GITHUB_OUTPUT"] = null,
        };
        return new Checkout(dir, env, log);
    }

    [Fact]
    public async Task The_guard_admits_a_matching_tag_with_a_real_publisher_and_the_minimum_daemon_published_and_verified()
    {
        Linux();
        using var root = new TempRoot("ext-guard-ok");
        var checkout = Make(root);
        var output = root.File("github-output", string.Empty);

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, new Dictionary<string, string?>(checkout.Env) { ["GITHUB_OUTPUT"] = output });

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        File.ReadAllText(output).Should().Be("version=0.1.0\npublisher=wsl-care-dev\nmin_daemon=0.1.0\n");
        File.ReadAllText(checkout.GhLog).Should().Contain("api repos/oleksandrdubyna88/wsl_care/releases/tags/daemon-v0.1.0", "it asked GitHub for the minimum daemon's release");
    }

    [Theory]
    [InlineData("daemon-v0.1.0", "not extension-v<version>")]
    [InlineData("extension-v0.1", "does not carry an x.y.z version")]
    [InlineData("extension-v0.1.0-rc.1", "does not carry an x.y.z version")]
    [InlineData("extension-v0.2.0", "src_vs_code/package.json at the tag says '0.1.0'")]
    public async Task The_guard_refuses_a_tag_it_cannot_release(string tag, string says)
    {
        Linux();
        using var root = new TempRoot("ext-guard-tag");
        var checkout = Make(root);

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", [tag], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain("::error::extension release guard:").And.Contain(says);
    }

    [Fact]
    public async Task The_guard_refuses_the_placeholder_publisher()
    {
        Linux();
        using var root = new TempRoot("ext-guard-publisher");
        var checkout = Make(root, publisher: "publisher-tbd");

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain("placeholder publisher 'publisher-tbd'");
    }

    /// <summary>The mechanical live gate (plan §15g M4): the minimum daemon must be a PUBLISHED release.</summary>
    [Theory]
    [InlineData(null, "is not a published release")]
    [InlineData("true\tdaemon-v0.1.0", "not a published, non-draft release")]
    [InlineData("false\tdaemon-v0.0.9", "not a published, non-draft release")]
    public async Task The_guard_refuses_while_the_minimum_daemon_is_not_a_published_release(string? ghAnswer, string says)
    {
        Linux();
        using var root = new TempRoot("ext-guard-daemon");
        var checkout = Make(root, ghAnswer: ghAnswer);

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain("daemon-v0.1.0").And.Contain(says);
    }

    /// <summary>…and POST_DEPLOY.md must name it as last verified: a date and a daemon at or above the minimum.</summary>
    [Theory]
    [InlineData("Last verified: never, as of 2026-10-03 — nothing released yet", "names no date")]
    [InlineData("Last verified: 2026-10-05 · the owner's installation", "names no 'daemon <x.y.z>'")]
    [InlineData("Last verified: 2026-10-05 · the owner's installation · daemon 0.0.9", "last verified daemon 0.0.9, older than the minimum 0.1.0")]
    public async Task The_guard_refuses_while_POST_DEPLOY_does_not_name_the_minimum_daemon_as_verified(string stamp, string says)
    {
        Linux();
        using var root = new TempRoot("ext-guard-stamp");
        var checkout = Make(root, stamp: stamp);

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain(says);
    }

    [Fact]
    public async Task A_newer_verified_daemon_satisfies_the_minimum_and_a_ten_compares_as_a_number()
    {
        Linux();
        using var root = new TempRoot("ext-guard-newer");
        var checkout = Make(root, min: "0.2.0", stamp: "Last verified: 2026-11-01 · installation · daemon 0.10.0 · extension 0.1.0", ghAnswer: "false\tdaemon-v0.2.0");

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("min_daemon=0.2.0");
    }

    /// <summary>E5 code round #6: ONE version comparison (<c>lib/versions.sh</c>, POSIX sh) for the guard's "verified daemon
    /// at or above the minimum" and POST_DEPLOY item 6's "the tag's version is served and nothing above it" — run under
    /// <c>/bin/sh</c>, as post-deploy-check runs the item.</summary>
    [Fact]
    public async Task The_shared_version_ranking_compares_numbers_and_says_whether_a_version_is_listed_with_none_above_it()
    {
        Linux();
        const string script = """
            . "$1"
            version_at_least 0.10.0 0.9.0 && echo at-least-ten
            version_at_least 0.1.0 0.1.0 && echo at-least-equal
            version_at_least 0.0.9 0.1.0 || echo below
            printf '0.9.0\n0.10.0\n0.2.0\n' | highest_version
            printf '0.1.0\n0.2.0\n' | is_top_version 0.2.0 && echo top
            printf '0.1.0\n0.3.0\n0.2.0\n' | is_top_version 0.2.0 || echo something-above
            printf '0.1.0\n0.3.0\n' | is_top_version 0.2.0 || echo not-listed
            printf '' | is_top_version 0.2.0 || echo nothing-served
            """;

        var result = await ChildProcess.RunAsync("/bin/sh", ["-c", script, "sh", ReleaseFiles.Script(Path.Combine("lib", "versions.sh"))], new Dictionary<string, string?>());

        result.Exit.Should().Be(0, result.Stderr);
        result.StdoutLines.Should().Equal("at-least-ten", "at-least-equal", "below", "0.10.0", "top", "something-above", "not-listed", "nothing-served");
    }

    /// <summary>E5 code round #2/#5: the guard reads the minimum from the JSON artefact, never TypeScript with a line pattern —
    /// a handshake.ts reformatted by a person or a formatter (a type annotation, double quotes) no longer stops every
    /// release.</summary>
    [Fact]
    public async Task A_reformatted_handshake_does_not_stop_a_release_because_the_guard_reads_the_json_artefact()
    {
        Linux();
        using var root = new TempRoot("ext-guard-reformatted");
        var checkout = Make(root, handshake: "export const MIN_DAEMON_FOR_RENDER: string = \"0.1.0\"; // reformatted\n");

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("min_daemon=0.1.0");
    }

    [Theory]
    [InlineData(null, "is missing")]
    [InlineData("{}\n", "carries no minDaemonForRender")]
    [InlineData("{ \"minDaemonForRender\": \"0.1\" }\n", "carries no minDaemonForRender")]
    [InlineData("{ \"minDaemonForRender\": 1 }\n", "carries no minDaemonForRender")]
    [InlineData("not json\n", "carries no minDaemonForRender")]
    public async Task The_guard_refuses_a_missing_or_malformed_minimum_daemon_artefact_naming_it(string? json, string says)
    {
        Linux();
        using var root = new TempRoot("ext-guard-min-json");
        var checkout = Make(root, minDaemonJson: json);

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain("src_vs_code/min-daemon.json").And.Contain(says);
        File.Exists(checkout.GhLog).Should().BeFalse("refused before GitHub is asked");
    }

    [Fact]
    public async Task The_guard_refuses_a_tagged_commit_that_is_not_on_main()
    {
        Linux();
        using var root = new TempRoot("ext-guard-main");
        var checkout = Make(root);
        var env = new Dictionary<string, string?>(checkout.Env)
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
            var git = await ChildProcess.RunAsync("git", args, env, checkout.Dir);
            git.Exit.Should().Be(0, $"git {string.Join(' ', args)}: {git.Stderr}");
        }

        await Git("init", "-q", "-b", "main");
        await Git("add", ".");
        await Git("commit", "-q", "-m", "on main");
        await Git("checkout", "-q", "-b", "side");
        await Git("commit", "-q", "--allow-empty", "-m", "off main");

        var off = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0", "main"], checkout.Dir, env);
        await Git("checkout", "-q", "main");
        var on = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0", "main"], checkout.Dir, env);

        off.Exit.Should().Be(1, off.Stdout);
        off.Stdout.Should().Contain("is not on main");
        on.Exit.Should().Be(0, on.Stdout + on.Stderr);
    }

    /// <summary>The set as the build makes it: the .vsix and a true .sha256.</summary>
    private static string AssetSet(TempRoot root, string version)
    {
        var dir = root.Dir("assets");
        var name = ReleaseFiles.ExtensionVsix(version);
        var bytes = Encoding.UTF8.GetBytes("PK fake vsix");
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        File.WriteAllText(Path.Combine(dir, name + ".sha256"), $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {name}\n");
        return dir;
    }

    [Fact]
    public async Task The_extension_asset_set_passes_whole_and_is_refused_broken()
    {
        Linux();
        var damage = new (string What, Action<string> Break, string Says)[]
        {
            ("the .vsix missing", dir => File.Delete(Path.Combine(dir, ReleaseFiles.ExtensionVsix("0.1.0"))), "expected exactly"),
            ("the .sha256 missing", dir => File.Delete(Path.Combine(dir, ReleaseFiles.ExtensionVsix("0.1.0") + ".sha256")), "expected exactly"),
            ("an extra file", dir => File.WriteAllText(Path.Combine(dir, "notes.txt"), "x"), "expected exactly"),
            ("a tampered .vsix", dir => File.AppendAllText(Path.Combine(dir, ReleaseFiles.ExtensionVsix("0.1.0")), "x"), "does not match its .sha256"),
            ("a .sha256 naming another file", dir => File.WriteAllText(Path.Combine(dir, ReleaseFiles.ExtensionVsix("0.1.0") + ".sha256"), new string('0', 64) + $"  {ReleaseFiles.ExtensionVsix("0.0.9")}\n"), $"names '{ReleaseFiles.ExtensionVsix("0.0.9")}'"),
            ("a .sha256 naming another package", dir => File.WriteAllText(Path.Combine(dir, ReleaseFiles.ExtensionVsix("0.1.0") + ".sha256"), new string('0', 64) + "  another-package-0.1.0.vsix\n"), "is not one"),
        };

        using (var root = new TempRoot("ext-assets-ok"))
        {
            var ok = await ReleaseScripts.RunAsync("verify-extension-assets.sh", ["0.1.0", AssetSet(root, "0.1.0")], root.Path);
            ok.Exit.Should().Be(0, ok.Stdout + ok.Stderr);
        }

        foreach (var (what, breakIt, says) in damage)
        {
            using var root = new TempRoot("ext-assets-bad");
            var dir = AssetSet(root, "0.1.0");
            breakIt(dir);

            var result = await ReleaseScripts.RunAsync("verify-extension-assets.sh", ["0.1.0", dir], root.Path);

            result.Exit.Should().Be(1, $"{what}:\n{result.Stdout}");
            result.Stdout.Should().Contain(says, what);
        }
    }

    /// <summary>POST_DEPLOY item 12 — the Marketplace publish credential — RUN as post-deploy-check runs it (its own command,
    /// read from the row, under <c>/bin/sh</c>) against each answer the <c>VSCE_PAT expires:</c> line may give. The owner's
    /// decision of 2026-10-06 is a MANUAL upload with no stored token (docs/repo-settings.md, step 9): that answer passes, as
    /// OIDC does and a PAT more than 30 days from expiry does; a PAT inside 30 days, the placeholder and a missing line fail.
    /// The dates are derived from now at noon UTC, because the command itself asks <c>date</c>.</summary>
    [Fact]
    public async Task Post_deploy_item_12_passes_a_manual_upload_OIDC_or_a_PAT_valid_beyond_30_days_and_fails_anything_else()
    {
        Linux();
        var row = File.ReadAllLines(Path.Combine(ReleaseFiles.Root, "POST_DEPLOY.md")).Single(l => l.StartsWith("| 12 |", StringComparison.Ordinal));
        var command = PostDeployCommand(row);
        var noon = DateTime.UtcNow.Date.AddHours(12);
        var cases = new (string Line, bool Passes)[]
        {
            ("VSCE_PAT expires: none — manual upload (the owner uploads the attested .vsix by hand; docs/repo-settings.md, step 9)", true),
            ("VSCE_PAT expires: none — OIDC", true),
            ($"VSCE_PAT expires: {noon.AddDays(60):yyyy-MM-dd} (global PAT)", true),
            // The boundary: the day is read as its UTC midnight and must lie MORE than 30 days after now — 31 days ahead
            // passes at any hour, exactly 30 days ahead fails at any hour (even at 00:00:00, where it is equal, not more).
            ($"VSCE_PAT expires: {noon.AddDays(31):yyyy-MM-dd} (global PAT)", true),
            ($"VSCE_PAT expires: {noon.AddDays(30):yyyy-MM-dd} (global PAT)", false),
            ($"VSCE_PAT expires: {noon.AddDays(10):yyyy-MM-dd} (global PAT)", false),
            ("VSCE_PAT expires: none yet — recorded at the E5 live gate", false),
            ("VSCE_PAT expires: none", false),
            ("no credential line at all", false),
        };

        foreach (var (line, passes) in cases)
        {
            using var root = new TempRoot("post-deploy-12");
            root.File("POST_DEPLOY.md", $"# Post-deploy checks\n\nTarget: x\n{line}\nLast verified: never\n");

            var result = await ChildProcess.RunAsync("/bin/sh", ["-c", command], new Dictionary<string, string?>(), root.Path);

            (result.Exit == 0).Should().Be(passes, $"item 12 over '{line}' (exit {result.Exit}): {result.Stderr}");
        }
    }

    /// <summary>The first code span of a POST_DEPLOY row's Check cell — the command post-deploy-check runs — with the
    /// table's escaped pipes restored.</summary>
    private static string PostDeployCommand(string row)
    {
        var start = row.IndexOf('`', StringComparison.Ordinal);
        var end = row.IndexOf('`', start + 1);
        start.Should().BeGreaterThan(0, "the row carries a code span");
        end.Should().BeGreaterThan(start, "the code span is closed");
        return row[(start + 1)..end].Replace("\\|", "|", StringComparison.Ordinal);
    }

    /// <summary>The manual Marketplace route (owner decision 2026-10-06; docs/repo-settings.md, step 9) RUN, not only
    /// structured: the three scripts of release-extension.yml's publish-marketplace job that the route rests on — the
    /// "served?" query, the publish, the wait — taken from the workflow and run under bash as the runner runs them
    /// (<c>bash -e -o pipefail</c>), with a fake vsce at the path the job calls (it checks its arguments, logs each call and
    /// answers what the Marketplace would) and a fake <c>sleep</c> that fails, so a wait that does not end at once is red
    /// rather than a 15-minute stall.
    /// <list type="bullet">
    /// <item>The owner uploaded the version by hand → "served" is true, so the publish step's <c>if:</c> (held by
    /// ReleaseExtensionWorkflowTests) skips it, and the wait ends on its first query with no sleep.</item>
    /// <item>Not uploaded yet → false; and the publish step itself, with the Environment holding no token (an unset secret
    /// is an empty string), refuses naming <c>VSCE_PAT is not set</c> before asking vsce anything — the harmless failure of
    /// an approval given before the upload.</item>
    /// <item>vsce 4.0.0's <c>undefined</c> for an extension it does not know, and a list holding only another version → false.</item>
    /// </list>
    /// What it does not prove: GitHub's Environment approval, the real Marketplace, and github-public's gh calls.</summary>
    [Fact]
    public async Task The_manual_upload_route_finds_the_version_served_skips_the_publish_and_ends_the_wait_at_once()
    {
        Linux();
        var steps = WorkflowShape.Steps(WorkflowShape.Jobs(WorkflowYaml.Load(ReleaseFiles.Workflow("release-extension.yml")))["publish-marketplace"].Map);
        var servedScript = WorkflowShape.Run(steps.Single(s => s.Find("id")?.Text == "served"));
        var publishScript = WorkflowShape.Run(steps.Single(s => WorkflowShape.Run(s).Contains("vsce/vsce publish", StringComparison.Ordinal)));
        var waitScript = WorkflowShape.Run(steps.Single(s => WorkflowShape.Run(s).Contains("vsce show", StringComparison.Ordinal) && WorkflowShape.Run(s).Contains("seq", StringComparison.Ordinal)));
        const string id = "remsoftdev.ai-os-care";

        async Task<(ChildResult Result, string Output, string[] Calls, string[] Sleeps)> RunStep(string script, string answer, string? pat = null)
        {
            using var root = new TempRoot("manual-upload");
            root.File("work/src_vs_code/node_modules/@vscode/vsce/vsce",
                "const fs = require('fs');\n" +
                "const args = process.argv.slice(2);\n" +
                "fs.appendFileSync(process.env.FAKE_VSCE_LOG, args.join(' ') + '\\n');\n" +
                "if (args.length !== 3 || args[0] !== 'show' || args[1] !== process.env.EXTENSION_ID || args[2] !== '--json') { console.error('fake vsce: unexpected ' + args.join(' ')); process.exit(97); }\n" +
                "process.stdout.write(process.env.FAKE_VSCE_ANSWER + '\\n');\n");
            var sleep = root.File("bin/sleep", "#!/bin/sh\necho \"$@\" >> \"$FAKE_SLEEP_LOG\"\nexit 99\n");
            File.SetUnixFileMode(sleep, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var output = root.File("github-output", string.Empty);
            var calls = root.File("vsce.log", string.Empty);
            var sleeps = root.File("sleep.log", string.Empty);
            var env = new Dictionary<string, string?>
            {
                ["PATH"] = $"{Path.GetDirectoryName(sleep)}:{Environment.GetEnvironmentVariable("PATH")}",
                ["VERSION"] = "0.1.0",
                ["EXTENSION_ID"] = id,
                ["GITHUB_OUTPUT"] = output,
                ["FAKE_VSCE_ANSWER"] = answer,
                ["FAKE_VSCE_LOG"] = calls,
                ["FAKE_SLEEP_LOG"] = sleeps,
                ["VSCE_PAT"] = pat,
            };
            var result = await ChildProcess.RunAsync("bash", ["-e", "-o", "pipefail", "-c", script], env, root.Under("work"));
            return (result, File.ReadAllText(output), File.ReadAllLines(calls), File.ReadAllLines(sleeps));
        }

        const string uploaded = "{\"versions\":[{\"version\":\"0.0.9\"},{\"version\":\"0.1.0\"}]}";

        var served = await RunStep(servedScript, uploaded);
        served.Result.Exit.Should().Be(0, served.Result.Stderr);
        served.Output.Should().Contain("served=true", "the hand-uploaded version is found — after a version that is not it");
        served.Calls.Should().Equal($"show {id} --json");

        foreach (var (answer, what) in new[] { ("{\"versions\":[{\"version\":\"0.0.9\"}]}", "another version only"), ("undefined", "vsce 4.0.0's answer for an unknown extension"), ("{\"versions\":[]}", "nothing served") })
        {
            var notServed = await RunStep(servedScript, answer);
            notServed.Result.Exit.Should().Be(0, $"{what}: {notServed.Result.Stderr}");
            notServed.Output.Should().Contain("served=false", what);
        }

        var wait = await RunStep(waitScript, uploaded);
        wait.Result.Exit.Should().Be(0, $"the served version ends the wait: {wait.Result.Stdout}{wait.Result.Stderr}");
        wait.Result.Stdout.Should().Contain("(attempt 1)");
        wait.Calls.Should().HaveCount(1, "one query");
        wait.Sleeps.Should().BeEmpty("no sleep when the version is already served");

        var early = await RunStep(publishScript, uploaded, pat: string.Empty);
        early.Result.Exit.Should().Be(1, "an approval before the upload reaches the publish with no token");
        early.Result.Stdout.Should().Contain("VSCE_PAT is not set", "the refusal names its cause");
        early.Calls.Should().BeEmpty("the refusal comes before vsce is asked anything");
    }

    /// <summary>The attested .vsix as a test builds it: <c>extension/…</c> members plus the root members vsce writes.</summary>
    private static string Vsix(TempRoot root, IReadOnlyDictionary<string, string> extensionFiles)
    {
        var path = root.Under("attested/ai-os-care-0.1.0.vsix");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, content) in new Dictionary<string, string>(extensionFiles.Select(f => KeyValuePair.Create("extension/" + f.Key, f.Value)))
        {
            ["extension.vsixmanifest"] = "<PackageManifest/>",
            ["[Content_Types].xml"] = "<Types/>",
        })
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    /// <summary>The installed folder exactly as VS Code makes it from that .vsix (measured on an installed Marketplace
    /// extension, 2026-10-06): the <c>extension/</c> files, plus <c>.vsixmanifest</c>, with <c>__metadata</c> written into
    /// package.json.</summary>
    private static string Installed(TempRoot root, IReadOnlyDictionary<string, string> extensionFiles)
    {
        var dir = root.Dir("installed/remsoftdev.ai-os-care-0.1.0");
        foreach (var (name, content) in extensionFiles)
        {
            var text = name == "package.json"
                ? content.TrimEnd().TrimEnd('}') + ",\n  \"__metadata\": { \"installedTimestamp\": 1, \"targetPlatform\": \"undefined\", \"size\": 2 }\n}\n"
                : content;
            root.File($"installed/remsoftdev.ai-os-care-0.1.0/{name}", text);
        }

        root.File("installed/remsoftdev.ai-os-care-0.1.0/.vsixmanifest", "<PackageManifest/>");
        return dir;
    }

    /// <summary>coai code round 2 on the manual route (accepted): comparing <c>dist/extension.js</c> alone would pass a
    /// Marketplace package whose package.json points <c>main</c> at an ADDED file. compare-installed-extension.sh compares
    /// EVERY file of the attested .vsix with the installed folder and refuses anything extra, allowing only what VS Code
    /// itself changes — the added <c>.vsixmanifest</c> and the <c>__metadata</c> it writes into package.json. One script,
    /// called by the pre-approval step (docs/repo-settings.md step 9) and by POST_DEPLOY item 6.</summary>
    [Fact]
    public async Task The_installed_extension_must_equal_the_attested_vsix_file_by_file_apart_from_what_VS_Code_adds()
    {
        Linux();
        var files = new Dictionary<string, string>
        {
            ["package.json"] = "{\n  \"name\": \"ai-os-care\",\n  \"main\": \"./dist/extension.js\"\n}\n",
            ["dist/extension.js"] = "exports.activate = () => {};\n",
            ["media/panel.js"] = "// panel\n",
            ["readme.md"] = "# AI OS Care\n",
        };

        using (var root = new TempRoot("installed-same"))
        {
            var same = await ReleaseScripts.RunAsync("compare-installed-extension.sh", [Vsix(root, files), Installed(root, files)], root.Path);
            same.Exit.Should().Be(0, same.Stdout + same.Stderr);
            same.Stdout.Should().Contain("is the attested build (4 files)");
        }

        var tampered = new (string What, Action<string> Tamper, string Says)[]
        {
            ("main pointed at an added file", dir =>
                {
                    File.WriteAllText(Path.Combine(dir, "package.json"), File.ReadAllText(Path.Combine(dir, "package.json")).Replace("./dist/extension.js", "./evil.js", StringComparison.Ordinal));
                    File.WriteAllText(Path.Combine(dir, "evil.js"), "require('child_process');\n");
                }, "package.json: differs"),
            ("an added file alone", dir => File.WriteAllText(Path.Combine(dir, "evil.js"), "x\n"), "evil.js: in the installed folder, not in the attested .vsix"),
            ("the bundle changed", dir => File.AppendAllText(Path.Combine(dir, "dist", "extension.js"), "x"), "dist/extension.js: different bytes"),
            ("a media script changed", dir => File.AppendAllText(Path.Combine(dir, "media", "panel.js"), "x"), "media/panel.js: different bytes"),
            ("a file missing", dir => File.Delete(Path.Combine(dir, "readme.md")), "readme.md: missing"),
        };

        foreach (var (what, tamper, says) in tampered)
        {
            using var root = new TempRoot("installed-tampered");
            var vsix = Vsix(root, files);
            var dir = Installed(root, files);
            tamper(dir);

            var result = await ReleaseScripts.RunAsync("compare-installed-extension.sh", [vsix, dir], root.Path);

            result.Exit.Should().Be(1, $"{what}:\n{result.Stdout}{result.Stderr}");
            result.Stdout.Should().Contain(says, what);
        }

        using (var root = new TempRoot("installed-usage"))
        {
            var usage = await ReleaseScripts.RunAsync("compare-installed-extension.sh", [root.File("not-a-zip.vsix", "x"), root.Dir("empty")], root.Path);
            usage.Exit.Should().Be(2, "an unreadable .vsix is a usage error, never a pass: " + usage.Stderr);
        }
    }
}
