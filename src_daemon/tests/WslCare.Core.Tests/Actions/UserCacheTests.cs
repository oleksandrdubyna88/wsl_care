using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.UserCaches;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The user-scoped cleanups of E3.S2 — A8 (npm), A17 (pnpm / uv / pip), A12 (Playwright browsers + NuGet http-cache), A14
/// (editor server builds) — over a sandbox whose target user is <c>me</c>: every tool runs ONLY as that user through
/// <c>runuser</c> with a clean environment, a tool that is not installed is a SKIP, every folder is the TARGET user's, freed
/// bytes are measured, and a delete under a protected root is refused by the deletion policy.
/// </summary>
public sealed class UserCacheTests : IDisposable
{
    private readonly UserWorld _world = new("user-caches");

    public void Dispose() => _world.Dispose();

    private static void Remove(string path)
    {
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }

    // ---------- A8 ----------

    [Fact]
    public async Task A8_without_npm_in_the_users_bin_folders_is_a_skip_with_the_reason_not_an_error()
    {
        var action = new NpmCacheClean();
        var context = _world.Context();

        var preview = await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None);

        preview.Skip.Should().Contain("npm is not installed for me");
        _world.Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A8_runs_npm_as_the_target_user_by_its_full_path_with_a_clean_environment_and_measures_what_left_the_cache()
    {
        var a = _world.File("/home/me/.npm/_cacache/a", 3000);
        _world.File("/home/me/.npm/_cacache/b", 1000);
        _world.Tool("npm", "/usr/bin");
        Asks("npm", ["config", "get", "cache"], "/home/me/.npm");
        _world.Runner.ScriptEffect(argv => TargetUserArgv.Parse(argv) is { ExecutableName: "npm", Arguments: ["cache", "clean", "--force"] }, _ =>
        {
            Remove(a);
            return RecordingCommandRunner.Exited(0);
        });
        var action = new NpmCacheClean();
        var context = _world.Context();
        var commands = _world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue(run.Failure);
        run.FreedBytes.Should().Be(3000, "the size of ~/.npm before minus after, not an estimate");
        _world.Wrapped.Should().Equal("npm config get cache", "npm cache clean --force");
        var request = _world.Runner.Requests[^1];
        request.Argv.Take(4).Should().Equal("runuser", "-u", "me", "--");
        request.Argv.Skip(5).Should().Equal("cache", "clean", "--force");
        request.Environment.Should().BeOfType<CommandEnvironment.Clean>().Which.Variables["HOME"].Should().Be("/home/me");
        Core.Processes.Policy.CommandPolicy.Product.Review(request).IsAllowed.Should().BeTrue();
    }

    // ---------- review S4: a tool's own configuration can move its cache into an agent folder ----------

    private UserCacheTests Asks(string tool, IReadOnlyList<string> arguments, string answer)
    {
        _world.Runner.Script(argv => TargetUserArgv.Parse(argv) is { } w && w.ExecutableName == tool && w.Arguments.SequenceEqual(arguments), RecordingCommandRunner.Exited(0, answer + "\n", string.Empty));
        return this;
    }

    [Fact]
    public async Task S4_A8_refuses_when_npms_own_configuration_puts_its_cache_in_an_agent_folder()
    {
        _world.File("/home/me/.claude/npm-cache/_cacache/x", 10);
        _world.Tool("npm", "/usr/bin");
        Asks("npm", ["config", "get", "cache"], "/home/me/.claude/npm-cache");
        var action = new NpmCacheClean();
        var context = _world.Context();

        var preview = await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None);

