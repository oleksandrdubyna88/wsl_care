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
/// E10.S2b (plan §15s, *E10.S2b build notes*): A20's line of an <c>act A20 --preview --entry - --json</c> answer when the panel showed
/// one archived entry that is verified and removed at its source — the <c>shown</c> list the extension holds and pipes back on the
/// confirm, and the per-entry items its modal names. The built CLI cannot answer it in a scenario: its A20 refuses a product binary
/// that is not root's alone, which a test build never is (as <see cref="A13PreviewGolden"/> says for A13). So the daemon's OWN
/// <see cref="RestoreAction"/> previews over a child that answers the list, and the line is built as the engine builds a previewed
/// one (<c>ActionEngine.PreviewedAsync</c>: status <c>previewed</c>, the first of the skip / refusal as the reason, <c>shown</c> from the
/// action, <c>shownTruncated</c> only past the shown-list cap).
/// </summary>
internal static class A20PreviewGolden
{
    public const string File = "act-a20-preview-action.json";

    /// <summary>The entry the panel showed — the archive-list golden's entry id (its normalised value).</summary>
    public const string EntryId = "0123456789abcdef";

    private const string Base = "/mnt/v/ai-archive";
    private const string Installed = "/opt/wsl-care/bin/wsl-care";
    private static readonly DateTimeOffset Now = new(2000, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The line, indented as the CLI writes it.</summary>
    public static async Task<string> TextAsync()
    {
        using var sandbox = new LinuxSandbox("golden-a20");
        sandbox.Write("/proc/sys/kernel/random/boot_id", "6d1c1c5e-0000-4000-8000-0000000000a2\n");
        sandbox.Write("/etc/pam.d/runuser", "auth sufficient pam_rootok.so\nsession required pam_unix.so\n");
        var action = new RestoreAction();
        var target = new TargetUserResult.Found(new TargetUser("user", 1000, "/home/user"), "golden");
        var context = new ActionContext(sandbox.Paths, sandbox.Files, new ManualTimeProvider(Now), Config(), RunTrigger.Manual, target)
        {
            Processes = _ => Reading.Of(new ProcessSnapshot(0, 0, 0, 0, 0, [], [])),
            RunId = "20000115T120000Z-1",
            RunStarted = Now,
            ShownEntries = ShownList.Of([EntryId]),
        };
        var children = new RecordingCommandRunner().Script(IsList, RecordingCommandRunner.Exited(0, ChildList()));
        var commands = new ActionCommands(action, children, target, []) { Self = () => new SelfBinaryResult.Found(Installed) };
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var reason = new[] { preview.Skip, preview.Refusal }.FirstOrDefault(r => r.Length > 0, string.Empty);
        var line = new ActionOutcome(action.Id.Text, action.Summary, ActionStatus.Previewed, reason, preview, null)
        {
            Shown = preview.Available ? action.Shown(preview) : null,
            ShownTruncated = preview.Available && ShownList.Truncates(preview.Count) ? true : null,
        };

        return JsonSerializer.Serialize(line, WslCareJsonContext.Default.ActionOutcome);
    }

    private static bool IsList(IReadOnlyList<string> argv) => argv.Count > 6 && argv[5] == "archive" && argv[6] == "list";

    private static EffectiveConfig Config() =>
        ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "baseFolder": "{{Base}}" } }"""))),
        ]).Config;

    /// <summary>The child's own answer to <c>archive list --restorable --entry &lt;shown&gt; --json</c>: the one entry, verified and removed at
    /// its source (two files, 200 bytes — the archive-list golden's figures).</summary>
    private static string ChildList() =>
        JsonSerializer.Serialize(
            new ArchiveListReport(SchemaVersion.Current, "wsl", "wsl-host-distro", Base, RunOutcomes.Done, string.Empty, [
                new ArchiveListEntry(EntryId, "claude-code", "projects/p/s1.jsonl", "1999/12", ArchiveIndex.Events.SourceRemoved, true, 2, 200, Now.AddDays(-20)),
            ], 0, [])
            { RestoreCeiling = 1000 },
            WslCareJsonContext.Compact.ArchiveListReport);
}
