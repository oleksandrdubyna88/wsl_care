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

    /// <summary>The workflows whose jobs gate a pull request — the required checks are exactly their jobs.</summary>
    private static readonly string[] GatingWorkflows = ["ci-daemon.yml", "ci-workflows.yml", "family-checks.yml", "pr-title.yml"];

    private static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    private static JsonElement Daemon => Json(ReleaseFiles.ReleasePleaseConfig).GetProperty("packages").GetProperty("src_daemon");

    [Fact]
    public void Release_please_cuts_daemon_tags_on_a_draft_with_the_tag_forced_and_ci_only_commits_excluded()
    {
        var config = Json(ReleaseFiles.ReleasePleaseConfig);

        config.GetProperty("draft").GetBoolean().Should().BeTrue("nothing is public until every RID's asset is on it (plan §15e #2)");
        config.GetProperty("force-tag-creation").GetBoolean().Should().BeTrue("without it a draft cuts no tag and release.yml never runs");
        config.GetProperty("include-component-in-tag").GetBoolean().Should().BeTrue();
        config.GetProperty("exclude-paths").EnumerateArray().Select(e => e.GetString()).Should().Equal(".github");
        Daemon.GetProperty("release-type").GetString().Should().Be("simple", "version.txt is the daemon's one version");

        var tag = Daemon.GetProperty("component").GetString() + config.GetProperty("tag-separator").GetString() + "v";
        tag.Should().Be(ReleaseWorkflowTests.TagPrefix(), "the tag release-please cuts is the tag release.yml starts on");
    }

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
        var jobs = GatingWorkflows
            .SelectMany(name => Jobs(WorkflowYaml.Load(ReleaseFiles.Workflow(name))).Entries.SelectMany(e => CheckNames(e.Key, e.Value.Map)))
            .ToList();

        checks.Select(c => c.GetProperty("context").GetString()).Should().BeEquivalentTo(jobs,
            "a required check that never reports blocks every pull request forever; a gating job left out protects nothing");
        checks.Should().OnlyContain(c => c.GetProperty("integration_id").GetInt64() == GitHubActionsAppId, "only GitHub Actions can satisfy them");
        jobs.Should().Contain(ReleaseFiles.DaemonRids.Select(rid => $"daemon · build · test · aot ({rid})"), "every shipped RID's leg is among them (the derived names expand the matrix)");
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
