using System.Text.RegularExpressions;

using FluentAssertions;

using static WslCare.Scenarios.WorkflowShape;

namespace WslCare.Scenarios;

/// <summary>
/// The EXTENSION release pipeline's structure (plan §15g M4/M5 and §15h #0; E5.S3), read from
/// <c>release-extension.yml</c> with <see cref="WorkflowYaml"/>: a tag-only trigger, exactly what each job may do, the
/// Marketplace secret's Environment on one job alone, the order draft upload → Marketplace → public (so a Marketplace
/// success never leaves the rollback source missing), the idempotent Marketplace skip, a guard that includes the minimum
/// daemon and POST_DEPLOY's stamp, and one package that is checked, attested and published. Released rarely and
/// expensively, so a mistake here would otherwise surface on a release day. The every-workflow rules — SHA-pinned
/// actions, no expression pasted into a <c>run:</c>, a ceiling per job, no token left in a checkout — are
/// <see cref="ReleaseWorkflowTests"/>' and cover this file too.
/// </summary>
public sealed partial class ReleaseExtensionWorkflowTests
{
    private const string Workflow = "release-extension.yml";
    private const string GuardScript = ".github/scripts/release-extension-guard.sh";
    private const string AssetsScript = ".github/scripts/verify-extension-assets.sh";
    private const string MinDaemonFile = "src_vs_code/min-daemon.json";
    private const string RerunHint = "Re-run FAILED jobs only";

    private static YamlMap Load() => WorkflowYaml.Load(ReleaseFiles.Workflow(Workflow));

    private static YamlMap Job(string id) => Jobs(Load())[id].Map;

    private static IReadOnlyList<string> Needs(YamlMap job) => job.Find("needs") switch
    {
        null => [],
        YamlScalar s => [s.Value],
        YamlSequence s => [.. s.Values.Select(v => v.Text)],
        _ => throw new InvalidOperationException("needs is neither a scalar nor a sequence"),
    };

    /// <summary>The tag prefix this file triggers on (<c>extension-v</c>), read from it.</summary>
    internal static string TagPrefix() => Load()["on"].Map["push"].Map["tags"].Items.Single().Text.TrimEnd('*');

    [Fact]
    public void The_release_is_started_by_an_extension_tag_push_and_nothing_else()
    {
        var on = Load()["on"].Map;

        on.Keys.Should().Equal(["push"], "no pull_request, no workflow_dispatch: no fork and no button reaches id-token, contents: write or the Marketplace secret");
        on["push"].Map.Keys.Should().Equal(["tags"], "a branch push is not a release");
        TagPrefix().Should().Be("extension-v", "the tag release-please cuts for the extension package");
        ReleaseWorkflowTests.TagPrefix().Should().NotBe(TagPrefix(), "the daemon's release.yml never starts on an extension tag");
    }

    [Fact]
    public void Every_job_holds_exactly_what_it_needs_and_the_workflow_defaults_to_reading()
    {
        Permissions(Load()).Should().Equal(new Dictionary<string, string> { ["contents"] = "read" });
        var read = new Dictionary<string, string> { ["contents"] = "read" };
        var write = new Dictionary<string, string> { ["contents"] = "write" };
        var expected = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["guard"] = read,
            ["build"] = read,
            ["attest"] = new Dictionary<string, string> { ["contents"] = "read", ["id-token"] = "write", ["attestations"] = "write" },
            ["github-draft"] = write,
            ["publish-marketplace"] = read,
            ["github-public"] = write,
        };

