using System.Text.RegularExpressions;

using FluentAssertions;

using static WslCare.Scenarios.WorkflowShape;

namespace WslCare.Scenarios;

/// <summary>
/// The release pipeline's STRUCTURE (plan §9, §15 #12, §15e #0/#2/#5; E4.S2), read from the workflow files with
/// <see cref="WorkflowYaml"/> and held on every OS: who may sign, who may write, what the publish job waits for, which
/// RIDs ship on which runners, and that the released binary is smoked by the same script as every pull request. A
/// release runs rarely and expensively, so a mistake here would otherwise be found on a release day (testing rule, "a
/// check that only runs during a release has never run").
/// </summary>
public sealed partial class ReleaseWorkflowTests
{
    private const string Release = "release.yml";
    private const string CiDaemon = "ci-daemon.yml";
    private const string Proposer = "release-please.yml";
    private const string ExtensionRelease = "release-extension.yml";
    private const string SmokeScript = ".github/scripts/smoke-daemon.sh";
    private const string PackageScript = ".github/scripts/package-daemon.sh";
    private const string VerifyScript = ".github/scripts/verify-release-assets.sh";
    private const string GuardScript = ".github/scripts/release-guard.sh";

    private static YamlMap Load(string name) => WorkflowYaml.Load(ReleaseFiles.Workflow(name));

    private static YamlMap Job(string workflow, string id) => Jobs(Load(workflow))[id].Map;

    /// <summary>Every job of every workflow, as (file, id, job).</summary>
    private static IEnumerable<(string File, string Id, YamlMap Job)> AllJobs() =>
        ReleaseFiles.AllWorkflows.SelectMany(path => Jobs(WorkflowYaml.Load(path)).Entries.Select(e => (Path.GetFileName(path), e.Key, e.Value.Map)));

    [Fact]
    public void Every_workflow_parses_with_the_reader_its_checks_use()
    {
        var parsed = ReleaseFiles.AllWorkflows.Select(path => (Name: Path.GetFileName(path), Workflow: WorkflowYaml.Load(path))).ToList();

        parsed.Select(p => p.Name).Should().Contain([Release, ExtensionRelease, Proposer, CiDaemon, "ci-workflows.yml", "family-checks.yml", "pr-title.yml", "sonarcloud.yml", "coderabbit-review.yml"]);
        parsed.Should().OnlyContain(p => p.Workflow.Has("on") && p.Workflow.Has("jobs"), "a workflow has triggers and jobs");
        // The one positive beside the structural reads: the reader sees the ci-daemon matrix the file really holds.
        RunnerPerRid(Job(CiDaemon, "build-test-publish")).Keys.Should().BeEquivalentTo(ReleaseFiles.DaemonRids, "the reader reads the matrix that is there");
    }

    [Fact]
    public void The_reader_refuses_what_it_does_not_understand_rather_than_guessing()
    {
        var refused = new[]
        {
            "a: &anchor 1\n",
            "a:\n\tb: 1\n",
            "a: >\n  folded\n",
            "a: plain\n  continued\n",
            "a: 1\na: 2\n",
            "a: {b: 1}\n",
        };
        foreach (var text in refused)
        {
            var parse = () => WorkflowYaml.Parse(text, "planted.yml");
            parse.Should().Throw<NotSupportedException>($"{text.Replace("\n", "\\n", StringComparison.Ordinal)} is outside the subset");
        }

        var read = WorkflowYaml.Parse("on:\n  push:\n    tags: ['daemon-v*'] # c\njobs:\n  b:\n    steps:\n      - uses: x@y # v1\n        with:\n          k: \"v\"\n      - run: |\n          one\n          two\n", "known.yml").Map;
        read["on"].Map["push"].Map["tags"].Items.Select(i => i.Text).Should().Equal("daemon-v*");
        var steps = Steps(read["jobs"].Map["b"].Map);
        Uses(steps[0]).Should().Be("x@y");
        steps[0]["with"].Map["k"].Text.Should().Be("v");
        Run(steps[1]).Should().Be("one\ntwo\n");

        // The quoted forms the reader unescapes (kept equal through the E4 review's complexity refactor).
        var quoted = WorkflowYaml.Parse("a: 'it''s # not a comment' # c\nb: \"say \\\"hi\\\" \\\\ done\"\nc: ''\n", "quoted.yml").Map;
        quoted["a"].Text.Should().Be("it's # not a comment");
        quoted["b"].Text.Should().Be("say \"hi\" \\ done");
        quoted["c"].Text.Should().BeEmpty();
        var unterminated = () => WorkflowYaml.Parse("a: 'open''\n", "quoted.yml");
        unterminated.Should().Throw<NotSupportedException>("a doubled quote is an escape, not an end");
    }

