using System.Text;
using System.Text.Json;

using WslCare.Core;
using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>config get</c>, <c>config set</c>, <c>config reset</c> (plan §6, §15a #1): the validated read
/// and write of the user layer, the only interface the extension's settings use.
/// </summary>
internal static class ConfigCommand
{
    private const string UnknownKeyHint = "\"wsl-care config get\" lists the keys this build knows";

    public static int Get(Request.ConfigGet request, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        var key = request.Key.Length == 0 ? null : ConfigKeys.Find(request.Key);
        if (request.Key.Length > 0 && key is null)
        {
            return Output.Refuse(stderr, $"unknown key \"{request.Key}\"; {UnknownKeyHint}.");
        }

        var entries = key is null ? loaded.Config.Entries : [loaded.Config.Entry(key)];
        return request.Json ? AnswerJson(loaded, entries, stdout) : AnswerText(loaded, entries, stdout, stderr);
    }

    public static int Set(Request.ConfigSet request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (RootForTheUser(host) is { } refusal)
        {
            return NotAsRoot(stderr, refusal);
        }

        var key = ConfigKeys.Find(request.Key);
        return key is null
            ? Output.Refuse(stderr, $"unknown key \"{request.Key}\"; {UnknownKeyHint}.")
            : SetKnown(key, request.Value, host, stdout, stderr);
    }

    /// <summary>A key the register knows: refused when only the machine layer may set it (plan §15q R1.3, review B1 — a value
    /// root writes into is set by root until its reader validates it), else validated and written.</summary>
    private static int SetKnown(ConfigKey key, string value, CliHost host, TextWriter stdout, TextWriter stderr) =>
        key switch
        {
            { Trust.MachineOnly: true } => Output.Refuse(stderr, $"{key.Name} is set only in the machine layer (/etc/wsl-care/config.json, as root); the user layer would be ignored. Nothing was written."),
            ConfigKey.AgentListKey agents => SetAgents(agents, value, host, stdout, stderr),
            _ => Checked(key, ConfigValidation.Parse(key, value), host, stdout, stderr),
        };

    private static int Checked(ConfigKey key, ValueCheck check, CliHost host, TextWriter stdout, TextWriter stderr) => check switch
    {
        ValueCheck.Invalid invalid => Output.Refuse(stderr, invalid.Message),
        ValueCheck.Ok ok => Report(new UserConfigWriter(host.Paths, host.Files, host.Clock).Set(key, ok.Value), key, host, stdout, stderr),
        _ => throw new System.Diagnostics.UnreachableException("ValueCheck is a closed set"),
    };

    /// <summary><c>config set aiAgents.extra -</c> (plan §15q D4, R2.1): the list is read from STDIN only (compact JSON, the stdin
    /// cap and ceiling of <see cref="StdinList"/>), its shape checked, and every entry of this side judged against the disk as
    /// this user — the first refusal names its entry and rule, and nothing is written.</summary>
    private static int SetAgents(ConfigKey.AgentListKey key, string value, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (value != "-")
        {
            return Output.Refuse(stderr, $"{key.Name} is read from stdin: wsl-care config set {key.Name} - < agents.json (a JSON list, at most {StdinList.MaxBytes} bytes). Nothing was written.");
        }

        return StdinList.Read(host.StandardInput(), StdinList.MaxBytes, host.StdinCeiling) switch
        {
            FileReadResult.Content content => Judged(key, ConfigValidation.Parse(key, System.Text.Encoding.UTF8.GetString(content.Bytes)), host, stdout, stderr),
            FileReadResult.Unreadable unreadable => Output.Refuse(stderr, $"{key.Name}: stdin {unreadable.Reason}. Nothing was written."),
            _ => Output.Refuse(stderr, $"{key.Name}: nothing arrived on stdin. Nothing was written."),
        };
    }

    private static int Judged(ConfigKey key, ValueCheck check, CliHost host, TextWriter stdout, TextWriter stderr) =>
        check is ValueCheck.Ok { Value: ConfigValue.AgentList list } && FirstRefusal(list.Agents, host) is { Length: > 0 } refusal
            ? Output.Refuse(stderr, $"{key.Name}: {refusal}. Nothing was written.")
            : Checked(key, check, host, stdout, stderr);

    /// <summary>The distro's entries judged as this user sees the disk (plan §15q R2.1); the Windows binary judges none yet (E7.S5b).</summary>
    private static string FirstRefusal(IReadOnlyList<ExtraAgent> agents, CliHost host) =>
        host.Paths is LinuxHostPaths linux
            ? ExtraAgentRules.Judge(linux, host.Files, [.. agents.Where(a => a.Side == ExtraAgentShape.Wsl)], ExtraAgentRules.CleanupRoots(host.Actions, linux.Home, linux.Rules))
                .Where(j => !j.Accepted).Select(j => $"\"{j.Agent.Name}\": {j.Refusal}").FirstOrDefault() ?? string.Empty
            : string.Empty;

    public static int Reset(Request.ConfigReset request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (RootForTheUser(host) is { } refusal)
        {
            return NotAsRoot(stderr, refusal);
        }

        var key = ConfigKeys.Find(request.Key);
        if (key is null)
        {
            return Output.Refuse(stderr, $"unknown key \"{request.Key}\"; {UnknownKeyHint}.");
        }

        return Report(new UserConfigWriter(host.Paths, host.Files, host.Clock).Reset(key), key, host, stdout, stderr, reset: true);
    }

    /// <summary>Root working for the target user (plan §15c #2, E3.S2) must not write that user's layer: a root-owned file in their
    /// home would lock them out of their own settings. The panel writes the layer as the user, unprivileged.</summary>
    /// <summary>E7.S1/S2 review round: its own exit code (81), so a client can tell "the distribution's default user is root" from a
    /// refused value (2) — the extension reverts a setting on 2 only.</summary>
    private static int NotAsRoot(TextWriter stderr, string refusal)
    {
        Output.Note(stderr, refusal);
        return (int)ExitCode.NotAsRoot;
    }

    private static string? RootForTheUser(CliHost host) =>
        host.HomeOwner is Core.Actions.HomeOwner.Target target
            ? $"config set and config reset write the user layer of {target.User.Name}; run them as {target.User.Name}, not as root (a root-owned file in their home would lock them out of it). Nothing was written."
            : null;

    private static int AnswerJson(ConfigLoadResult loaded, IReadOnlyList<ConfigEntry> entries, TextWriter stdout)
    {
        var report = new ConfigReport(
            SchemaVersion.Current,
            loaded.IsObserveOnly,
            [.. loaded.Errors.Select(ConfigErrorReport.From)],
            [.. entries.Select(ConfigValueReport.From)])
        {
            ConfigNotices = ConfigNoticeReport.Of(loaded),
        };
        return Output.Answer(stdout, JsonSerializer.Serialize(report, WslCareJsonContext.Default.ConfigReport));
    }

    private static int AnswerText(ConfigLoadResult loaded, IReadOnlyList<ConfigEntry> entries, TextWriter stdout, TextWriter stderr)
    {
        foreach (var error in loaded.Errors)
        {
            Output.Note(stderr, $"config error: {error.Display}");
        }

        foreach (var notice in loaded.Notices)
        {
            Output.Note(stderr, $"config notice: {notice.Display}");
        }

        if (loaded.IsObserveOnly)
        {
            Output.Note(stderr, "observe-only: the daemon collects and reports but does not act until the layer above is fixed; \"config set\" still writes the user layer.");
        }

        var width = entries.Max(e => e.Key.Name.Length);
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            text.AppendLine(Line(entry, width));
        }

        return Output.Answer(stdout, text.ToString().TrimEnd('\r', '\n'));
    }

    private static string Line(ConfigEntry entry, int width) =>
        $"{entry.Key.Name.PadRight(width)} = {entry.Value.Describe(),-24} ({LayerName(entry.Layer)})";

    private static string LayerName(ConfigLayer layer) => layer switch
    {
        ConfigLayer.Default => "default",
        ConfigLayer.Machine => "machine",
        _ => "user",
    };

    /// <summary>After a write: the notes about the repair, then the key's effective value, re-read from disk.</summary>
    private static int Report(UserConfigWriteResult result, ConfigKey key, CliHost host, TextWriter stdout, TextWriter stderr, bool reset = false)
    {
        if (result is UserConfigWriteResult.TooLarge large)
        {
            return Output.Refuse(stderr, $"{key.Name}: the user layer would be {large.Bytes} bytes, over the {large.Max}-byte cap its reader keeps (non-ASCII text is written escaped, six bytes a character). Nothing was written.");
        }

        if (result is UserConfigWriteResult.BreaksRule broken)
        {
            return Output.Refuse(stderr, $"{key.Name}: {broken.Message}. Nothing was written.");
        }

        if (result is UserConfigWriteResult.Refused refused)
        {
            // The user's config directory is never a protected place: reaching here is a defect in the
            // layout, or the directory could not be inspected or changed under the write (Unresolvable,
            // PathChanged). The reason names paths, which Output keeps on one clean line.
            return Output.Internal(stderr, $"the deletion policy refused the user config file: {refused.Verdict.Reason}");
        }

        var written = (UserConfigWriteResult.Written)result;
        NoteRepairs(written, stderr);
        NoteOutcome(written, key, reset, stderr);
        var effective = ConfigLoader.Load(host.Paths, host.Files).Config.Entry(key);
        return Output.Answer(stdout, Line(effective, effective.Key.Name.Length));
    }

    /// <summary>Retro gate over PR #4: the timer switched dry by a lossy repair, and a reset that had nothing to remove — both
    /// facts the writer knew and the person was never told.</summary>
    private static void NoteOutcome(UserConfigWriteResult.Written written, ConfigKey key, bool reset, TextWriter stderr)
    {
        if (written.PinnedDryRun)
        {
            Output.Note(stderr, "dryRun was set to true in the user layer: the repair discarded settings it could not read, so the timer only previews until you check your settings and run: wsl-care config set dryRun false");
        }

        // Absence is KNOWN only for a layer that was read; what an unparseable one held is not (fix-PR code round).
        if (reset && !written.KeyWasPresent && written.MovedAsideTo.Length == 0)
        {
            Output.Note(stderr, $"{key.Name} was not set in the user layer; nothing was removed.");
        }
    }

    private static void NoteRepairs(UserConfigWriteResult.Written written, TextWriter stderr)
    {
        if (written.ReplacedLink)
        {
            Output.Note(stderr, "the user layer was a link, which root never follows; it was replaced by a regular file holding the values read through it (the file it pointed at is untouched).");
        }

        if (written.MovedAsideTo.Length > 0)
        {
            Output.Note(stderr, $"the user layer could not be parsed; it was moved to {written.MovedAsideTo} and a valid file written in its place.");
        }

        if (written.DroppedKeys.Count > 0)
        {
            Output.Note(stderr, $"dropped from the user layer because they did not validate: {string.Join(", ", written.DroppedKeys)}.");
        }
    }
}
