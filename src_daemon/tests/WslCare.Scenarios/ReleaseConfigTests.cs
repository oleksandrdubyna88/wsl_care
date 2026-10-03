using System.Text.Json;

using FluentAssertions;

using static WslCare.Scenarios.WorkflowShape;

namespace WslCare.Scenarios;

/// <summary>
/// The release-please files and the owner-applied rulesets (E4.S2), held against the workflows they must agree with:
/// the tag release-please cuts is the tag release.yml starts on and the tag ruleset protects; the first release is
/// 0.1.0 exactly; every required check is a job a pull request really runs. GitHub reads none of the ruleset files —
/// they are the PUT bodies docs/repo-settings.md applies — so this is the only thing that keeps them true.
/// </summary>
public sealed class ReleaseConfigTests
{
    /// <summary>The GitHub Actions app: a required check pinned to it cannot be satisfied by a status any other actor posts.</summary>
    private const long GitHubActionsAppId = 15368;

    /// <summary>Workflows a pull request triggers that deliberately gate NOTHING, each with its reason: SonarCloud passes
    /// while skipping without a token, CodeRabbit is a third-party free tier that runs out (docs/repo-settings.md, step 4).
    /// Every OTHER workflow a pull request triggers gates it, so its jobs must be required checks.</summary>
    private static readonly string[] NonGatingWorkflows = ["sonarcloud.yml", "coderabbit-review.yml"];

    /// <summary>The workflows whose jobs gate a pull request, DERIVED: every workflow triggered by <c>pull_request</c> or
    /// <c>pull_request_target</c>, minus <see cref="NonGatingWorkflows"/> (E4 review B6: a hand-typed list would not notice
    /// the next workflow).</summary>
    private static IReadOnlyList<string> GatingWorkflows(IEnumerable<(string Name, YamlMap Workflow)> workflows) =>
        [.. workflows.Where(w => TriggeredByPullRequests(w.Workflow) && !NonGatingWorkflows.Contains(w.Name)).Select(w => w.Name)];

    private static bool TriggeredByPullRequests(YamlMap workflow) => workflow["on"] switch
    {
        YamlMap triggers => triggers.Has("pull_request") || triggers.Has("pull_request_target"),
        YamlSequence triggers => triggers.Values.Any(t => t.Text is "pull_request" or "pull_request_target"),
        var trigger => trigger.Text is "pull_request" or "pull_request_target",
    };

    private static IEnumerable<(string Name, YamlMap Workflow)> RepositoryWorkflows() =>
        ReleaseFiles.AllWorkflows.Select(path => (Path.GetFileName(path), WorkflowYaml.Load(path)));

    private static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    private static JsonElement Daemon => Json(ReleaseFiles.ReleasePleaseConfig).GetProperty("packages").GetProperty("src_daemon");

    [Fact]
    public void Release_please_cuts_daemon_tags_on_a_draft_with_the_tag_forced()
    {
        var config = Json(ReleaseFiles.ReleasePleaseConfig);

        config.GetProperty("draft").GetBoolean().Should().BeTrue("nothing is public until every RID's asset is on it (plan §15e #2)");
        config.GetProperty("force-tag-creation").GetBoolean().Should().BeTrue("without it a draft cuts no tag and release.yml never runs");
        config.GetProperty("include-component-in-tag").GetBoolean().Should().BeTrue();
        Daemon.GetProperty("release-type").GetString().Should().Be("simple", "version.txt is the daemon's one version");

        var tag = Daemon.GetProperty("component").GetString() + config.GetProperty("tag-separator").GetString() + "v";
        tag.Should().Be(ReleaseWorkflowTests.TagPrefix(), "the tag release-please cuts is the tag release.yml starts on");
    }