    [Fact]
    public void The_release_is_started_by_a_daemon_tag_push_and_nothing_else()
    {
        var on = Load(Release)["on"].Map;

        on.Keys.Should().Equal(["push"], "no pull_request, no workflow_dispatch: no fork and no button reaches id-token or contents: write");
        on["push"].Map.Keys.Should().Equal(["tags"], "a branch push is not a release");
        on["push"].Map["tags"].Items.Select(i => i.Text).Should().Equal([$"{TagPrefix()}*"], "the tag release-please cuts, and only it");
    }

    [Fact]
    public void Release_workflows_default_to_reading_and_every_release_job_declares_its_own_permissions()
    {
        foreach (var name in new[] { Release, ExtensionRelease, Proposer })
        {
            Permissions(Load(name)).Should().Equal(new Dictionary<string, string> { ["contents"] = "read" }, $"{name}: workflow-level permissions stay contents: read (plan §15e #0)");
        }

        Jobs(Load(Release)).Entries.Should().OnlyContain(e => e.Value.Map.Has("permissions"), "each release job names exactly what it needs");
    }

    /// <summary>The permission checks below read <c>permissions:</c> blocks; a workflow with NONE inherits the repository's
    /// default token scope (possibly write-all) and would pass every one of them vacuously (E4 review B5).</summary>
    [Fact]
    public void Every_workflow_declares_its_permissions_at_the_top_or_on_every_job()
    {
        ReleaseFiles.AllWorkflows.Where(path => !DeclaresPermissions(WorkflowYaml.Load(path))).Select(Path.GetFileName).Should().BeEmpty(
            "a workflow without a permissions block gets the repository's default token, and no per-job check can see it");

        // The rule's two shapes and its refusal, on planted workflows — the scan's known instances.
        DeclaresPermissions(WorkflowYaml.Parse("on:\n  push:\njobs:\n  a:\n    runs-on: x\n", "planted.yml").Map).Should().BeFalse("no block anywhere");
        DeclaresPermissions(WorkflowYaml.Parse("on:\n  push:\npermissions:\n  contents: read\njobs:\n  a:\n    runs-on: x\n", "planted.yml").Map).Should().BeTrue();
        DeclaresPermissions(WorkflowYaml.Parse("on:\n  push:\njobs:\n  a:\n    permissions: {}\n  b:\n    runs-on: x\n", "planted.yml").Map).Should().BeFalse("job b has none");
    }

    private static bool DeclaresPermissions(YamlMap workflow) =>
        workflow.Has("permissions") || Jobs(workflow).Entries.All(e => e.Value.Map.Has("permissions"));

    [Fact]
    public void Only_the_daemon_build_and_the_extension_attest_job_can_sign_and_they_write_nothing_to_the_repository()
    {
        var signers = AllJobs().Where(j => SignsWith(Permissions(j.Job))).Select(j => $"{j.File}/{j.Id}").ToList();
        var workflowLevel = ReleaseFiles.AllWorkflows.Where(p => SignsWith(Permissions(WorkflowYaml.Load(p)))).ToList();

        signers.Should().BeEquivalentTo([$"{Release}/build", $"{ExtensionRelease}/attest"],
            "id-token / attestations are the daemon's build job's and the extension's attest job's alone (plan §15e #0, §15g M5; E5 code round #3: the extension's build runs npm install scripts and a downloaded VS Code, so it signs nothing)");
        workflowLevel.Should().BeEmpty("no workflow grants a signing scope to all its jobs");
        Permissions(Job(Release, "build")).Should().Equal(new Dictionary<string, string>
        {
            ["contents"] = "read",
            ["id-token"] = "write",
            ["attestations"] = "write",
        });
    }

