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
    private const string Handshake = "src_vs_code/src/client/handshake.ts";

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
            ["build"] = new Dictionary<string, string> { ["contents"] = "read", ["id-token"] = "write", ["attestations"] = "write" },
            ["github-draft"] = write,
            ["publish-marketplace"] = read,
            ["github-public"] = write,
        };

        Jobs(Load()).Keys.Should().Equal(expected.Keys, "the five jobs, in their order");
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
        Needs(Job("github-draft")).Should().BeEquivalentTo(["guard", "build"]);
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
        var wait = steps.ToList().FindIndex(s => Run(s).Contains("vsce show", StringComparison.Ordinal) && Run(s).Contains("seq", StringComparison.Ordinal));

        new[] { served, publish, wait }.Should().NotContain(-1).And.BeInAscendingOrder("ask, publish only when not served, then wait until served");
        Run(steps[served]).Should().Contain("vsce show").And.Contain("served=", "the skip is decided by what the Marketplace serves");
        steps[publish]["if"].Text.Should().Be("steps.served.outputs.served != 'true'", "a re-run after a publish publishes nothing");
        Run(steps[publish]).Should().Contain("--packagePath \"from-build/wsl-care-$VERSION.vsix\"", "the very file the build attested");
        StepIndex(job, AssetsScript).Should().BeInRange(0, served, "the downloaded file is checked against its .sha256 before anything uses it");
        Steps(job).Single(s => Uses(s).StartsWith("actions/download-artifact@", StringComparison.Ordinal))["with"].Map["name"].Text
            .Should().Be(BuildArtifactName(), "the artifact the build uploaded after attesting it");
        Run(steps[StepIndex(job, "npm ci")]).Should().Contain("--ignore-scripts", "no dependency's install script runs beside the Marketplace secret");
    }

    [Fact]
    public void Every_github_step_is_rerunnable_and_published_bytes_are_never_replaced()
    {
        var draft = Job("github-draft");
        var upload = Run(Steps(draft)[StepIndex(draft, "gh release upload")]);
        upload.Should().Contain("isDraft").And.Contain("--clobber").And.Contain("already public", "uploads onto a draft; a re-run on a public release uploads nothing");
        var readBack = Steps(draft).ToList().FindIndex(s => Run(s).Contains("gh release download", StringComparison.Ordinal) && Run(s).Contains(AssetsScript, StringComparison.Ordinal) && Run(s).Contains("cmp ", StringComparison.Ordinal));
        readBack.Should().BeGreaterThan(StepIndex(draft, "gh release upload"), "the release is read back and compared byte for byte with the build's file");

        var pub = Job("github-public");
        var steps = Steps(pub);
        var check = steps.ToList().FindIndex(s => Run(s).Contains(AssetsScript, StringComparison.Ordinal));
        var goPublic = StepIndex(pub, "--draft=false");
        new[] { check, goPublic }.Should().NotContain(-1).And.BeInAscendingOrder();
        Run(steps[goPublic]).Should().Contain("already public", "a re-run on a public release changes nothing");
        goPublic.Should().Be(steps.Count - 1, "nothing runs after the release is public");
        ReleaseFiles.AllWorkflows.Sum(p => File.ReadAllText(p).Split("--draft=false").Length - 1).Should().Be(2, "one place per release workflow makes a release public");
    }

    [Fact]
    public void The_build_packages_once_checks_that_file_then_attests_and_uploads_it()
    {
        var build = Job("build");
        var steps = Steps(build);
        int[] order =
        [
            StepIndex(build, "npm test"),
            StepIndex(build, "npm run package"),
            StepIndex(build, "scripts/check-vsix.mjs"),
            StepIndex(build, AssetsScript.Replace(".github", "../.github", StringComparison.Ordinal)),
            StepIndex(build, "actions/attest-build-provenance@"),
            StepIndex(build, "actions/upload-artifact@"),
        ];

        order.Should().NotContain(-1).And.BeInAscendingOrder("test, package, check the artefact, check the set, attest it, upload it");
        steps.Count(s => Run(s).Contains("npm run package", StringComparison.Ordinal) || Run(s).Contains("vsce package", StringComparison.Ordinal)).Should().Be(1, "vsce package ONCE");
        Run(steps[order[2]]).Should().Contain("--release", "a release refuses the placeholder publisher");
        steps[order[4]]["with"].Map["subject-path"].Text.Should().Be("release-extension/wsl-care-${{ needs.guard.outputs.version }}.vsix", "the attested subject is the packaged file");
        steps[order[5]]["with"].Map["path"].Text.Should().Be("release-extension/", "the .vsix and its .sha256 travel together");
        build["env"].Map["VERSION"].Text.Should().Be("${{ needs.guard.outputs.version }}", "the build packs the version the guard approved");
        File.ReadAllText(ReleaseFiles.Workflow("ci-extension.yml")).Should().Contain("npm run package").And.Contain("npm run check:vsix", "every pull request packages and checks the same way (plan §15g M8)");
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
        guard["outputs"].Map.Keys.Should().Contain(["version", "publisher"]);

        var script = File.ReadAllText(Path.Combine(ReleaseFiles.Root, GuardScript));
        script.Should().Contain("MIN_DAEMON_FOR_RENDER").And.Contain("releases/tags/$daemon_tag").And.Contain("POST_DEPLOY.md").And.Contain("publisher-tbd")
            .And.Contain($"{TagPrefix()}*)", "the guard reads the tag shape this workflow triggers on");
    }

    /// <summary>The guard reads <c>MIN_DAEMON_FOR_RENDER</c> with a line pattern; the TypeScript must keep that exact
    /// line, or the guard would refuse every release — held here rather than discovered on a release day.</summary>
    [Fact]
    public void The_minimum_daemon_line_the_guard_reads_is_in_the_extension_exactly_once()
    {
        var lines = File.ReadAllLines(Path.Combine(ReleaseFiles.Root, Handshake)).Where(l => MinDaemonLine().IsMatch(l)).ToList();

        lines.Should().ContainSingle("export const MIN_DAEMON_FOR_RENDER = 'x.y.z'; — the line release-extension-guard.sh parses");
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

    [GeneratedRegex("""^  [a-z][a-z0-9-]*:\s*$""")]
    private static partial Regex JobKeyLine();

    [GeneratedRegex("""^export const MIN_DAEMON_FOR_RENDER = '[0-9]+\.[0-9]+\.[0-9]+';$""")]
    private static partial Regex MinDaemonLine();
}