    /// <summary>
    /// What keeps a CI-only commit out of a daemon release is the PACKAGE PATH, not an exclude list (E4 review B3):
    /// release-please hands a package only the commits that touch a file under <c>&lt;path&gt;/</c> (its CommitSplit), so a
    /// commit touching only <c>.github/</c> or root files never reaches <c>src_daemon</c> — and an <c>exclude-paths</c> entry
    /// outside every package path can never remove a commit the split already kept out. Such a key is inert, and a reader
    /// would trust it; it may not appear.
    /// </summary>
    [Fact]
    public void A_commit_touching_only_github_or_root_files_reaches_no_package_and_no_inert_exclude_list_pretends_otherwise()
    {
        var config = Json(ReleaseFiles.ReleasePleaseConfig);
        var paths = config.GetProperty("packages").EnumerateObject().Select(p => p.Name).ToList();

        paths.Should().NotContain(["", "."], "a root package would take every commit");
        paths.Should().OnlyContain(path => Directory.Exists(Path.Combine(ReleaseFiles.Root, path)), "each package path is a folder of this repository");
        PackagesTouched(paths, [".github/workflows/release.yml", ".github/scripts/smoke-daemon.sh"]).Should().BeEmpty("a workflow change is not a daemon release");
        PackagesTouched(paths, ["global.json", "Directory.Packages.props", "README.md"]).Should().BeEmpty("root build files ship with the next daemon change");
        PackagesTouched(paths, ["src_daemon/version.txt"]).Should().Equal(["src_daemon"], "the positive: a daemon file reaches the daemon package");
        PackagesTouched(paths, [".github/workflows/ci-daemon.yml", "src_daemon/src/WslCare.Cli/Program.cs"]).Should().Equal(["src_daemon"], "a commit touching both is released");

        config.TryGetProperty("exclude-paths", out _).Should().BeFalse("inert: no exclude path can drop a commit the package path never received");
        config.GetProperty("packages").EnumerateObject().Where(p => p.Value.TryGetProperty("exclude-paths", out _)).Should().BeEmpty("the same inside a package");
    }

    /// <summary>Before 1.0.0 a breaking change (<c>feat!:</c>, <c>BREAKING CHANGE:</c>) bumps the MINOR — release-please's
    /// default would make the daemon 1.0.0 on the first breaking commit — and a <c>feat:</c> stays a minor too (the epics
    /// each end in a minor release; a patch per feature would hide them).</summary>
    [Fact]
    public void Before_1_0_a_breaking_change_bumps_the_minor_and_a_feature_stays_a_minor()
    {
        var config = Json(ReleaseFiles.ReleasePleaseConfig);

        config.TryGetProperty("bump-minor-pre-major", out var minor).Should().BeTrue("without it release-please's default makes the first feat! 1.0.0");
        minor.GetBoolean().Should().BeTrue("feat! at 0.x must not jump to 1.0.0");
        config.TryGetProperty("bump-patch-for-minor-pre-major", out var patch).Should().BeTrue("the choice is written down, not left to a default");
        patch.GetBoolean().Should().BeFalse("feat at 0.x is a minor, as every epic's release is");
    }

    /// <summary>The packages a commit touching <paramref name="files"/> is handed to: release-please's CommitSplit rule —
    /// a file belongs to a package when it starts with <c>&lt;path&gt;/</c>.</summary>
    private static IReadOnlyList<string> PackagesTouched(IReadOnlyList<string> packagePaths, IReadOnlyList<string> files) =>
        [.. packagePaths.Where(path => files.Any(file => file.StartsWith(path + "/", StringComparison.Ordinal)))];

    [Fact]
    public void The_manifest_and_version_txt_say_the_same_version_and_an_unreleased_daemon_starts_at_0_1_0()
    {
        var manifest = Json(ReleaseFiles.ReleasePleaseManifest).GetProperty("src_daemon").GetString();

        manifest.Should().Be(ReleaseFiles.TrimmedVersion, "release-please bumps both in one pull request; a hand edit to one is a release that lies");
        if (manifest == "0.0.0")
        {
            // release-please treats 0.0.0 as never released and then uses initial-version; any other manifest value would
            // be taken as ALREADY released and bumped past (release-please-config.json, $bootstrap).
            Daemon.GetProperty("initial-version").GetString().Should().Be("0.1.0", "the first daemon release is 0.1.0 exactly (plan §16 E4)");
        }
    }