    [Fact]
    public void Only_the_release_publish_jobs_can_write_the_repository()
    {
        var writers = AllJobs().Where(j => WritesContents(Permissions(j.Job))).Select(j => $"{j.File}/{j.Id}").ToList();
        var workflowLevel = ReleaseFiles.AllWorkflows.Where(p => WritesContents(Permissions(WorkflowYaml.Load(p)))).ToList();

        writers.Should().BeEquivalentTo([$"{Release}/publish", $"{ExtensionRelease}/github-draft", $"{ExtensionRelease}/github-public"],
            "contents: write is the daemon aggregator's and the extension's two GitHub-release jobs' alone (plan §15e #0, §15h #0) — the proposer writes with the App token");
        workflowLevel.Should().BeEmpty();
        Permissions(Job(Release, "publish")).Should().Equal(new Dictionary<string, string> { ["contents"] = "write" });
    }

    [Fact]
    public void The_publish_job_waits_for_the_guard_and_every_build_leg_and_cannot_run_past_a_failure()
    {
        var publish = Job(Release, "publish");
        var build = Job(Release, "build");

        publish["needs"].Items.Select(i => i.Text).Should().BeEquivalentTo(["guard", "build"], "one red leg publishes nothing (plan §15e #2)");
        build["needs"].Text.Should().Be("guard", "no leg builds a tag the guard refused");
        publish.Has("if").Should().BeFalse("an if: (always(), failure()) would let a release publish past a failed leg");
        build.Has("continue-on-error").Should().BeFalse("a leg that may fail and count as success ships a partial release");
        build["strategy"].Map["fail-fast"].Text.Should().Be("false", "every leg reports, so one red build names all broken platforms");
    }

    [Fact]
    public void Every_shipped_rid_builds_on_a_runner_of_its_own_kind_the_same_runner_its_pull_request_leg_uses()
    {
        var release = RunnerPerRid(Job(Release, "build"));
        var pullRequest = RunnerPerRid(Job(CiDaemon, "build-test-publish"));

        release.Keys.Should().Equal(ReleaseFiles.DaemonRids, "the release matrix is the asset contract's RIDs, in order");
        pullRequest.Should().Equal(release, "every shipped binary-and-platform pair runs its tests on a pull request, on the runner it ships from (platform-limits part 2)");
        ReleaseFiles.InstallerRids.Should().BeEquivalentTo(ReleaseFiles.DaemonRids.Where(r => r.StartsWith("linux-", StringComparison.Ordinal)), "install.sh installs exactly the Linux RIDs a release ships");
        release.Where(p => p.Key.StartsWith("linux-", StringComparison.Ordinal)).Should().OnlyContain(
            p => p.Value.StartsWith("ubuntu-24.04", StringComparison.Ordinal),
            "the Linux binaries link the runner's glibc — 24.04's 2.39 is the support floor (plan §15 #15); ubuntu-latest moves");
        Load(Release)["jobs"].Map["build"].Map["runs-on"].Text.Should().Be("${{ matrix.os }}");
    }

    /// <summary>daemon-v0.1.1 (2026-10-06): <c>PostDeployCommandFlows</c> extract the POST_DEPLOY commands with the conventions
    /// checker (the <c>.agents/conventions</c> submodule) and FAIL in CI without it. ci-daemon.yml was given the fetch, the
    /// release workflow — which runs the same Scenarios executable before it packs — was not, and both Linux release legs
    /// failed at their Test step (the publish never ran, the tag's draft stayed empty). Every job that runs the Scenarios
    /// executable fetches the submodule BEFORE that step.</summary>
    [Fact]
    public void Every_job_that_runs_the_scenario_suite_fetches_the_conventions_checker_before_it()
    {
        foreach (var (file, id, job) in SuiteRunners())
        {
            var fetch = StepIndex(job, "git submodule update --init --depth 1 .agents/conventions");
            fetch.Should().BeGreaterThanOrEqualTo(0, $"{file}/{id} runs the Scenarios suite, whose PostDeployCommandFlows read the conventions checker");
            fetch.Should().BeLessThan(StepIndex(job, ScenarioSuite), $"{file}/{id} fetches it before the suite runs");
        }
    }

    /// <summary>The companion (testing.md — a scan that matches nothing passes forever): the scan still finds the jobs known
    /// to run the suite.</summary>
    [Fact]
    public void The_suite_runner_scan_finds_the_jobs_known_to_run_the_scenario_suite()
    {
        SuiteRunners().Select(j => $"{j.File}/{j.Id}").Should().Contain([$"{CiDaemon}/build-test-publish", $"{Release}/build"]);
    }

