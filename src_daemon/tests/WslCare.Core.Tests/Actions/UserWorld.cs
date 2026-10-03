using WslCare.Core.Actions;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The distro of a user-scoped action's test: a sandbox whose target user is <c>me</c> (home <c>/home/me</c>), tools placed in
/// that user's bin folders (or left out — "not installed"), a recording runner under the PRODUCT policy, and a process table
/// the test writes. A tool's "effect" is the test changing files under the sandbox — nothing real ever runs.
/// </summary>
internal sealed class UserWorld : IDisposable
{
    public static readonly TargetUser Me = new("me", 1000, "/home/me");

    public UserWorld(string purpose) => Sandbox = new LinuxSandbox(purpose);

    public LinuxSandbox Sandbox { get; }

    public RecordingCommandRunner Runner { get; } = new() { Policy = CommandPolicy.Product, Default = new CommandOutcome.FailedToStart("not scripted") };

    public List<ProcessEntry> Processes { get; } = [];

    public void Dispose() => Sandbox.Dispose();

    /// <summary>A tool installed for the target user, in one of the fixed bin folders.</summary>
    public UserWorld Tool(string name, string folder = "/home/me/.local/bin")
    {
        Sandbox.Executable(folder, name);
        return this;
    }

    /// <summary>A file of <paramref name="bytes"/> bytes at the distro path.</summary>
    public string File(string distroPath, int bytes) => Sandbox.Sized(distroPath, bytes, FixedTimeProvider.DefaultNow.AddDays(-30));

    public ActionContext Context(RunTrigger trigger = RunTrigger.Cli, string userConfig = "{}")
    {
        Sandbox.Write("/home/me/.config/wsl-care/config.json", userConfig);
        var loaded = ConfigLoader.Load(Sandbox.Paths, Sandbox.Files);
        return new ActionContext(Sandbox.Paths, Sandbox.Files, new FixedTimeProvider(), loaded.Config, trigger, new TargetUserResult.Found(Me, "test"))
        {
            Processes = _ => Reading.Of(Snapshot(Processes)),
        };
    }

    public ActionCommands Commands(ICleanupAction action, ActionContext context) =>
        new(action, Runner, context.TargetUser, TargetUserCommands.BinFolders(Me, Sandbox.Paths, Sandbox.Files));

    /// <summary>Scripts what the tool <paramref name="tool"/> answers when run as the target user, playing <paramref name="effect"/> first.</summary>
    public UserWorld OnTool(string tool, Action effect, int exit = 0, string stderr = "")
    {
        Runner.ScriptEffect(argv => TargetUserArgv.Parse(argv) is { } wrapped && wrapped.ExecutableName == tool, _ =>
        {
            effect();
            return RecordingCommandRunner.Exited(exit, string.Empty, stderr);
        });
        return this;
    }

    /// <summary>The wrapped commands the runner was asked for, as <c>tool args…</c>.</summary>
    public IReadOnlyList<string> Wrapped =>
        [.. Runner.Requests.Select(r => TargetUserArgv.Parse(r.Argv)).OfType<WrappedCommand>().Select(w => string.Join(' ', [w.ExecutableName, .. w.Arguments]))];

    public static ProcessEntry Process(int pid, string commandLine, string cwd = "/", string family = "other", bool orphaned = false, bool tty = false, double ageHours = 24, string user = "me", char state = 'S', long held = 1_000_000) =>
        new(pid, orphaned ? 1 : 500, user, $"p{pid}", state, held, 0, Reading.Of(TimeSpan.FromHours(ageHours)), Reading.Of(0.5), Reading.Of(cwd), commandLine, family, orphaned, tty, false);

    public static ProcessSnapshot Snapshot(IReadOnlyList<ProcessEntry> processes) =>
        new(processes.Count, 0, 0, 0, processes.Sum(p => p.HeldBytes), processes, []);
}