    [Fact]
    public void The_tag_ruleset_protects_exactly_the_release_tags_and_lets_only_the_release_App_through()
    {
        var ruleset = Json(ReleaseFiles.Ruleset("tags-daemon.json"));

        ruleset.GetProperty("target").GetString().Should().Be("tag", "branch protection does not govern tags");
        ruleset.GetProperty("enforcement").GetString().Should().Be("active");
        ruleset.GetProperty("conditions").GetProperty("ref_name").GetProperty("include").EnumerateArray().Select(e => e.GetString())
            .Should().Equal($"refs/tags/{ReleaseWorkflowTests.TagPrefix()}*");
        ruleset.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("type").GetString())
            .Should().BeEquivalentTo(["creation", "update", "deletion"], "creation alone would leave a protected tag that can simply be moved");
        var bypass = ruleset.GetProperty("bypass_actors").EnumerateArray().Should().ContainSingle("the App and nobody else").Subject;
        bypass.GetProperty("actor_type").GetString().Should().Be("Integration");
        bypass.GetProperty("bypass_mode").GetString().Should().Be("always");
    }

    [Fact]
    public void Every_required_check_is_a_job_a_pull_request_runs_and_every_gating_job_is_required()
    {
        var checks = Json(ReleaseFiles.Ruleset("branch-main.json")).GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "required_status_checks")
            .GetProperty("parameters").GetProperty("required_status_checks").EnumerateArray().ToList();
        var gating = GatingWorkflows(RepositoryWorkflows());
        gating.Should().Contain(["ci-daemon.yml", "pr-title.yml"], "the derivation finds the known gating workflows (its known instances)");
        var jobs = gating
            .SelectMany(name => Jobs(WorkflowYaml.Load(ReleaseFiles.Workflow(name))).Entries.SelectMany(e => CheckNames(e.Key, e.Value.Map)))
            .ToList();

        checks.Select(c => c.GetProperty("context").GetString()).Should().BeEquivalentTo(jobs,
            "a required check that never reports blocks every pull request forever; a gating job left out protects nothing");
        checks.Should().OnlyContain(c => c.GetProperty("integration_id").GetInt64() == GitHubActionsAppId, "only GitHub Actions can satisfy them");
        jobs.Should().Contain(ReleaseFiles.DaemonRids.Select(rid => $"daemon · build · test · aot ({rid})"), "every shipped RID's leg is among them (the derived names expand the matrix)");
    }

    [Fact]
    public void A_new_workflow_a_pull_request_triggers_counts_as_gating_unless_it_is_named_non_gating()
    {
        var planted = WorkflowYaml.Parse("on:\n  pull_request:\n    branches: [main]\njobs:\n  lint:\n    name: new · lint\n    runs-on: x\n", "new-check.yml").Map;
        var target = WorkflowYaml.Parse("on: pull_request_target\njobs:\n  a:\n    runs-on: x\n", "target.yml").Map;
        var pushOnly = WorkflowYaml.Parse("on:\n  push:\n    branches: [main]\njobs:\n  a:\n    runs-on: x\n", "push.yml").Map;

        GatingWorkflows([.. RepositoryWorkflows(), ("new-check.yml", planted), ("target.yml", target), ("push.yml", pushOnly)])
            .Should().Contain(["new-check.yml", "target.yml"], "a workflow added for pull requests gates them — its jobs must become required checks")
            .And.NotContain(["push.yml", "sonarcloud.yml", "coderabbit-review.yml"]);
    }

    [Fact]
    public void The_main_ruleset_requires_a_pull_request_linear_history_and_admits_no_bypass()
    {
        var ruleset = Json(ReleaseFiles.Ruleset("branch-main.json"));
        var rules = ruleset.GetProperty("rules").EnumerateArray().ToDictionary(r => r.GetProperty("type").GetString()!, r => r);

        ruleset.GetProperty("target").GetString().Should().Be("branch");
        ruleset.GetProperty("bypass_actors").GetArrayLength().Should().Be(0, "admins included, like the family's enforce_admins");
        rules.Keys.Should().Contain(["deletion", "non_fast_forward", "required_linear_history", "pull_request", "required_status_checks"]);
        rules["pull_request"].GetProperty("parameters").GetProperty("required_review_thread_resolution").GetBoolean().Should().BeTrue();
        rules["required_status_checks"].GetProperty("parameters").GetProperty("strict_required_status_checks_policy").GetBoolean().Should().BeTrue();
    }
}