    private const string ScenarioSuite = "WslCare.Scenarios/bin/";

    private static List<(string File, string Id, YamlMap Job)> SuiteRunners() => [.. AllJobs().Where(j => StepIndex(j.Job, ScenarioSuite) >= 0)];

    [Fact]
    public void The_released_binary_is_smoked_by_the_one_script_every_pull_request_runs()
    {
        File.Exists(Path.Combine(ReleaseFiles.Root, SmokeScript)).Should().BeTrue();
        StepIndex(Job(CiDaemon, "build-test-publish"), SmokeScript).Should().BeGreaterThanOrEqualTo(0, "ci-daemon.yml smokes with the shared script");
        StepIndex(Job(Release, "build"), SmokeScript).Should().BeGreaterThanOrEqualTo(0, "release.yml smokes with the same script (plan §15e #5)");

        // No copy of the smoke's own assertions may live inline in either workflow beside the call.
        foreach (var name in new[] { CiDaemon, Release })
        {
            var text = File.ReadAllText(ReleaseFiles.Workflow(name));
            foreach (var marker in SmokeMarkers())
            {
                text.Should().NotContain(marker, $"{name} must call {SmokeScript}, not repeat what it checks");
            }
        }

        var smoke = File.ReadAllText(Path.Combine(ReleaseFiles.Root, SmokeScript));
        SmokeMarkers().Should().OnlyContain(m => smoke.Contains(m, StringComparison.Ordinal), "the markers are what the script really checks (the scan's known instance)");
    }

    [Fact]
    public void Each_leg_tests_then_publishes_then_smokes_then_packs_then_attests_what_it_packed_then_uploads_it()
    {
        var build = Job(Release, "build");
        var steps = Steps(build);
        int[] order =
        [
            StepIndex(build, "WslCare.Scenarios"),
            StepIndex(build, "dotnet publish"),
            StepIndex(build, SmokeScript),
            StepIndex(build, PackageScript),
            StepIndex(build, "actions/attest-build-provenance@"),
            StepIndex(build, "actions/upload-artifact@"),
        ];

        order.Should().NotContain(-1, "every stage is there").And.BeInAscendingOrder("never ship a binary that did not pass its tests and the smoke");
        var package = steps[order[3]];
        Run(package).Should().Contain("ASSET=").And.Contain("GITHUB_ENV", "the packed archive's path is handed to the attestation and the upload");
        steps[order[4]]["with"].Map["subject-path"].Text.Should().Be("${{ env.ASSET }}", "the attested subject is the archive install.sh downloads (plan §15 #12)");
        steps[order[5]]["with"].Map["path"].Text.Should().Contain("${{ env.ASSET }}\n").And.Contain("${{ env.ASSET }}.sha256", "the archive and its .sha256 travel together");
    }

    /// <summary>The Windows release leg once handed an MSYS path to the actions (E4 review B1): packaging ran on no pull
    /// request, so nothing found it. Every pull-request leg now packs exactly as its release leg does, checks that set,
    /// and opens the printed path in a step that is not bash.</summary>
    [Fact]
    public void Every_pull_request_leg_packs_the_archive_as_the_release_does_and_opens_its_path_outside_bash()
    {
        var job = Job(CiDaemon, "build-test-publish");
        var steps = Steps(job);
        var publish = StepIndex(job, "dotnet publish");
        var package = StepIndex(job, PackageScript);
        var check = steps.ToList().FindIndex(s => Run(s).Contains(VerifyScript, StringComparison.Ordinal) && Run(s).Contains("\"$RID\"", StringComparison.Ordinal));
        var native = NativePathCheck(job);

        new[] { publish, package, check, native }.Should().NotContain(-1).And.BeInAscendingOrder(
            "publish, pack it as release.yml does, check that RID's set, then open the printed path outside bash");
        Run(steps[package]).Should().Contain("\"$RID\"").And.Contain("ASSET=").And.Contain("GITHUB_ENV");
        new[] { package, check, native }.Should().OnlyContain(i => !steps[i].Has("if"), "on EVERY leg — Windows is the leg that needed it");
        var release = Job(Release, "build");
        NativePathCheck(release).Should().BeInRange(StepIndex(release, PackageScript), StepIndex(release, "actions/attest-build-provenance@"),
            "release.yml checks the path before handing it to the attestation");
    }

