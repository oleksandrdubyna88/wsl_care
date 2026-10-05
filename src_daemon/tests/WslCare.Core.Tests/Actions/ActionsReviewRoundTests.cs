using System.Globalization;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>Plan §15q, the E7.S0 review round — S7 (A11 ends only the target user's processes) and C5 (<c>idle.minutes</c>
/// higher is really stricter).</summary>
public sealed class ActionsReviewRoundTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("actions-review-round");

    public void Dispose() => _sandbox.Dispose();

    private sealed class RecordingSignals : IProcessSignals
    {
        public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Ended(NeededKill: false))]);
    }

    private void Stat(int pid, int uid)
    {
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} (VBCSCompiler) S 1 {pid} {pid} 0 -1 0 0 0 0 0 100 0 0 0 20 0 1 0 4000 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\tVBCSCompiler\nState:\tS (sleeping)\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t1000 kB\nRssShmem:\t0 kB\n"));
    }

    /// <summary>S7 (decided): A11 is root, and it ends idle orphans of the TARGET user only — never another account's.</summary>
    [Fact]
    public async Task An_idle_orphan_of_another_non_root_account_is_never_a_suspect()
    {
        Stat(10, uid: 1000);
        Stat(20, uid: 1001);
        IReadOnlyList<ProcessEntry> processes =
        [
            UserWorld.Process(10, "dotnet /usr/lib/dotnet/sdk/VBCSCompiler.dll -pipename:10", family: "dotnet-build-servers", orphaned: true, tty: false, ageHours: 24, user: "me"),
            UserWorld.Process(20, "dotnet /usr/lib/dotnet/sdk/VBCSCompiler.dll -pipename:20", family: "dotnet-build-servers", orphaned: true, tty: false, ageHours: 24, user: "sam"),
        ];
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        var context = new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), config, RunTrigger.Manual,
            new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(processes)),
            Signals = new RecordingSignals(),
            Wait = (_, _) => Task.CompletedTask,
        };
        var action = new SuspectTermination();

        var preview = await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), context.TargetUser, []), CancellationToken.None);

        preview.Targets.Select(t => t.Name).Should().Equal("10 p10");
    }

    /// <summary>C5: <c>idle.minutes</c> is declared "higher is safer", so a longer window must never be LOOSER: a machine busy
    /// two minutes ago is busy over the last 15 minutes too — busy is the highest average up to the window, not one of them.</summary>
    [Fact]
    public void A_machine_busy_a_minute_ago_is_not_idle_over_a_longer_window()
    {
        _sandbox.Load(load1: 4.0, load5: 0.10, load15: 0.05, cpus: 4);
        var loaded = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new WslCare.Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]);

        var sample = IdleGate.Sample(_sandbox.Paths, _sandbox.Files, Reading.Of(UserWorld.Snapshot([])), minutes: 15);

        IdleGate.Judge(sample, loaded.Config).Idle.Should().BeFalse("a load of 4 on 4 CPUs a minute ago is a busy machine over any window that holds that minute");
    }
}
