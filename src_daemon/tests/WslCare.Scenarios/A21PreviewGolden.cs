using System.Globalization;
using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// S7b.2b (the code round's finding 0: a contract with two implementations is checked through what the EMITTER writes): A21's line
/// of an <c>act A21 --preview --json</c> answer with two client-gone relays selected — the <c>shown</c> <c>pid:start</c> list, the
/// items and the basis the extension's *Stop client-gone relays* holds and shows. The built CLI cannot answer it in a scenario: a
/// relay needs an exe link <c>/init</c> and fd links a Windows leg cannot make, and an idle CPU history needs time to pass. So the
/// daemon's OWN <see cref="InteropRelayStop"/> previews over a synthetic <c>/proc</c> whose links the file system answers, and the
/// line is built as the engine builds a previewed one (<c>ActionEngine.PreviewedAsync</c>: status <c>previewed</c>, the
/// <c>shown</c> list and <c>shownTruncated</c> of an <see cref="IBoundToShownList"/> action).
/// </summary>
internal static class A21PreviewGolden
{
    public const string File = "act-a21-preview-action.json";

    private const string Boot = "6d1c1c5e-0000-4000-8000-0000000000a2";
    private const string CredsExe = "/mnt/c/Users/user/AppData/Local/Programs/creds/creds-mcp.exe";
    private const int SessionInit = 7410;
    private static readonly int[] Relays = [12062, 12063];
    private static readonly DateTimeOffset Now = new(2000, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The links a relay needs, answered by the file system (a link needs a privilege on Windows).</summary>
    private sealed class Linked(IFileSystem inner, IReadOnlyDictionary<string, string> links) : DelegatingFileSystem(inner)
    {
        public override LinkReadResult ReadLink(string path) =>
            links.TryGetValue(path.Replace('\\', '/'), out var target) ? new LinkReadResult.Target(target) : new LinkReadResult.NotALink();
    }

    /// <summary>The line, normalised as every golden is (the sandbox's path in the basis becomes the golden root).</summary>
    public static async Task<string> TextAsync(ISet<string> matched)
    {
        using var sandbox = new LinuxSandbox("golden-a21");
        sandbox.Write("/proc/sys/kernel/random/boot_id", Boot + "\n");
        var proc = sandbox.Paths.ProcRoot.Replace('\\', '/');
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        Stat(sandbox, SessionInit, parent: 1, uid: 0, comm: "Relay(7411)");
        foreach (var pid in Relays)
        {
            Stat(sandbox, pid, parent: SessionInit, uid: 1000, comm: "init");
            sandbox.Write($"/proc/{pid}/cmdline", $"/init\0{CredsExe}\0");
            links[$"{proc}/{pid}/exe"] = "/init";
            foreach (var fd in new[] { 0, 1 })
            {
                sandbox.Write($"/proc/{pid}/fd/{fd}", string.Empty);
                links[string.Create(CultureInfo.InvariantCulture, $"{proc}/{pid}/fd/{fd}")] = string.Create(CultureInfo.InvariantCulture, $"pipe:[{(pid * 10) + fd}]");
            }
        }

        var files = new Linked(sandbox.Files, links);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        var config = ConfigLoader.Load(sandbox.Paths, files).Config;
        var processes = Relays.Select(Entry).ToList();
        AgentCpuHistory.Record(sandbox.Paths, files, processes, SampleTime.Of(clock), config);
        clock.Advance(TimeSpan.FromMinutes(42));
        AgentCpuHistory.Record(sandbox.Paths, files, processes, SampleTime.Of(clock), config);
        var action = new InteropRelayStop();
        var target = new TargetUserResult.Found(new TargetUser("user", 1000, "/home/user"), "golden");
        var context = new ActionContext(sandbox.Paths, files, clock, config, RunTrigger.Cli, target)
        {
            Processes = _ => Reading.Of(new ProcessSnapshot(processes.Count, 0, 0, 0, processes.Sum(p => p.HeldBytes), processes, [])),
        };
        var preview = await action.PreviewAsync(context, new ActionCommands(action, new RecordingCommandRunner(), target, []), CancellationToken.None);
        var reason = new[] { preview.Skip, preview.Refusal }.FirstOrDefault(r => r.Length > 0, string.Empty);
        var line = new ActionOutcome(action.Id.Text, action.Summary, ActionStatus.Previewed, reason, preview, null)
        {
            Shown = preview.Available ? action.Shown(preview) : null,
            ShownTruncated = preview.Available && ShownList.Truncates(preview.Count) ? true : null,
        };

        return GoldenContracts.Normalise(JsonSerializer.Serialize(line, WslCareJsonContext.Default.ActionOutcome), sandbox.Root.Path, matched);
    }

    private static ProcessEntry Entry(int pid) =>
        new(pid, SessionInit, "user", "init", 'S', 2_048_000, 0, Reading.Of(TimeSpan.FromHours(1)), Reading.Of(0.5), Reading.Of("/"), $"/init {CredsExe}", "other", false, false, true)
        {
            StartTicks = Reading.Of(36501L + pid),
        };

    private static void Stat(LinuxSandbox sandbox, int pid, int parent, int uid, string comm)
    {
        var start = 36501L + pid;
        sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} ({comm}) S {parent} {pid} {pid} 0 -1 0 0 0 0 0 500 0 0 0 20 0 1 0 {start} 0 0\n"));
        sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\t{comm}\nState:\tS (sleeping)\nPPid:\t{parent}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t2000 kB\nRssShmem:\t0 kB\n"));
        sandbox.Write($"/proc/{pid}/comm", comm + "\n");
    }
}