    /// <summary>The step that opens <c>$ASSET</c> in pwsh — NOT bash: inside a Git Bash step MSYS rewrites a path-looking
    /// variable before any child sees it, so the check would pass the very spelling it exists to catch.</summary>
    private static int NativePathCheck(YamlMap job) =>
        Steps(job).ToList().FindIndex(s => s.Find("shell")?.Text == "pwsh" && Run(s).Contains("Test-Path -LiteralPath", StringComparison.Ordinal) && Run(s).Contains("$env:ASSET", StringComparison.Ordinal));

    [Fact]
    public void The_publish_job_checks_the_set_before_and_after_upload_and_making_it_public_is_its_last_step()
    {
        var publish = Job(Release, "publish");
        var steps = Steps(publish);
        var draftCheck = StepIndex(publish, "isDraft");
        var verifyBuilt = steps.ToList().FindIndex(s => Run(s).Contains(VerifyScript, StringComparison.Ordinal) && Run(s).Contains("from-build", StringComparison.Ordinal));
        var upload = StepIndex(publish, "gh release upload");
        var verifyDraft = steps.ToList().FindIndex(s => Run(s).Contains(VerifyScript, StringComparison.Ordinal) && Run(s).Contains("gh release download", StringComparison.Ordinal));
        var goPublic = StepIndex(publish, "--draft=false");

        new[] { draftCheck, verifyBuilt, upload, verifyDraft, goPublic }.Should().NotContain(-1).And.BeInAscendingOrder(
            "a draft is checked, the built set verified, uploaded, verified again FROM the draft, and only then published (plan §15e #2)");
        goPublic.Should().Be(steps.Count - 1, "nothing runs after the release is public");
        steps.Count(s => Run(s).Contains("--draft=false", StringComparison.Ordinal)).Should().Be(1, "one place makes it public");
    }

    [Fact]
    public void The_guard_runs_first_on_the_whole_history_and_hands_the_version_on()
    {
        var guard = Job(Release, "guard");
        var checkout = Steps(guard).Single(s => Uses(s).StartsWith("actions/checkout@", StringComparison.Ordinal));

        checkout["with"].Map["fetch-depth"].Text.Should().Be("0", "is-the-commit-on-main needs main in the clone");
        Run(Steps(guard)[StepIndex(guard, GuardScript)]).Should().Contain("\"$GITHUB_REF_NAME\" origin/main", "the tag arrives as an argument from the environment, and main is checked");
        guard["outputs"].Map["version"].Text.Should().Be("${{ steps.guard.outputs.version }}");
        Job(Release, "build")["env"].Map["VERSION"].Text.Should().Be("${{ needs.guard.outputs.version }}", "the legs pack the version the guard approved");
    }

    [Fact]
    public void The_proposer_refuses_loudly_without_the_App_secrets_and_hands_the_App_token_to_release_please()
    {
        var job = Job(Proposer, "release-please");
        var steps = Steps(job);
        var check = steps.ToList().FindIndex(s => s.Find("env") is YamlMap env && env.Has("APP_ID_SET") && env.Has("APP_KEY_SET"));
        var mint = StepIndex(job, "actions/create-github-app-token@");
        var propose = StepIndex(job, "googleapis/release-please-action@");

        new[] { check, mint, propose }.Should().NotContain(-1).And.BeInAscendingOrder("say what is missing before the mint fails on an empty input");
        steps[check]["env"].Map["APP_ID_SET"].Text.Should().Be("${{ secrets.RELEASE_PLEASE_APP_ID != '' }}", "only true or false reaches the shell");
        steps[check]["env"].Map["APP_KEY_SET"].Text.Should().Be("${{ secrets.RELEASE_PLEASE_APP_PRIVATE_KEY != '' }}");
        Run(steps[check]).Should().Contain("exit 1", "a missing secret is a red run, never a green one that did nothing");
        steps[mint]["with"].Map.Scalars().Should().Equal(new Dictionary<string, string>
        {
            ["app-id"] = "${{ secrets.RELEASE_PLEASE_APP_ID }}",
            ["private-key"] = "${{ secrets.RELEASE_PLEASE_APP_PRIVATE_KEY }}",
        });
        steps[propose]["with"].Map["token"].Text.Should().Be("${{ steps.token.outputs.token }}", "with GITHUB_TOKEN the tag would start nothing");
        Permissions(job).Should().Equal(new Dictionary<string, string> { ["contents"] = "read" }, "every write is the App token's");
    }

