using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The E7.S1/S2 review round (plan §15q *E7.S1/S2 review round*): two own reviews (correctness; safety/security), every finding
/// accepted. Each fact here was seen red for its real symptom before the fix.
/// </summary>
public sealed class AgentsReviewRoundTests : IDisposable
{
    /// <summary>The PATH a process started by <c>wsl.exe --exec</c> gets (measured 2026-10-05, anonymised): system folders, WSL's
    /// library folder, then Windows folders on drvfs — and NO <c>~/.local/bin</c>, NO nvm bin.</summary>
    private const string MeasuredPathShape = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/games:/usr/local/games:/usr/lib/wsl/lib:/mnt/c/Program Files/Git/cmd:/mnt/c/Users/me/AppData/Roaming/npm:/mnt/c/WINDOWS/system32";

    private readonly LinuxSandbox _sandbox = new("agents-review");

    public void Dispose() => _sandbox.Dispose();

    /// <summary>The measured shape with EVERY entry inside the sandbox — this machine's own /usr/bin is never searched by a test.</summary>
    private string PathShape() => string.Join(':', MeasuredPathShape.Split(':').Select(_sandbox.Paths.DistroPath));

    private AgentPresence Of(string id, string? pathVariable = null) =>
        AgentDiscovery.Discover(_sandbox.Paths, _sandbox.Files, pathVariable ?? PathShape(), asRoot: false).Single(p => p.Entry.Id == id);

