using System.Text;
using System.Text.Json;

using WslCare.Core;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E10.S1b (plan §15s, the plan round's #2): A13's line of an <c>act A13 --preview --json</c> answer when a session is due — the
/// per-agent items the extension's modal names. The built CLI cannot answer it in a scenario: its A13 refuses a product binary that
/// is not root's alone (<see cref="ArchiveActFlows"/>), which a test build never is. So the daemon's OWN <see cref="ArchiveAction"/>
/// previews over a child that answers one due session, and the line is built as the engine builds a previewed one
/// (<c>ActionEngine.PreviewedAsync</c>: status <c>previewed</c>, the first of the user refusal / skip / refusal as the reason).
/// </summary>
internal static class A13PreviewGolden
{
    public const string File = "act-a13-preview-action.json";

    private const string Base = "/mnt/v/ai-archive";
    private const string Installed = "/opt/wsl-care/bin/wsl-care";
    private static readonly DateTimeOffset Now = new(2000, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The line, indented as the CLI writes it.</summary>
    public static async Task<string> TextAsync()
    {
        using var sandbox = new LinuxSandbox("golden-a13");
        sandbox.Write("/proc/sys/kernel/random/boot_id", "6d1c1c5e-0000-4000-8000-0000000000a1\n");
        sandbox.Write("/etc/pam.d/runuser", "auth sufficient pam_rootok.so\nsession required pam_unix.so\n");
        var action = new ArchiveAction();
        var target = new TargetUserResult.Found(new TargetUser("user", 1000, "/home/user"), "golden");
        var context = new ActionContext(sandbox.Paths, sandbox.Files, new ManualTimeProvider(Now), Config(), RunTrigger.Manual, target)
        {
            Processes = _ => Reading.Of(new ProcessSnapshot(0, 0, 0, 0, 0, [], [])),
            RunId = "20000115T120000Z-1",
            RunStarted = Now,
        };
        var children = new RecordingCommandRunner().Script(IsPreview, RecordingCommandRunner.Exited(0, ChildPreview()));
        var commands = new ActionCommands(action, children, target, []) { Self = () => new SelfBinaryResult.Found(Installed) };
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var reason = new[] { preview.Skip, preview.Refusal }.FirstOrDefault(r => r.Length > 0, string.Empty);

        return JsonSerializer.Serialize(new ActionOutcome(action.Id.Text, action.Summary, ActionStatus.Previewed, reason, preview, null), WslCareJsonContext.Default.ActionOutcome);
    }

    private static bool IsPreview(IReadOnlyList<string> argv) => argv.Count > 6 && argv[5] == "archive" && argv[6] == "preview";

    private static EffectiveConfig Config() =>
        ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "baseFolder": "{{Base}}" } }"""))),
        ]).Config;

    /// <summary>The child's own answer: one agent, one session due (two files, 200 bytes) — the archive-preview golden's figures.</summary>
    private static string ChildPreview() =>
        JsonSerializer.Serialize(
            new ArchivePreviewReport(SchemaVersion.Current, "wsl", "wsl-host-distro", Now, "UTC", Base, new InUseReport("complete", 0, 0, string.Empty), [
                new AgentPreviewReport("claude-code", "Claude Code", true, "/home/user/.claude", new ArchiveRetentionReport("claude-settings", true, 30, "the documented default (30 days)", []), 14, 1, 2, 200, Now.AddDays(-20), 0, [], 0, string.Empty, [],
                    [new ArchiveUnitReport("session", "projects/p/s1.jsonl", 2, 200, Now.AddDays(-20), "1999/12")]),
            ]),
            WslCareJsonContext.Compact.ArchivePreviewReport);
}