        Jobs(Load()).Keys.Should().Equal(expected.Keys, "the six jobs, in their order");
        foreach (var (id, permissions) in expected)
        {
            Permissions(Job(id)).Should().Equal(permissions, $"{id}: job-level permissions only, nothing more (plan §15e #0)");
        }
    }

    [Fact]
    public void The_marketplace_secret_lives_in_one_Environment_on_one_job_and_nowhere_else()
    {
        foreach (var (id, job) in Jobs(Load()).Entries.Select(e => (e.Key, e.Value.Map)))
        {
            if (id == "publish-marketplace")
            {
                job["environment"].Text.Should().Be("marketplace", "the protected Environment: a required reviewer, extension-v* tags only");
            }
            else
            {
                job.Has("environment").Should().BeFalse($"{id} must not reach the marketplace Environment");
            }
        }

        var holders = ReleaseFiles.AllWorkflows.SelectMany(path => Jobs(WorkflowYaml.Load(path)).Entries
                .Where(e => JobText(path, e.Key).Contains("VSCE_PAT", StringComparison.Ordinal))
                .Select(e => $"{Path.GetFileName(path)}/{e.Key}"))
            .ToList();
        holders.Should().Equal([$"{Workflow}/publish-marketplace"], "only the Marketplace job names the secret — never a daemon job, never a pull request");
        Steps(Job("publish-marketplace")).Single(s => s.Find("env") is YamlMap env && env.Has("VSCE_PAT"))["env"].Map["VSCE_PAT"].Text
            .Should().Be("${{ secrets.VSCE_PAT }}", "reaches the step through env:, never pasted into the shell");
    }

    /// <summary>The source text of one job — from its key line to the next job's, so a secret named in any form is seen.</summary>
    private static string JobText(string path, string id)
    {
        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l == $"  {id}:");
        var end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, l => JobKeyLine().IsMatch(l));
        return start < 0 ? string.Empty : string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    [Fact]
    public void The_order_is_guard_build_draft_upload_marketplace_then_public_and_nothing_runs_past_a_failure()
    {
        Needs(Job("build")).Should().Equal("guard");
        Needs(Job("attest")).Should().BeEquivalentTo(["guard", "build"]);
        Needs(Job("github-draft")).Should().BeEquivalentTo(["guard", "build", "attest"], "nothing reaches a release before its attestation exists");
        Needs(Job("publish-marketplace")).Should().Contain("github-draft", "the rollback source (the .vsix on the draft) exists BEFORE the Marketplace serves anything (§15h #0)");
        Needs(Job("github-public")).Should().Contain(["github-draft", "publish-marketplace"], "the release goes public only after the Marketplace serves the version");
        Needs(Job("github-draft")).Should().NotContain("publish-marketplace");
        Jobs(Load()).Entries.Should().OnlyContain(e => !e.Value.Map.Has("if"), "an if: (always(), failure()) would let a step run past a failed one");
        Jobs(Load()).Entries.Should().OnlyContain(e => !e.Value.Map.Has("continue-on-error"));
    }

    [Fact]
    public void The_marketplace_is_skipped_when_it_already_serves_the_version_and_publishes_the_attested_file_otherwise()
    {
        var job = Job("publish-marketplace");
        var steps = Steps(job);
        var served = steps.ToList().FindIndex(s => s.Find("id")?.Text == "served");
        var publish = StepIndex(job, "vsce/vsce publish");
        var wait = steps.ToList().FindIndex(s => Run(s).Contains(WaitScript, StringComparison.Ordinal));

        new[] { served, publish, wait }.Should().NotContain(-1).And.BeInAscendingOrder("ask, publish only when not served, then wait until served");
        Run(steps[served]).Should().Contain("vsce show").And.Contain("served=", "the skip is decided by what the Marketplace serves");
        steps[publish]["if"].Text.Should().Be("steps.served.outputs.served != 'true'", "a re-run after a publish publishes nothing");
        Run(steps[publish]).Should().Contain($"--packagePath \"from-build/{ReleaseFiles.ExtensionVsix("$VERSION")}\"", "the very file the build attested");
        job["env"].Map["EXTENSION_ID"].Text.Should().Be($"${{{{ needs.guard.outputs.publisher }}}}.{ReleaseFiles.ExtensionName}", "the Marketplace id is <publisher>.<the manifest's name>");
        StepIndex(job, AssetsScript).Should().BeInRange(0, served, "the downloaded file is checked against its .sha256 before anything uses it");
        Steps(job).Single(s => Uses(s).StartsWith("actions/download-artifact@", StringComparison.Ordinal))["with"].Map["name"].Text
            .Should().Be(BuildArtifactName(), "the artifact the build uploaded after attesting it");
        Run(steps[StepIndex(job, "npm ci")]).Should().Contain("--ignore-scripts", "no dependency's install script runs beside the Marketplace secret");
    }

    private const string WaitScript = "wait-marketplace-served.sh";

    /// <summary>2026-10-09, extension 0.2.0: `vsce publish` succeeded at 15:58:44Z, but `vsce show` still did not list 0.2.0 when the
    /// job's 20-minute timeout cancelled it at 16:23:28Z (the gallery's own query served it at 16:12Z) — and a cancelled job keeps
    /// no log of what each attempt saw. The wait now lasts at least 45 minutes, inside a job limit at least 10 minutes longer, so
    /// the wait's own error — naming what every attempt saw — is what ends a slow propagation, never the job's timeout.</summary>
    [Fact]
    public void The_marketplace_wait_outlasts_the_propagation_seen_on_2026_10_09_and_the_job_outlasts_the_wait()
    {
        var job = Job("publish-marketplace");
        var wait = Steps(job).Should().ContainSingle(s => Run(s).Contains(WaitScript, StringComparison.Ordinal)).Subject;
        var call = WaitCall().Match(Run(wait));

        call.Success.Should().BeTrue($"the wait runs {WaitScript} \"$EXTENSION_ID\" \"$VERSION\" <attempts> <interval seconds> <attempt timeout seconds>: {Run(wait)}");
        var (attempts, interval, perAttempt) = (Number(call, 1), Number(call, 2), Number(call, 3));
        TimeSpan.FromSeconds(attempts * interval).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(45), "the propagation to what vsce show reads exceeded 20 minutes on 2026-10-09");
        perAttempt.Should().BePositive("coai plan round 2026-10-09: one hung vsce show must not eat the job — each attempt is bounded");
        var grace = KillGrace().Match(File.ReadAllText(ReleaseFiles.Script(WaitScript)));
        grace.Success.Should().BeTrue($"{WaitScript} names its kill grace as kill_grace=<seconds> and passes it to timeout -k");
        // coai code round 2026-10-09: a vsce show that ignores TERM lives the kill grace past its bound, so that counts too.
        TimeSpan.FromMinutes(Number(job["timeout-minutes"].Text))
            .Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(attempts * (interval + perAttempt + Number(grace, 1))) + TimeSpan.FromMinutes(10), "the job outlasts even a wait whose every attempt hangs to its bound and its kill grace, so the wait's error with its log ends it, not the timeout");
    }

    /// <summary>coai code round 2026-10-09: the skip step and the wait decide "served" from <c>vsce show --json</c> with ONE
    /// reader — two copies could disagree, and a skip that misses what the wait calls served publishes again.</summary>
    [Fact]
    public void The_skip_and_the_wait_decide_served_with_one_reader()
    {
        var served = Run(Steps(Job("publish-marketplace")).Single(s => s.Find("id")?.Text == "served"));
        var wait = File.ReadAllText(ReleaseFiles.Script(WaitScript));

        File.Exists(ReleaseFiles.Script(ServedReader)).Should().BeTrue($".github/scripts/{ServedReader} is the one reader");
        served.Should().Contain($".github/scripts/{ServedReader}").And.NotContain("JSON.parse", "the skip step parses nothing itself");
        wait.Should().Contain(ServedReader).And.NotContain("JSON.parse", "the wait parses nothing itself");
    }

    private const string ServedReader = "marketplace-served.cjs";

    [GeneratedRegex("""wait-marketplace-served\.sh "\$EXTENSION_ID" "\$VERSION" (\d+) (\d+) (\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex WaitCall();

    [GeneratedRegex("""(?m)^kill_grace=(\d+)\r?$""", RegexOptions.CultureInvariant)]
    private static partial Regex KillGrace();

    private static int Number(Match match, int group) => Number(match.Groups[group].Value);

    private static int Number(string text) => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>E5 code round (security #2): a re-run of ALL jobs rebuilds a different .vsix, while the Marketplace may
    /// already serve the first. So an asset on the release is never replaced — not even on a draft — and the release is
    /// compared with THIS run's attested build both before the Marketplace and immediately before going public.</summary>
    [Fact]
    public void Every_github_step_is_rerunnable_and_bytes_on_a_release_are_never_replaced()
    {
        var draft = Job("github-draft");
        var upload = Run(Steps(draft)[StepIndex(draft, "gh release upload")]);
        upload.Should().Contain("isDraft").And.Contain("already public", "uploads onto a draft; a re-run on a public release uploads nothing")
            .And.Contain("never replaced", "an asset already on the draft is compared, never replaced");
        upload.Should().NotContain("--clobber", "a clobber would put a re-run's rebuilt .vsix beside a Marketplace that serves the first one");
        var readBack = Steps(draft).ToList().FindIndex(s => Run(s).Contains("gh release download", StringComparison.Ordinal) && Run(s).Contains(AssetsScript, StringComparison.Ordinal) && Run(s).Contains("cmp ", StringComparison.Ordinal));
        readBack.Should().BeGreaterThan(StepIndex(draft, "gh release upload"), "the release is read back and compared byte for byte with the build's file");
        Run(Steps(draft)[readBack]).Should().Contain(RerunHint, "a difference is a re-run of ALL jobs, and the refusal says what to do instead");

        var pub = Job("github-public");
        var steps = Steps(pub);
        var fromBuild = steps.ToList().FindIndex(s => Uses(s).StartsWith("actions/download-artifact@", StringComparison.Ordinal));
        var compare = steps.ToList().FindIndex(s => Run(s).Contains("cmp ", StringComparison.Ordinal) && Run(s).Contains("from-build/", StringComparison.Ordinal) && Run(s).Contains("from-release/", StringComparison.Ordinal));
        var goPublic = StepIndex(pub, "--draft=false");
        new[] { fromBuild, compare, goPublic }.Should().NotContain(-1).And.BeInAscendingOrder("the attested build is fetched, the draft compared with it, and only then is it made public");
        steps[fromBuild]["with"].Map["name"].Text.Should().Be(BuildArtifactName(), "the very file the attest job signed");
        Run(steps[compare]).Should().Contain(AssetsScript).And.Contain(RerunHint);
        Run(steps[goPublic]).Should().Contain("already public", "a re-run on a public release changes nothing");
        goPublic.Should().Be(steps.Count - 1, "nothing runs after the release is public");
        ReleaseFiles.AllWorkflows.Sum(p => File.ReadAllText(p).Split("--draft=false").Length - 1).Should().Be(2, "one place per release workflow makes a release public");
    }

    [Fact]
    public void The_build_packages_once_checks_that_file_and_uploads_it_and_signs_nothing()
    {
        var build = Job("build");
        var steps = Steps(build);
        int[] order =
        [
            StepIndex(build, "npm test"),
            StepIndex(build, "npm run package"),
            StepIndex(build, "scripts/check-vsix.mjs"),
            StepIndex(build, AssetsScript.Replace(".github", "../.github", StringComparison.Ordinal)),
            StepIndex(build, "actions/upload-artifact@"),
        ];

        order.Should().NotContain(-1).And.BeInAscendingOrder("test, package, check the artefact, check the set, upload it");
        StepIndex(build, "actions/attest-build-provenance@").Should().Be(-1, "the build runs npm install scripts and a downloaded VS Code: it holds no signing scope and attests nothing");
        steps.Count(s => Run(s).Contains("npm run package", StringComparison.Ordinal) || Run(s).Contains("vsce package", StringComparison.Ordinal)).Should().Be(1, "vsce package ONCE");
        Run(steps[order[2]]).Should().Contain("--release", "a release refuses the placeholder publisher")
            .And.Contain("--min-daemon \"$MIN_DAEMON\"", "the built minimum is the one the guard found published and verified")
            .And.Contain("--min-daemon-actions \"$MIN_DAEMON_ACTIONS\"", "the actions minimum too (E6.S2, plan §15j M5)")
            .And.Contain("--install-daemon \"$INSTALL_DAEMON\"", "the release the .vsix installs is the one the guard found published and verified (#37)")
            .And.Contain("--root-allowed \"$ROOT_ALLOWED\"", "the guard's tag answer reaches the artefact check (plan §15j B3, §15k #7)");
        steps[order[4]]["with"].Map["path"].Text.Should().Be("release-extension/", "the .vsix and its .sha256 travel together");
        build["env"].Map["VERSION"].Text.Should().Be("${{ needs.guard.outputs.version }}", "the build packs the version the guard approved");
        build["env"].Map["MIN_DAEMON"].Text.Should().Be("${{ needs.guard.outputs.min_daemon }}");
        build["env"].Map["MIN_DAEMON_ACTIONS"].Text.Should().Be("${{ needs.guard.outputs.min_daemon_actions }}");
        build["env"].Map["INSTALL_DAEMON"].Text.Should().Be("${{ needs.guard.outputs.install_daemon }}");
        build["env"].Map["ROOT_ALLOWED"].Text.Should().Be("${{ needs.guard.outputs.root_allowed }}");
        File.ReadAllText(ReleaseFiles.Workflow("ci-extension.yml")).Should().Contain("npm run package").And.Contain("npm run check:vsix", "every pull request packages and checks the same way (plan §15g M8)");
    }

    /// <summary>E5 code round #3 (and security #1): the ONLY job with <c>id-token</c> / <c>attestations: write</c> downloads
    /// the build's artifact, checks the pair and attests it — no dependency checkout, no npm, no node, no editor.</summary>
    [Fact]
    public void The_attest_job_signs_the_downloaded_build_and_runs_nothing_else()
    {
        var attest = Job("attest");
        var steps = Steps(attest);

        steps.Select(s => Uses(s).Split('@')[0] is { Length: > 0 } action ? action : "run").Should().Equal(
            ["actions/checkout", "actions/download-artifact", "run", "actions/attest-build-provenance"],
            "the verification script's folder, the build's artifact, the pair checked, the attestation — nothing else");
        var checkout = steps[0]["with"].Map;
        checkout["sparse-checkout"].Text.Should().Be(".github/scripts", "only the verification script — no package.json, no lock file, nothing to install");
        checkout["persist-credentials"].Text.Should().Be("false");
        steps[1]["with"].Map["name"].Text.Should().Be(BuildArtifactName());
        steps[1]["with"].Map["path"].Text.Should().Be("from-build");
        Run(steps[2]).Should().Contain(AssetsScript).And.Contain("from-build");
        steps[3]["with"].Map["subject-path"].Text.Should().Be($"from-build/{ReleaseFiles.ExtensionVsix("${{ needs.guard.outputs.version }}")}", "the attested subject is the file the build packaged, checked against its .sha256");
        steps.Should().OnlyContain(s => !Run(s).Contains("npm", StringComparison.Ordinal) && !Run(s).Contains("node ", StringComparison.Ordinal) && !Uses(s).StartsWith("actions/setup-node", StringComparison.Ordinal),
            "no package manager and no JavaScript runtime beside the signing scope");
    }

    private static string BuildArtifactName() =>
        Steps(Job("build")).Single(s => Uses(s).StartsWith("actions/upload-artifact@", StringComparison.Ordinal))["with"].Map["name"].Text;

    [Fact]
    public void The_guard_runs_first_on_the_whole_history_and_includes_the_minimum_daemon_and_the_post_deploy_stamp()
    {
        var guard = Job("guard");
        var checkout = Steps(guard).Single(s => Uses(s).StartsWith("actions/checkout@", StringComparison.Ordinal));
        checkout["with"].Map["fetch-depth"].Text.Should().Be("0");
        var step = Steps(guard)[StepIndex(guard, GuardScript)];
        Run(step).Should().Contain("\"$GITHUB_REF_NAME\" origin/main");
        step["env"].Map["GH_TOKEN"].Text.Should().Be("${{ github.token }}", "the job's read-only token asks for the daemon release");
        guard["outputs"].Map.Keys.Should().Equal(GuardOutputNames(), "every line the guard emits is a declared output (E5 code round #7; E6.S2)");
        guard["outputs"].Map["min_daemon"].Text.Should().Be("${{ steps.guard.outputs.min_daemon }}");
        guard["outputs"].Map["min_daemon_actions"].Text.Should().Be("${{ steps.guard.outputs.min_daemon_actions }}");
        guard["outputs"].Map["root_allowed"].Text.Should().Be("${{ steps.guard.outputs.root_allowed }}");

        var script = File.ReadAllText(Path.Combine(ReleaseFiles.Root, GuardScript));
        script.Should().Contain("min-daemon.json").And.Contain("releases/tags/$daemon_tag").And.Contain("POST_DEPLOY.md").And.Contain("publisher-tbd")
            .And.Contain("refs/tags/$first_public^{commit}", "B3 is keyed on the TAGS (plan §15k #7), never on the release-please manifest")
            .And.Contain("refs/tags/$first_public:$ROOT_MODULE", "the first public tag's OWN tree must be root-free (E6.S2 review S1)")
            .And.Contain("published_release \"$first_public\"", "and it must be a published, non-draft release (E6.S2 review S1)")
            .And.Contain($"{TagPrefix()}*)", "the guard reads the tag shape this workflow triggers on");
        script.Should().NotContain("handshake.ts", "the guard reads the JSON artefact, never TypeScript with a line pattern (E5 code round #2/#5)");
    }

    /// <summary>The guard reads the minima from the checked-in artefact <c>src_vs_code/min-daemon.json</c> — what the bundle
    /// step emits from <c>MIN_DAEMON_FOR_RENDER</c> and (since E6.S2, plan §15j M5) <c>MIN_DAEMON_FOR_ACTIONS</c>, held equal to
    /// them by the extension's tests and by check-vsix. Its shape is held here, on every OS, rather than discovered on a
    /// release day.</summary>
    [Fact]
    public void The_daemon_versions_artefact_the_guard_reads_is_three_json_members_each_an_x_y_z()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ReleaseFiles.Root, MinDaemonFile)));

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(["minDaemonForRender", "minDaemonForActions", "installDaemon"]);
        foreach (var member in json.RootElement.EnumerateObject())
        {
            member.Value.GetString().Should().MatchRegex(@"^\d+\.\d+\.\d+$", $"{member.Name} is what the guard compares and asks GitHub about");
        }
    }

    /// <summary>E5 code round #6: POST_DEPLOY item 6 compares by CONTAINMENT and RANK — the newest published extension tag's
    /// version is among the Marketplace's versions and none of them sorts above it — through the guard's own comparison
    /// (<c>lib/versions.sh</c>), never by the list's first entry; and it reads the .vsix with python3, which stock Ubuntu
    /// has, rather than unzip, which it does not.</summary>
    [Fact]
    public void Post_deploy_item_6_ranks_the_served_versions_with_the_guards_comparison_and_needs_no_unzip()
    {
        var row = File.ReadAllLines(Path.Combine(ReleaseFiles.Root, "POST_DEPLOY.md")).Single(l => l.StartsWith("| 6 |", StringComparison.Ordinal));
        var guard = File.ReadAllText(Path.Combine(ReleaseFiles.Root, GuardScript));

        row.Should().Contain(". .github/scripts/lib/versions.sh").And.Contain("highest_version").And.Contain("is_top_version");
        row.Should().Contain("bash .github/scripts/compare-installed-extension.sh", "the installed extension is compared file by file by the ONE script the manual upload's pre-approval step also runs (coai code round 2)")
            .And.NotContain("dist/extension.js", "a bundle-only comparison misses a package.json pointing main at an added file");
        File.ReadAllText(ReleaseFiles.Script("compare-installed-extension.sh")).Should().Contain("zipfile", "the .vsix is read with python3, on stock Ubuntu")
            .And.NotContain("unzip", "not on stock Ubuntu");
        row.Should().NotContain("versions[0]", "the first listed version is not the highest one").And.NotContain("unzip", "not on stock Ubuntu");
        row.Should().Contain("require('./src_vs_code/package.json').name", "the extension's name is read from the manifest, like its publisher")
            .And.NotContain($"$p.{ReleaseFiles.ExtensionName}", "the id is never retyped beside the manifest")
            .And.NotContain(ReleaseFiles.ExtensionVsix("$v"), "nor the .vsix name");
        guard.Should().Contain("lib/versions.sh").And.Contain("version_at_least", "the guard and the item share one comparison");
        guard.Should().NotContain("sort -t.", "no second spelling of the comparison");
    }

    [Fact]
    public void The_extension_tag_ruleset_protects_exactly_the_extension_tags_and_lets_only_the_release_App_through()
    {
        var extension = File.ReadAllText(ReleaseFiles.Ruleset("tags-extension.json"));
        var daemon = File.ReadAllText(ReleaseFiles.Ruleset("tags-daemon.json"));

        extension.Should().Contain($"\"refs/tags/{TagPrefix()}*\"");
        extension.Replace("extension", "X", StringComparison.Ordinal).Should().Be(daemon.Replace("daemon", "X", StringComparison.Ordinal),
            "the same rules and the same single bypass (the release App) as the daemon's — only the pattern and the name differ");
        daemon.Should().NotContain("extension", "tags-daemon.json is not edited (plan §15g M5 (3))");
    }

    /// <summary>The documents a person copies a <c>gh attestation verify</c> command from.</summary>
    private static readonly string[] AttestationDocuments = ["README.md", "POST_DEPLOY.md", "docs/repo-settings.md"];

    /// <summary>Retro review of PR #8, O4: the extension's rollback check (docs/repo-settings.md and the README) ran
    /// <c>gh attestation verify … --signer-workflow …/release-extension.yml</c>, which gh matches as a literal PREFIX of the
    /// identity — release-extension.yml run from ANY ref passed (install.sh's own header says so). Every documented command that
    /// verifies a file pins the exact identity: <c>--cert-identity …@refs/tags/…</c>, <c>--repo</c>,
    /// <c>--deny-self-hosted-runners</c> — and never <c>--signer-workflow</c>. A mention of a flag (<c>`gh attestation verify
    /// --bundle`</c> in prose) verifies nothing and is not a command here.</summary>
    [Fact]
    public void Every_documented_attestation_check_pins_the_exact_identity_never_a_signer_workflow_prefix()
    {
        var commands = AttestationDocuments
            .SelectMany(doc => VerifyCommand().Matches(File.ReadAllText(Path.Combine(ReleaseFiles.Root, doc))).Select(m => (Doc: doc, Command: Whitespace().Replace(m.Value, " "))))
            .Where(c => !c.Command.StartsWith("gh attestation verify --", StringComparison.Ordinal))
            .ToList();

        commands.Select(c => c.Doc).Distinct().Should().BeEquivalentTo(AttestationDocuments,
            "the scan still finds the known checks — POST_DEPLOY items 6 and 9, the rollback in the README and in docs/repo-settings.md");
        commands.Should().Contain(c => c.Doc == "docs/repo-settings.md" && c.Command.Contains("extension-v<x.y.z>", StringComparison.Ordinal),
            "the scan reads a command wrapped across two lines — the manual Marketplace upload's draft check");
        commands.Should().OnlyContain(
            c => c.Command.Contains("--cert-identity \"https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/", StringComparison.Ordinal)
                && c.Command.Contains("@refs/tags/", StringComparison.Ordinal)
                && c.Command.Contains("--repo oleksandrdubyna88/wsl_care", StringComparison.Ordinal)
                && c.Command.Contains("--deny-self-hosted-runners", StringComparison.Ordinal)
                && !c.Command.Contains("--signer-workflow", StringComparison.Ordinal),
            "a signer workflow is a prefix match, so it admits the workflow run from any branch");
    }

    /// <summary>One <c>gh attestation verify</c> command, up to where a shell or markdown would end it. An inline code span wraps
    /// onto the next line in these documents (markdown reads the line break as a space), so a line break ends a command only
    /// before a blank line — a wrapped command read up to its first line break loses its identity flags.</summary>
    [GeneratedRegex("""gh attestation verify (?:[^`|;&\n]|\n(?![ \t\r]*\n))*""")]
    private static partial Regex VerifyCommand();

    /// <summary>A run of whitespace — a wrapped command's line break and indent read as the one space markdown renders.</summary>
    [GeneratedRegex("""\s+""")]
    private static partial Regex Whitespace();

    [GeneratedRegex("""^  [a-z][a-z0-9-]*:\s*$""")]
    private static partial Regex JobKeyLine();

    /// <summary>The names the guard emits, read from its own `for line in "name=$value" …; do` loop — the source of truth the
    /// workflow's declared outputs are held to (coai round 8: never a second hand-kept list).</summary>
    private static IReadOnlyList<string> GuardOutputNames()
    {
        var loop = File.ReadAllLines(Path.Combine(ReleaseFiles.Root, GuardScript)).Single(l => l.StartsWith("for line in \"version=", StringComparison.Ordinal));
        var names = System.Text.RegularExpressions.Regex.Matches(loop, "\"([a-z_]+)=").Select(m => m.Groups[1].Value).ToList();
        names.Should().Contain(["version", "install_daemon", "root_allowed"], "the parse still finds known outputs");
        return names;
    }
}