    private string Executable(string distroPath, string content = "#!/bin/false")
    {
        var path = _sandbox.Write(distroPath, content);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static void SkipUnlessLinux(string why) => Assert.SkipUnless(OperatingSystem.IsLinux(), why);

    // ---------- E7.S2b/S2c review round, A-L3: ONE ceiling over the whole lookup ----------

    /// <summary>A file system whose device question hangs: a PATH entry on a 9p share the host stopped serving.</summary>
    private sealed class SlowDevices(IFileSystem inner, TimeSpan delay) : DelegatingFileSystem(inner)
    {
        public override (uint Major, uint Minor)? DeviceOf(string path)
        {
            Thread.Sleep(delay);
            return base.DeviceOf(path);
        }
    }

    [Fact]
    public void A_hanging_device_question_while_choosing_the_folders_is_inside_the_lookup_s_one_ceiling()
    {
        var machine = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content("""{ "agents": { "lookupCeilingSeconds": 1 } }"""u8.ToArray())),
        ]).Config;
        var started = System.Diagnostics.Stopwatch.StartNew();

        using (Tuning.Use(machine))
        {
            AgentDiscovery.Discover(_sandbox.Paths, new SlowDevices(_sandbox.Files, TimeSpan.FromSeconds(4)), PathShape(), asRoot: false).Should().NotBeEmpty();
        }

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "review A-L3: the folder choice (a device stat per PATH entry) runs under the same 1 s ceiling as the lookups");
    }

    // ---------- R1: discovery under wsl.exe --exec ----------

    [Fact]
    public void R1_a_cli_in_the_users_local_bin_is_found_although_the_exec_PATH_lacks_it()
    {
        SkipUnlessLinux("the distro's PATH rule (an execute bit) is Linux's");
        Executable("/home/me/.local/bin/claude");

        Of("claude-code").DetectedBy.Should().Contain(AgentDiscovery.Binary, "~/.local/bin is one of the fixed folders, whatever PATH holds");
    }

    [Fact]
    public void R1_a_PATH_folder_under_the_automount_root_is_never_searched()
    {
        SkipUnlessLinux("the distro's PATH rule (an execute bit) is Linux's");
        Executable("/mnt/c/Users/me/AppData/Roaming/npm/codex", "#!/bin/sh\nexec node codex.js\n");

        var codex = Of("codex");

        codex.DetectedBy.Should().NotContain(AgentDiscovery.Binary, $"a Windows npm shim on drvfs is not the distro's binary (found: {string.Join(", ", codex.Binaries.Select(b => b.Path))})");
    }

    // ---------- R3: the version of the binary that was found ----------

    [Fact]
    public void R3_the_version_comes_from_the_found_binarys_package_not_from_the_oldest_nvm()
    {
        SkipUnlessLinux("a file link needs no privilege on Linux");
        _sandbox.Write("/home/me/.nvm/alias/default", "22\n");
        foreach (var (node, version) in new[] { ("v18.20.0", "0.30.0"), ("v22.11.0", "0.44.0") })
        {
            _sandbox.Write($"/home/me/.nvm/versions/node/{node}/lib/node_modules/@openai/codex/package.json", $$"""{ "name": "@openai/codex", "version": "{{version}}" }""");
            var script = Executable($"/home/me/.nvm/versions/node/{node}/lib/node_modules/@openai/codex/bin/codex.js");
            var bin = _sandbox.Paths.DistroPath($"/home/me/.nvm/versions/node/{node}/bin");
            Directory.CreateDirectory(bin);
            File.CreateSymbolicLink(Path.Combine(bin, "codex"), script);
        }

        var codex = Of("codex");

        codex.Version.Should().Be("0.44.0", "the binary found is v22's (nvm's default), so is its package");
        codex.VersionReason.Should().Contain("v22.11.0").And.Contain("package.json", "the version names where it was read");
    }

    [Fact]
    public void R3_a_native_install_two_links_away_wins_over_a_stale_npm_package()
    {
        SkipUnlessLinux("a file link needs no privilege on Linux");
        var native = Executable("/home/me/.local/share/claude/versions/2.1.3");
        var current = _sandbox.Paths.DistroPath("/home/me/.local/share/claude/current");
        File.CreateSymbolicLink(current, native);
        Directory.CreateDirectory(_sandbox.Paths.DistroPath("/home/me/.local/bin"));
        File.CreateSymbolicLink(_sandbox.Paths.DistroPath("/home/me/.local/bin/claude"), current);
        _sandbox.Write("/home/me/.npm-global/lib/node_modules/@anthropic-ai/claude-code/package.json", """{ "version": "1.0.0" }""");

        Of("claude-code").Version.Should().Be("2.1.3", "the link chain is followed to its end; the stale package is not the binary that runs");
    }

    // ---------- R2 / R6: figures that are not whole ----------

    [Fact]
    public void R2_a_walk_cut_by_a_limit_is_a_lower_bound_with_its_reason_and_takes_no_growth()
    {
        var entry = AgentCatalogue.Agents.Single(a => a.Id == "claude-code");
        var presence = new AgentPresence(entry, [AgentDiscovery.Folder], [], string.Empty, "n/a", ["/a"], string.Empty);
        var cut = new AgentsSample(FixedTimeProvider.DefaultNow, [new AgentSize(entry.Id, [new AgentFolderSize("/a", true, 100, 1, false, [], "stopped after 2000000 entries; the figures are a lower bound")], SessionFigures.NotCounted("n/a"))]);
        var whole = new AgentsSample(FixedTimeProvider.DefaultNow, [new AgentSize(entry.Id, [new AgentFolderSize("/a", true, 50, 1, true, [], string.Empty)], SessionFigures.NotCounted("n/a"))]);
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;

        var report = AgentsReports.From("wsl", FixedTimeProvider.DefaultNow, [presence], new AgentSizes.Now(cut), whole, config).Agents.Single();

        report.TotalBytes.Should().Be(new Core.Status.ByteFigure(true, 100, "stopped after 2000000 entries; the figures are a lower bound"), "a lower bound says so");
        report.GrowthBytes.Available.Should().BeFalse("growth is taken between two WHOLE walks only");
    }

    [Fact]
    public void R6_a_folder_that_was_not_measured_has_no_file_count()
    {
        var entry = AgentCatalogue.Agents.Single(a => a.Id == "claude-code");
        var presence = new AgentPresence(entry, [AgentDiscovery.Folder], [], string.Empty, "n/a", ["/a"], string.Empty);
        var notReached = new AgentsSample(FixedTimeProvider.DefaultNow, [new AgentSize(entry.Id, [new AgentFolderSize("/a", true, 0, 0, false, [], AgentWalk.NotReached)], SessionFigures.NotCounted("n/a"))]);

        var folder = AgentsReports.From("wsl", FixedTimeProvider.DefaultNow, [presence], new AgentSizes.Now(notReached), null, ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config).Agents.Single().DataFolders.Single();

        folder.Files.Should().BeNull("0 files is a measurement; this folder was not measured");
    }

    // ---------- R4: the session listing ----------

    private AgentsSample WalkClaude(IFileSystem? files = null, Func<TimeProvider>? clock = null) =>
        new AgentWalk(files ?? _sandbox.Files, clock?.Invoke() ?? new FixedTimeProvider(), _sandbox.Paths.Home)
            .Measure([new AgentTarget(AgentCatalogue.Agents.Single(a => a.Id == "claude-code"), [_sandbox.Paths.DistroPath("/home/me/.claude")], _sandbox.Paths.DistroPath("/home/me/.claude"))], AgentWalk.CollectBudget, withNames: true, CancellationToken.None);

    [Fact]
    public void R4_a_repeated_double_star_is_refused_by_the_glob_rule()
    {
        ExtraAgentShape.GlobProblem("**/**/*.log").Should().Contain("**");
        ExtraAgentShape.GlobProblem("a/**/b/**").Should().Contain("**");
        ExtraAgentShape.GlobProblem("sessions/**").Should().BeEmpty();
    }

    [Fact]
    public void R4_each_folder_is_listed_once()
    {
        _sandbox.Sized("/home/me/.mycli/s/a/one.log", 10, FixedTimeProvider.DefaultNow);
        var counting = new CountingListings(_sandbox.Files);
        var under = _sandbox.Paths.DistroPath("/home/me/.mycli");

        var scan = SessionGlob.Find(counting, under, "s/**", new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase), static () => false);

        scan.Sessions.Should().ContainSingle();
        counting.Listed.GroupBy(p => p).Should().OnlyContain(g => g.Count() == 1, "a folder is listed once, whatever the glob");
    }

    [Fact]
    public void R4_a_listing_stopped_before_the_last_level_is_not_counted_and_says_why()
    {
        _sandbox.Sized("/home/me/.claude/projects/p/s.jsonl", 10, FixedTimeProvider.DefaultNow);
        var stepping = new SteppingTimeProvider(TimeSpan.FromSeconds(50)); // the budget runs out at the second level of projects/*/*.jsonl

        var sessions = WalkClaude(clock: () => stepping).Find("claude-code")!.Sessions;

        sessions.Counted.Should().BeFalse("a listing stopped before it reached the sessions counted nothing — that is not 0 sessions");
        sessions.Reason.Should().NotBeEmpty();
    }

    [Fact]
    public void R4_S3_a_linked_agent_folder_is_neither_walked_nor_listed()
    {
        SkipUnlessLinux("a folder link needs no privilege on Linux");
        _sandbox.Sized("/data/elsewhere/projects/p/s.jsonl", 10, FixedTimeProvider.DefaultNow);
        Directory.CreateDirectory(_sandbox.Paths.DistroPath("/home/me"));
        Directory.CreateSymbolicLink(_sandbox.Paths.DistroPath("/home/me/.claude"), _sandbox.Paths.DistroPath("/data/elsewhere"));

        var claude = WalkClaude().Find("claude-code")!;

        claude.Folders.Single().Reason.Should().Contain("link");
        claude.Sessions.Counted.Should().BeFalse();
        claude.Sessions.Reason.Should().Contain("link");
    }

    [Fact]
    public void S3_a_catalogue_folder_on_another_filesystem_than_the_home_is_not_walked()
    {
        _sandbox.Sized("/home/me/.claude/projects/p/s.jsonl", 10, FixedTimeProvider.DefaultNow);
        var claude = Path.GetFullPath(_sandbox.Paths.DistroPath("/home/me/.claude"));
        var files = new DeviceFiles(_sandbox.Files, p => Path.GetFullPath(p).StartsWith(claude, StringComparison.Ordinal) ? (0u, 159u) : (8u, 48u));

        var size = WalkClaude(files).Find("claude-code")!;

        size.Folders.Single().Reason.Should().Contain("another filesystem");
        size.Sessions.Counted.Should().BeFalse();
    }

    // ---------- R5: the probe's one budget ----------

    [Fact]
    public void R5_the_probe_measures_every_candidate_under_one_budget()
    {
        var cli = Executable("/home/me/.local/bin/mycli");
        foreach (var folder in new[] { "/home/me/.mycli", "/home/me/.config/mycli", "/home/me/.local/share/mycli", "/home/me/.cache/mycli" })
        {
            _sandbox.Sized(folder + "/x.bin", 10, FixedTimeProvider.DefaultNow);
        }

        var report = AgentProbe.Probe(_sandbox.Paths, _sandbox.Files, "/home/me/.local/bin/mycli", new SteppingTimeProvider(TimeSpan.FromSeconds(25)), CancellationToken.None);

        _ = cli;
        report.DataFolders.Count(f => f.Folder.Size.Reason == AgentWalk.NotReached).Should().BeGreaterThan(0, "four candidates share ONE 60 s budget, not 60 s each");
    }

    // ---------- R7: one session is its transcript and its companions ----------

    [Fact]
    public void R7_a_sessions_size_holds_its_companion_folders()
    {
        _sandbox.Sized("/home/me/.claude/projects/p/s1.jsonl", 100, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.claude/projects/p/s1/subagents/a.jsonl", 5000, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.claude/file-history/s1/v1.txt", 300, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.claude/projects/p/s2.jsonl", 2000, FixedTimeProvider.DefaultNow);

        var size = WalkClaude().Find("claude-code")!;

        size.Sessions.LargestBytes.Should().Be(5400, "D2's one session: the transcript, its folder and its file history");
        size.Largest!.First().Should().Be(new SessionName("projects/p/s1.jsonl", 5400));
    }

    // ---------- R9: what slow.agents keeps ----------

    [Fact]
    public void R9_a_persisted_walk_names_no_folder_of_another_filesystem()
    {
        _sandbox.Sized("/home/me/.claude/projects/secret-client-project/s.jsonl", 10, FixedTimeProvider.DefaultNow);
        var secret = Path.GetFullPath(_sandbox.Paths.DistroPath("/home/me/.claude/projects/secret-client-project"));
        var files = new DeviceFiles(_sandbox.Files, p => Path.GetFullPath(p).StartsWith(secret, StringComparison.Ordinal) ? (0u, 159u) : (8u, 48u));

        var persisted = new AgentWalk(files, new FixedTimeProvider(), _sandbox.Paths.Home)
            .Measure([new AgentTarget(AgentCatalogue.Agents.Single(a => a.Id == "claude-code"), [_sandbox.Paths.DistroPath("/home/me/.claude")], string.Empty)], AgentWalk.CollectBudget, withNames: false, CancellationToken.None);

        var excluded = persisted.Find("claude-code")!.Folders.Single().Excluded;
        excluded.Should().NotContain(e => e.Contains("secret", StringComparison.Ordinal));
        excluded.Should().Contain("1 folder(s) on another filesystem");
    }

    // ---------- S2 / S8: memory ----------

    [Theory]
    [InlineData("/home/me/.mycli/memory")]
    [InlineData("/home/me/memory/mycli")]
    [InlineData("/home/me/.mycli/Memory")]
    public void S2_a_manual_data_folder_at_or_under_a_memory_folder_is_refused(string folder)
    {
        Directory.CreateDirectory(_sandbox.Paths.DistroPath(folder));
        var agent = new ExtraAgent("/home/me/.local/bin/mycli", ExtraAgentShape.Wsl, "mycli", [folder], string.Empty);

        ExtraAgentRules.Judge(_sandbox.Paths, _sandbox.Files, [agent], []).Single().Refusal.Should().Contain("memory");
    }

    [Fact]
    public void S2_a_walk_whose_root_is_a_memory_folder_enters_nothing()
    {
        _sandbox.Sized("/home/me/.mycli/memory/notes.md", 10, FixedTimeProvider.DefaultNow);

        var measure = TreeWalk.Measure(_sandbox.Paths.DistroPath("/home/me/.mycli/memory"), new TreeLimits(100, TimeSpan.FromMinutes(1)), new TreeRules(new HashSet<string>(), new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase)), static _ => (0u, 0u), CancellationToken.None);

        measure.Should().BeOfType<TreeMeasure.Unreadable>().Which.Reason.Should().Contain("never entered");
    }

    [Fact]
    public void S8_a_memory_folder_is_never_entered_whatever_its_case()
    {
        _sandbox.Sized("/home/me/.claude/projects/p/s.jsonl", 10, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.claude/projects/p/Memory/notes.md", 9000, FixedTimeProvider.DefaultNow);

        WalkClaude().Find("claude-code")!.TotalBytes.Should().Be(10);
    }

    // ---------- R10 / R11 ----------

    [Fact]
    public void R10_a_user_layer_that_would_render_past_the_layer_cap_is_not_written()
    {
        var folders = Enumerable.Range(0, ExtraAgentShape.MaxFolders).Select(i => "/home/me/" + new string('é', 1000) + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var agents = Enumerable.Range(0, ExtraAgentShape.MaxEntries).Select(i => new ExtraAgent("/a", ExtraAgentShape.Wsl, $"a{i}", folders, string.Empty)).ToList();

        var result = new UserConfigWriter(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider()).Set(ConfigKeys.AiAgents.Extra, new ConfigValue.AgentList(agents));

        result.Should().NotBeOfType<UserConfigWriteResult.Written>("JSON escapes every non-ASCII character as six bytes: the layer would be refused by its own reader");
        File.Exists(_sandbox.Paths.UserConfigFile).Should().BeFalse();
    }

    [Fact]
    public void R11_the_probe_asks_whether_THIS_user_may_start_the_cli()
    {
        SkipUnlessLinux("access(X_OK) is the distro's question");
        var cli = _sandbox.Write("/home/me/.local/bin/othersonly", "#!/bin/false");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherExecute);
        }


        AgentProbe.Probe(_sandbox.Paths, _sandbox.Files, "/home/me/.local/bin/othersonly", new FixedTimeProvider(), CancellationToken.None).Usable
            .Should().BeFalse("an execute bit for OTHERS does not let the owner start it");
    }

    private sealed class DeviceFiles(IFileSystem inner, Func<string, (uint, uint)?> device) : DelegatingFileSystem(inner)
    {
        public override (uint Major, uint Minor)? DeviceOf(string path) => device(path);

        public override TreeMeasure WalkTree(string path, TreeLimits limits, TreeRules rules, CancellationToken cancellationToken) =>
            TreeWalk.Measure(path, limits, rules, p => device(p), cancellationToken);
    }

    private sealed class CountingListings(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<string> Listed { get; } = [];

        public override IReadOnlyList<FileEntry> ListEntries(string path)
        {
            Listed.Add(Path.GetFullPath(path));
            return base.ListEntries(path);
        }
    }

    /// <summary>Every timestamp is <paramref name="step"/> after the last.</summary>
    private sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);

        public override DateTimeOffset GetUtcNow() => FixedTimeProvider.DefaultNow;
    }
}