        preview.Refusal.Should().Contain("/home/me/.claude/npm-cache").And.Contain("AI agent folder");
    }

    [Fact]
    public async Task S4_A17_does_not_run_a_tool_whose_configured_cache_is_in_an_agent_folder()
    {
        _world.File("/home/me/.codex/pip/wheels/y", 700);
        _world.Tool("pip3", "/usr/bin");
        Asks("pip3", ["cache", "dir"], "/home/me/.codex/pip");
        var action = new ToolCacheTrims();
        var context = _world.Context();
        var commands = _world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        _world.Wrapped.Should().NotContain("pip3 cache purge");
        run.Notes.Should().Contain(n => n.Contains("/home/me/.codex/pip", StringComparison.Ordinal) && n.Contains("AI agent folder", StringComparison.Ordinal));
    }

    // ---------- A17 ----------

    [Fact]
    public async Task A17_with_none_of_its_tools_installed_is_a_skip_and_runs_nothing()
    {
        var action = new ToolCacheTrims();
        var context = _world.Context();

        var preview = await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None);

        preview.Skip.Should().Contain("none of pnpm, uv, pip is installed");
        preview.What.Should().Contain("cargo sweep").And.Contain("Gradle");
    }

    [Fact]
    public async Task A17_runs_each_installed_tools_own_trim_as_the_user_skips_the_missing_ones_and_takes_pip3_only_without_pip()
    {
        var store = _world.File("/home/me/.local/share/pnpm/store/v3/x", 5000);
        var wheel = _world.File("/home/me/.cache/pip/wheels/y", 700);
        _world.Tool("pnpm").Tool("pip3", "/usr/bin");
        Asks("pnpm", ["store", "path"], "/home/me/.local/share/pnpm/store/v3");
        Asks("pip3", ["cache", "dir"], "/home/me/.cache/pip");
        _world.Runner.ScriptEffect(argv => TargetUserArgv.Parse(argv) is { ExecutableName: "pnpm", Arguments: ["store", "prune"] }, _ =>
        {
            Remove(store);
            return RecordingCommandRunner.Exited(0);
        });
        _world.Runner.ScriptEffect(argv => TargetUserArgv.Parse(argv) is { ExecutableName: "pip3", Arguments: ["cache", "purge"] }, _ =>
        {
            Remove(wheel);
            return RecordingCommandRunner.Exited(0);
        });
        var action = new ToolCacheTrims();
        var context = _world.Context();
        var commands = _world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        _world.Wrapped.Should().Equal("pnpm store path", "pnpm store prune", "pip3 cache dir", "pip3 cache purge");
        run.FreedBytes.Should().Be(5700);
        run.Notes.Should().ContainSingle().Which.Should().Contain("cargo sweep");
    }

    // ---------- A12 ----------

    private void Playwright()
    {
        foreach (var browser in new[] { "chromium-1228", "chromium-1243", "chromium_headless_shell-1228", "ffmpeg-1011" })
        {
            _world.File($"/home/me/.cache/ms-playwright/{browser}/bin", 1000);
        }

        _world.Sandbox.Write("/home/me/.cache/ms-playwright/.links/aaaa", "/home/me/git/web/node_modules/playwright-core\n");
        _world.Sandbox.Write("/home/me/.cache/ms-playwright/.links/bbbb", "/tmp/gone/node_modules/playwright-core\n");
        _world.Sandbox.Write("/home/me/git/web/node_modules/playwright-core/browsers.json",
            """{ "browsers": [ { "name": "chromium", "revision": "1228" }, { "name": "chromium-headless-shell", "revision": "1228" }, { "name": "ffmpeg", "revision": "1011" } ] }""");
    }

    [Fact]
    public async Task A12_removes_only_the_browsers_no_live_projects_browsers_json_references_and_a_stale_link_references_nothing()
    {
        Playwright();
        var action = new BrowserAndHttpCaches();
        var context = _world.Context(RunTrigger.Manual);
        var commands = _world.Commands(action, context);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Targets.Select(t => t.Name).Should().Equal("chromium-1243");
        run.Removed.Select(r => r.Name).Should().Equal("chromium-1243");
        run.FreedBytes.Should().Be(1000);
        Directory.Exists(_world.Sandbox.Paths.DistroPath("/home/me/.cache/ms-playwright/chromium-1243")).Should().BeFalse();
        Directory.Exists(_world.Sandbox.Paths.DistroPath("/home/me/.cache/ms-playwright/chromium-1228")).Should().BeTrue();
        action.Trigger(preview, context.Config).Fired.Should().BeFalse("A12 is a button only");
    }

    [Theory]
    [InlineData("no-links")]
    [InlineData("no-browsers-json")]
    [InlineData("install-running")]
    [InlineData("no-process-table")]
    public async Task A12_refuses_the_playwright_part_whole_whenever_what_is_referenced_cannot_be_told(string doubt)
    {
        Playwright();
        switch (doubt)
        {
            case "no-links":
                Directory.Delete(_world.Sandbox.Paths.DistroPath("/home/me/.cache/ms-playwright/.links"), recursive: true);
                break;
            case "no-browsers-json":
                System.IO.File.Delete(_world.Sandbox.Paths.DistroPath("/home/me/git/web/node_modules/playwright-core/browsers.json"));
                break;
            case "install-running":
                _world.Sandbox.Write("/home/me/.cache/ms-playwright/__dirlock", string.Empty);
                break;
        }

        var action = new BrowserAndHttpCaches();
        var context = doubt == "no-process-table" ? _world.Context() with { Processes = _ => Core.Collectors.Reading.Missing<Core.Collectors.ProcessSnapshot>("no table") } : _world.Context();

        var preview = await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None);

        preview.Targets.Should().BeEmpty();
        preview.Skip.Should().Contain("the Playwright part refuses");
    }

    [Fact]
    public async Task A12_keeps_a_browser_a_running_process_names()
    {
        Playwright();
        _world.Processes.Add(UserWorld.Process(4242, "/home/me/.cache/ms-playwright/chromium-1243/chrome-linux/chrome --headless"));
        var action = new BrowserAndHttpCaches();
        var context = _world.Context();

        (await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None)).Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task A12_clears_the_nuget_http_cache_with_dotnets_own_command_as_the_user()
    {
        var cached = _world.File("/home/me/.local/share/NuGet/http-cache/abc/x.nupkg", 2500);
        _world.Tool("dotnet", "/usr/bin").OnTool("dotnet", () => Remove(cached));
        var action = new BrowserAndHttpCaches();
        var context = _world.Context(RunTrigger.Manual);
        var commands = _world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        _world.Wrapped.Should().Equal("dotnet nuget locals http-cache --clear");
        run.FreedBytes.Should().Be(2500);
    }

    // ---------- A14 ----------

    private const string C1 = "1111111111111111111111111111111111111111";
    private const string C2 = "2222222222222222222222222222222222222222";
    private const string C3 = "3333333333333333333333333333333333333333";
    private const string C4 = "4444444444444444444444444444444444444444";

    private string Build(string relative, int daysAgo)
    {
        _world.File($"/home/me/.vscode-server/{relative}/node", 4000);
        var path = _world.Sandbox.Paths.DistroPath($"/home/me/.vscode-server/{relative}");
        Directory.SetLastWriteTimeUtc(path, FixedTimeProvider.DefaultNow.AddDays(-daysAgo).UtcDateTime);
        return path;
    }

    [Fact]
    public async Task A14_keeps_the_newest_two_builds_and_every_build_in_use_and_removes_the_rest_with_the_obsolete_extensions()
    {
        Build($"bin/{C1}", 1);
        Build($"cli/servers/Stable-{C2}", 2);
        var old = Build($"bin/{C3}", 30);
        Build($"bin/{C4}", 40);
        _world.File("/home/me/.vscode-server/extensions/pub.ext-1.0.0/package.json", 600);
        _world.File("/home/me/.vscode-server/extensions/pub.ext-2.0.0/package.json", 600);
        _world.Sandbox.Write("/home/me/.vscode-server/extensions/.obsolete", """{ "pub.ext-1.0.0": true, "../../git": true, "pub.ext-2.0.0": false }""");
        _world.Processes.Add(UserWorld.Process(77, $"/home/me/.vscode-server/bin/{C4}/node out/server-main.js"));
        var action = new EditorServerCleanup();
        var context = _world.Context();
        var commands = _world.Commands(action, context);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Targets.Select(t => t.Name).Should().BeEquivalentTo([$".vscode-server/{C3}", ".vscode-server/extensions/pub.ext-1.0.0"], "C1 and C2 are the newest two, C4 is in use, the traversal key is not a plain name");
        run.Removed.Should().HaveCount(2);
        run.FreedBytes.Should().Be(4600);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(_world.Sandbox.Paths.DistroPath($"/home/me/.vscode-server/bin/{C4}")).Should().BeTrue();
        action.Trigger(preview, context.Config).Fired.Should().BeTrue();
    }

    [Fact]
    public async Task A14_a_build_folder_that_leads_into_git_is_refused_by_the_deletion_policy_and_the_repository_is_untouched()
    {
        // The two kept builds are dated in the future: the link is made now, and must be the OLD one.
        Build($"bin/{C1}", -2000);
        Build($"bin/{C2}", -1999);
        var repoFile = _world.File("/home/me/git/repo/src/main.c", 123);
        var link = Path.GetFullPath(_world.Sandbox.Paths.DistroPath($"/home/me/.vscode-server/bin/{C3}"));
        Assert.SkipUnless(DirectoryLinks.TryCreate(link, Path.GetFullPath(_world.Sandbox.Paths.DistroPath("/home/me/git/repo"))), "this account can create neither a symlink nor a junction");
        var action = new EditorServerCleanup();
        var context = _world.Context();
        var commands = _world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("repositories folder");
        run.Failure.Should().Contain("refused");
        System.IO.File.Exists(repoFile).Should().BeTrue("nothing under ~/git is ever deleted");
    }
}
