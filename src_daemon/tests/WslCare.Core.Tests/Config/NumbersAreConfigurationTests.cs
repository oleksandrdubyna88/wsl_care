using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// E7.S2c, the owner's rule (2026-10-05): every behavioural number is configuration. Group A keys are ordinary; group B keys —
/// a limit on what ROOT reads, does or waits for — are machine-layer only, with a hard range; coupled limits are checked at load.
/// </summary>
public sealed class NumbersAreConfigurationTests
{
    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "/etc/wsl-care/config.json");
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static (ConfigLayerFile, FileReadResult) Layer(ConfigLayerFile file, string json) => (file, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)));

    private static ConfigLoadResult Load(string machine, string user = "{}") => ConfigLoader.Load([Defaults(), Layer(Machine, machine), Layer(User, user)]);

    /// <summary>The inventory's group A (behaviour) — ordinary keys.</summary>
    public static readonly TheoryData<string> GroupA =
    [
        "walk.intervalHours", "walk.maxExclusionsNamed", "agents.measureBudgetSeconds", "runs.stopMarkerRetentionHours", "preview.maxItems",
        "clock.minimumGapMinutes", "clock.driftObservationsApartMinutes", "trim.periodDays", "aptCache.triggerMb", "toolCaches.triggerGb",
        "editorServers.keepNewest", "processes.cpuWindowSeconds", "processes.termGraceSeconds", "runs.historyRetentionDays",
        "events.retryFirstSeconds", "events.retryMaxSeconds", "events.retryFactor", "events.stalenessSlackMinutes",
        "events.topImages", "events.segmentMinutes", "thresholds.pageCacheWarnGib", "thresholds.pageCacheActGib",
        "thresholds.pageCacheActAvailablePercent", "thresholds.inactiveAnonWarnGib", "thresholds.order7WarnBlocks",
        "thresholds.memoryPressureWarn", "thresholds.rootUsedWarnPercent", "journal.maxGb", "thresholds.journalHistoryWarnDays",
        "thresholds.clockJumpsWarnPer4h", "thresholds.collectorFreshMinutes", "wslConfig.recommendedMemoryGb",
        "thresholds.wslMemoryCriticalPercent", "processes.topCount",
    ];

    /// <summary>The inventory's group B (root-safety limits) — machine-layer-only keys.</summary>
    public static readonly TheoryData<string> GroupB =
    [
        "walk.maxEntries", "walk.maxSeconds", "agents.walkBudgetSeconds", "agents.sessionMaxEntries", "agents.maxPackageJsonBytes",
        "agents.lookupCeilingSeconds", "requests.maxRead", "requests.maxQueued", "requests.graceSeconds", "requests.futureSkewSeconds",
        "act.maxShownNames", "act.maxListBytes", "act.stdinTimeoutSeconds", "stops.maxMarkerBytes", "timer.firstDryWindowDays",
        "timer.periodHours", "timer.lateSlackMinutes", "timer.randomizedDelayMinutes", "timer.accuracyMinutes", "units.nice",
        "units.memoryMaxMb", "units.stopTimeoutSeconds", "units.eventsRestartSeconds", "docker.probeTimeoutSeconds",
        "docker.listTimeoutSeconds", "docker.diskUsageTimeoutSeconds", "docker.removeTimeoutSeconds", "docker.pruneTimeoutSeconds",
        "docker.batchSize", "docker.smallOutputCapBytes", "docker.largeOutputCapBytes", "docker.actionOutputCapBytes",
        "docker.maxDaemonJsonBytes", "systemd.timeoutSeconds", "systemd.searchTimeoutSeconds", "systemd.outputCapBytes",
        "systemd.searchOutputCapBytes", "systemd.unitOutputCapBytes", "systemd.unitStartTimeoutSeconds", "systemd.unitStopTimeoutSeconds",
        "journal.vacuumTimeoutSeconds", "memory.syncTimeoutSeconds", "memory.dropCachesTimeoutSeconds", "memory.compactTimeoutSeconds",
        "aptCache.cleanTimeoutSeconds", "snap.removeTimeoutSeconds", "buildServers.shutdownTimeoutSeconds", "trim.timeoutSeconds",
        "npm.cleanTimeoutSeconds", "nuget.clearTimeoutSeconds", "toolCaches.trimTimeoutSeconds", "toolCaches.whereTimeoutSeconds",
        "toolCaches.whereOutputCapBytes", "clock.stepTimeoutSeconds", "health.windowsClockTimeoutSeconds", "health.snapTimeoutSeconds",
        "health.outputCapBytes", "health.kernelLinesKept", "health.kernelLineChars", "health.maxWslConfigBytes", "commands.outputCapBytes",
        "commands.systemDriveLookupSeconds", "commands.maxTimeoutHours", "commands.drainGraceMilliseconds", "userFiles.maxSmallFileBytes",
        "userFiles.maxJsonBytes", "userFiles.maxLinkBytes", "records.maxReasonChars", "records.lockTimeoutSeconds",
        "records.maxStateFileBytes", "records.maxHistoryBytes", "logs.maxDetailsRead", "logs.maxRangeDays", "processes.killWaitSeconds",
        "processes.shownCommandChars", "processes.signalSliceMilliseconds", "events.segmentSlackSeconds", "events.earlyEndSeconds",
        "running.heartbeatSeconds", "running.wedgedAfterSeconds", "running.readRetries", "running.readRetryMilliseconds",
        "config.maxLayerBytes", "patterns.matchTimeoutMilliseconds", "files.renameRetryMilliseconds", "files.renameRetrySleepMilliseconds",
        "files.lockJitterMinMilliseconds", "files.lockJitterMaxMilliseconds", "agentCpu.maxEntries", "agentCpu.maxBytes", "requests.maxBytes", "events.startsRetentionDays", "timer.runLimitMinutes", "running.noProgressMinutes",
    ];

    [Theory]
    [MemberData(nameof(GroupA))]
    public void A_behaviour_number_is_an_ordinary_key(string name)
    {
        var key = ConfigKeys.Find(name);

        key.Should().BeOfType<ConfigKey.IntKey>($"{name} is a number the owner may change");
        key!.Trust.MachineOnly.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GroupB))]
    public void A_root_safety_limit_is_a_machine_only_key(string name)
    {
        var key = ConfigKeys.Find(name);

        key.Should().BeOfType<ConfigKey.IntKey>($"{name} is a number the owner may change — in the machine layer");
        key!.Trust.MachineOnly.Should().BeTrue("a limit on what root reads, does or waits for is never the user layer's");
    }

    [Fact]
    public void A_limit_above_its_hard_maximum_is_refused_and_a_user_layer_value_of_one_is_a_notice()
    {
        var above = Load("""{ "walk": { "maxEntries": 3000000 } }""");
        var fromUser = Load("{}", """{ "walk": { "maxEntries": 1000 } }""");

        above.Errors.Should().ContainSingle().Which.Message.Should().Contain("walk.maxEntries").And.Contain("2000000");
        fromUser.IsObserveOnly.Should().BeFalse();
        fromUser.Notices.Should().ContainSingle(n => n.Key == "walk.maxEntries");
        fromUser.Config.Int((ConfigKey.IntKey)ConfigKeys.Find("walk.maxEntries")!).Should().Be(2000000);
    }

    [Theory]
    [InlineData("""{ "running": { "heartbeatSeconds": 20, "wedgedAfterSeconds": 30 } }""", "running.wedgedAfterSeconds")]
    [InlineData("""{ "requests": { "maxRead": 10, "maxQueued": 20 } }""", "requests.maxRead")]
    [InlineData("""{ "act": { "maxShownNames": 10000, "maxListBytes": 65536 } }""", "act.maxListBytes")]
    [InlineData("""{ "logs": { "maxRangeDays": 90 }, "runs": { "historyRetentionDays": 200 } }""", "logs.maxRangeDays")]
    [InlineData("""{ "units": { "stopTimeoutSeconds": 120 }, "systemd": { "unitStopTimeoutSeconds": 120 } }""", "systemd.unitStopTimeoutSeconds")]
    [InlineData("""{ "files": { "lockJitterMinMilliseconds": 50, "lockJitterMaxMilliseconds": 20 } }""", "files.lockJitterMaxMilliseconds")]
    [InlineData("""{ "timer": { "periodHours": 5 } }""", "timer.periodHours")]
    [InlineData("""{ "timer": { "runLimitMinutes": 60 } }""", "timer.runLimitMinutes")]
    [InlineData("""{ "running": { "noProgressMinutes": 5 } }""", "running.noProgressMinutes")]
    [InlineData("""{ "timer": { "lateSlackMinutes": 10, "randomizedDelayMinutes": 9, "accuracyMinutes": 1 } }""", "timer.lateSlackMinutes")]
    [InlineData("""{ "agentCpu": { "maxBytes": 16384 } }""", "agentCpu.maxBytes")]
    [InlineData("""{ "requests": { "maxBytes": 65536 } }""", "requests.maxBytes")]
    public void Coupled_limits_that_contradict_each_other_refuse_the_layer_naming_the_rule(string machine, string named)
    {
        var result = Load(machine);

        result.IsObserveOnly.Should().BeTrue("a contradiction between two limits is a configuration error");
        result.Errors.Should().Contain(e => e.Message.Contains(named, StringComparison.Ordinal));
    }

    /// <summary>The point of the rule: a machine value REACHES the code it bounds — a command's ceiling, its template's (read when
    /// a request is built, never frozen when the template is declared), a wait, a walk's limits.</summary>
    [Fact]
    public void A_machine_value_reaches_the_command_the_wait_and_the_walk_it_bounds()
    {
        var loaded = Load("""
            {
              "docker": { "probeTimeoutSeconds": 3, "pruneTimeoutSeconds": 61, "batchSize": 7 },
              "health": { "windowsClockTimeoutSeconds": 6 },
              "systemd": { "unitStopTimeoutSeconds": 150 },
              "units": { "stopTimeoutSeconds": 100 },
              "running": { "heartbeatSeconds": 2, "wedgedAfterSeconds": 40 },
              "walk": { "maxEntries": 5000, "maxSeconds": 9 },
              "commands": { "maxTimeoutHours": 2, "systemDriveLookupSeconds": 9 }
            }
            """);
        loaded.IsObserveOnly.Should().BeFalse(string.Join("; ", loaded.Errors.Select(e => e.Display)));
        // The templates are static: read them once under the defaults FIRST, so a template that froze its limits when it was
        // declared shows here instead of hiding behind a first use inside the scope.
        Core.Actions.Clock.ClockFix.WindowsClock.Ceiling.Should().Be(TimeSpan.FromSeconds(20));
        Core.Systemd.UnitCommands.StopTemplate.Ceiling.Should().Be(TimeSpan.FromSeconds(120));
        Core.Processes.SystemDriveLookup.ThisMachine.Ceiling.Should().Be(TimeSpan.FromSeconds(5));
        Core.Processes.SystemDriveLookup.NotConsulted.Ceiling.Should().Be(TimeSpan.FromSeconds(5));

        using (Tuning.Use(loaded.Config))
        {
            Core.Docker.DockerCommands.Version.Ceiling.Should().Be(TimeSpan.FromSeconds(3));
            Core.Actions.DockerCleanups.DockerCleanupCommands.BuilderPruneAll.Ceiling.Should().Be(TimeSpan.FromSeconds(61), "a template's limit is read when a request is built");
            Core.Actions.DockerCleanups.DockerCleanupCommands.Batch.Should().Be(7);
            Core.Actions.Clock.ClockFix.WindowsClock.Ceiling.Should().Be(TimeSpan.FromSeconds(6), "a fixed read template follows its command");
            Core.Systemd.UnitCommands.StopTemplate.Ceiling.Should().Be(TimeSpan.FromSeconds(150));
            Core.Actions.Engine.RunningState.HeartbeatPeriod.Should().Be(TimeSpan.FromSeconds(2));
            Core.Actions.Engine.RunningState.StaleAfter.Should().Be(TimeSpan.FromSeconds(40));
            Core.Folders.FolderSizes.Limits.Should().Be(new Core.Files.TreeLimits(5000, TimeSpan.FromSeconds(9)));
            Core.Processes.CommandRequest.MaxTimeout.Should().Be(TimeSpan.FromHours(2));
            Core.Processes.SystemDriveLookup.ThisMachine.Ceiling.Should().Be(TimeSpan.FromSeconds(9), "PR #10 retro round O5: the system-drive lookup's ceiling is read when the lookup is asked for");
            Core.Processes.SystemDriveLookup.NotConsulted.Ceiling.Should().Be(TimeSpan.FromSeconds(9));
        }

        Core.Docker.DockerCommands.Version.Ceiling.Should().Be(TimeSpan.FromSeconds(10), "outside the scope the defaults are back");
        Core.Actions.DockerCleanups.DockerCleanupCommands.BuilderPruneAll.Ceiling.Should().Be(TimeSpan.FromSeconds(900));
    }

    [Fact]
    public void A_default_is_todays_value_so_behaviour_is_unchanged()
    {
        Core.Actions.Engine.RunningState.HeartbeatPeriod.Should().Be(TimeSpan.FromSeconds(5));
        Core.Actions.Engine.RunningState.StaleAfter.Should().Be(TimeSpan.FromSeconds(30));
        Core.Processes.CommandRequest.MaxTimeout.Should().Be(TimeSpan.FromHours(24));
        Core.Processes.CommandRequest.DefaultOutputCapChars.Should().Be(1024 * 1024);
        Core.Folders.FolderSizes.Limits.Should().Be(new Core.Files.TreeLimits(2_000_000, TimeSpan.FromMinutes(2)));
        Core.Systemd.UnitCommands.StopTemplate.Ceiling.Should().Be(TimeSpan.FromSeconds(120));
        Core.Actions.Engine.DryRunWindow.Length.Should().Be(TimeSpan.FromDays(7));
        Core.Doctor.DoctorRun.LastRunMaxAge.Should().Be(TimeSpan.FromHours(5), "the timer period (4 h) plus the late slack (1 h)");
    }

    /// <summary>Review N-6: one definition per number — a sentence a person reads says the number IN FORCE, derived from its key,
    /// never a copy of the default typed into the text.</summary>
    [Fact]
    public void A_sentence_says_the_number_in_force_not_a_copy_of_the_default()
    {
        var loaded = Load("""
            {
              "processes": { "termGraceSeconds": 20 },
              "editorServers": { "keepNewest": 3 },
              "runs": { "historyRetentionDays": 120 },
              "timer": { "firstDryWindowDays": 10 },
              "thresholds": { "rootUsedWarnPercent": 70, "order7WarnBlocks": 40 },
              "clock": { "driftObservationsApartMinutes": 9 }
            }
            """);
        loaded.IsObserveOnly.Should().BeFalse(string.Join("; ", loaded.Errors.Select(e => e.Display)));
        const string missing = "not read";

        using (Tuning.Use(loaded.Config))
        {
            new Core.Actions.Suspects.SuspectTermination().Summary.Should().Contain("SIGKILL after 20 s");
            new Core.Actions.Suspects.AgentOrphans().Summary.Should().Contain("SIGKILL after 20 s");
            new Core.Actions.UserCaches.EditorServerCleanup().Summary.Should().Contain("the newest 3 kept");
            Core.History.RunShow.NothingNamesIt.Should().Contain("120-day retention");
            Core.Thresholds.ThresholdRules.ApartText.Should().Be("9 minutes");
            var verdicts = Core.Thresholds.ThresholdRules.FromSample(
                Core.Collectors.Reading.Missing<Core.Collectors.MemorySnapshot>(missing),
                Core.Collectors.Reading.Missing<Core.Collectors.VolumeUsage>(missing),
                Core.Collectors.Reading.Missing<Core.Health.WslConfigAudit>(missing),
                loaded.Config);
            verdicts.Single(v => v.Id == "disk.root").Limit.Should().Be("warn > 70 %");
            verdicts.Single(v => v.Id == "memory.fragmentation").Limit.Should().StartWith("warn < 40 free order-7 blocks");
        }
    }

    /// <summary>E7.S2b/S2c review C-M1: a USER value in its range that breaks a coupled rule with the machine layer is a notice —
    /// the value not taken, root not observe-only — and the rule is said against the user layer that set it.</summary>
    [Fact]
    public void A_user_value_that_breaks_a_rule_is_a_notice_and_the_value_below_stays_in_force()
    {
        var loaded = Load("""{ "logs": { "maxRangeDays": 120 } }""", """{ "runs": { "historyRetentionDays": 200 } }""");

        loaded.IsObserveOnly.Should().BeFalse("a user value in range never puts root observe-only");
        loaded.Notices.Should().Contain(n => n.Key == "runs.historyRetentionDays" && n.File.Layer == ConfigLayer.User && n.Message.Contains("logs.maxRangeDays", StringComparison.Ordinal));
        loaded.Config.Int(ConfigKeys.Runs.HistoryRetentionDays).Should().Be(90, "the user's 200 is not taken: the default stays");
        ConfigKeys.Runs.HistoryRetentionDays.Max.Should().BeLessThanOrEqualTo(ConfigKeys.Logs.MaxRangeDays.Max, "review C-M1: the user key's range is clamped to the machine key's");
    }

    /// <summary>E7.S2b/S2c review C-M2: a MACHINE layer that breaks a rule is an error (observe-only) AND its value is not in force —
    /// the drop-in rendered from it says the value below.</summary>
    [Fact]
    public void A_machine_value_that_breaks_a_rule_is_an_error_and_not_in_force()
    {
        var loaded = Load("""{ "timer": { "periodHours": 5 } }""");

        loaded.IsObserveOnly.Should().BeTrue();
        loaded.Errors.Should().ContainSingle().Which.File.Layer.Should().Be(ConfigLayer.Machine);
        loaded.Config.Int(ConfigKeys.Timer.PeriodHours).Should().Be(4, "the contradiction is never in force");
        using (Tuning.Use(loaded.Config))
        {
            Core.Systemd.UnitDropIns.Render("wsl-care.timer").Should().Contain("OnCalendar=*-*-* 00/4:00:00").And.NotContain("00/5");
        }
    }

    /// <summary>The embedded defaults are never checked by the merge (nothing lies below them), so a test holds them to every rule.</summary>
    [Fact]
    public void The_defaults_hold_every_coupled_rule()
    {
        var defaults = ConfigLoader.Load([Defaults()]).Config;

        NumberRules.Broken(defaults).Select(r => r.Says(defaults)).Should().BeEmpty();
        RunBudget.TimerRunWorstCase(defaults).Should().BeLessThan(TimeSpan.FromMinutes(defaults.Int(ConfigKeys.Timer.RunLimitMinutes)));
    }

    /// <summary>E7.S2b/S2c review C-M3: a ceiling COMPOSED of keys — the event stream's segment plus its slack — stays under the
    /// longest wait a request accepts at every value the ranges allow, with the margin the coupled rule keeps.</summary>
    [Fact]
    public void Every_composed_command_ceiling_stays_under_the_commands_maximum()
    {
        var max = TimeSpan.FromHours(ConfigKeys.Commands.MaxTimeoutHours.Min);
        var stream = TimeSpan.FromMinutes(ConfigKeys.Events.SegmentMinutes.Max) + TimeSpan.FromSeconds(ConfigKeys.Events.SegmentSlackSeconds.Max) + TimeSpan.FromSeconds(NumberRules.CeilingMarginSeconds);

        stream.Should().BeLessThanOrEqualTo(max, "the event stream's ceiling (segment + slack) is a CommandRequest's timeout");
        var loaded = Load("""{ "events": { "segmentMinutes": 50, "segmentSlackSeconds": 60 }, "commands": { "maxTimeoutHours": 1 } }""");
        loaded.IsObserveOnly.Should().BeFalse(string.Join("; ", loaded.Errors.Select(e => e.Display)));
        using (Tuning.Use(loaded.Config))
        {
            var now = DateTimeOffset.UnixEpoch;
            var command = Core.Docker.DockerCommands.EventStream(now, now + Core.Events.EventsFollower.SegmentLength, Core.Events.EventsFollower.SegmentSlack);
            FluentActions.Invoking(command.ToRequest).Should().NotThrow("CommandRequest refuses a timeout above commands.maxTimeoutHours");
        }
    }

    [Fact]
    public void Every_timeout_key_stays_under_the_commands_maximum()
    {
        var max = (ConfigKey.IntKey)ConfigKeys.Find("commands.maxTimeoutHours")!;
        var timeouts = ConfigKeys.All.OfType<ConfigKey.IntKey>().Where(k => k.Name.Contains("Timeout", StringComparison.Ordinal) && k != max).ToList();

        timeouts.Should().NotBeEmpty();
        timeouts.Should().OnlyContain(k => TimeSpan.FromSeconds(k.Max) <= TimeSpan.FromHours(max.Min), "no command timeout may reach past the least maximum the machine may set");
    }
}