    [Fact]
    public void Every_action_is_pinned_to_a_commit_sha_with_its_version_beside_it()
    {
        var uses = ReleaseFiles.AllWorkflows
            .SelectMany(path => File.ReadAllLines(path).Select((line, i) => (File: Path.GetFileName(path), Line: i + 1, Text: line)))
            .Where(l => UsesLine().IsMatch(l.Text))
            .ToList();

        uses.Should().Contain(l => l.Text.Contains("actions/attest-build-provenance@", StringComparison.Ordinal), "the scan finds the uses: lines (its known instance)");
        uses.Where(l => !PinnedUse().IsMatch(l.Text)).Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}").Should().BeEmpty(
            "every uses: names a 40-hex commit and its version in a comment — a tag can be moved, a commit cannot");
    }

    [Fact]
    public void Every_job_has_a_ceiling_every_checkout_leaves_no_token_behind_and_no_run_pastes_an_expression()
    {
        foreach (var (file, id, job) in AllJobs())
        {
            job.Has("timeout-minutes").Should().BeTrue($"{file}/{id}: every wait has a ceiling");
            foreach (var step in job.Find("steps")?.Items.Select(s => s.Map) ?? [])
            {
                if (Uses(step).StartsWith("actions/checkout@", StringComparison.Ordinal))
                {
                    step["with"].Map["persist-credentials"].Text.Should().Be("false", $"{file}/{id}: the token is not left in the clone's git config");
                }

                Run(step).Should().NotContain("${{", $"{file}/{id}: an expression inside run: is pasted into the shell source — values go through env:");
            }
        }
    }

    [Fact]
    public void The_release_scripts_agree_with_the_installer_on_what_a_version_is()
    {
        ReleaseFiles.DaemonVersionPattern.Should().Be(ReleaseFiles.InstallerVersionPattern, "the guard never admits a tag the installer would refuse to install");
        File.ReadAllText(ReleaseFiles.Script("release-guard.sh")).Should().Contain($"{TagPrefix()}*)", "the guard reads the tag shape release.yml triggers on");
        File.ReadAllText(ShippedFiles.InstallScript).Should().Contain($"releases/download/{TagPrefix()}$VERSION", "install.sh downloads from the tag release.yml publishes");
        File.ReadAllText(ShippedFiles.InstallScript).Should().Contain($"readonly SIGNER_WORKFLOW=\"$REPO/.github/workflows/{Release}\"")
            .And.Contain($"$SIGNER_WORKFLOW@refs/tags/{TagPrefix()}$VERSION",
            "install.sh trusts exactly this workflow run for the tag it triggers on — the identity its attestation carries");
    }

    /// <summary>The tag prefix release.yml triggers on (<c>daemon-v</c>), read from the file.</summary>
    internal static string TagPrefix() => Load(Release)["on"].Map["push"].Map["tags"].Items.Single().Text.TrimEnd('*');

    private static bool SignsWith(IReadOnlyDictionary<string, string> permissions) =>
        permissions.Any(p => p.Key is "id-token" or "attestations" or "*" && p.Value.Contains("write", StringComparison.Ordinal));

    private static bool WritesContents(IReadOnlyDictionary<string, string> permissions) =>
        permissions.Any(p => p.Key is "contents" or "*" && p.Value.Contains("write", StringComparison.Ordinal));

    /// <summary>Strings only the smoke script's checks contain.</summary>
    private static IReadOnlyList<string> SmokeMarkers() =>
        ["config set volumes.anonymousMaxGb 100001", "\"act\" holds these actions", "\"recording\": \"recorded\"", "WSL_CARE_SANDBOX_PRIVILEGED=1", "preview --all --json", "\"verdicts\": [", "productVersion"];

    [GeneratedRegex("""^\s*(-\s+)?uses:\s""")]
    private static partial Regex UsesLine();

    [GeneratedRegex("""^\s*(-\s+)?uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(/[A-Za-z0-9_./-]+)?@[0-9a-f]{40} # v\d+(\.\d+)*\s*$""")]
    private static partial Regex PinnedUse();
}
