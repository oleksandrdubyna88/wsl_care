using System.Text;
using System.Text.Json;

using WslCare.Core;
using WslCare.Core.Config;
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
        var key = ConfigKeys.Find(request.Key);
        if (key is null)
        {
            return Output.Refuse(stderr, $"unknown key \"{request.Key}\"; {UnknownKeyHint}.");
        }

        return ConfigValidation.Parse(key, request.Value) switch
        {
            ValueCheck.Invalid invalid => Output.Refuse(stderr, invalid.Message),
            ValueCheck.Ok ok => Report(new UserConfigWriter(host.Paths, host.Files, host.Clock).Set(key, ok.Value), key, host, stdout, stderr),
            _ => throw new System.Diagnostics.UnreachableException("ValueCheck is a closed set"),
        };
    }

    public static int Reset(Request.ConfigReset request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        var key = ConfigKeys.Find(request.Key);
        if (key is null)
        {
            return Output.Refuse(stderr, $"unknown key \"{request.Key}\"; {UnknownKeyHint}.");
        }

        return Report(new UserConfigWriter(host.Paths, host.Files, host.Clock).Reset(key), key, host, stdout, stderr);
    }

    private static int AnswerJson(ConfigLoadResult loaded, IReadOnlyList<ConfigEntry> entries, TextWriter stdout)
    {
        var report = new ConfigReport(
            SchemaVersion.Current,
            loaded.IsObserveOnly,
            [.. loaded.Errors.Select(ConfigErrorReport.From)],
            [.. entries.Select(ConfigValueReport.From)]);
        return Output.Answer(stdout, JsonSerializer.Serialize(report, WslCareJsonContext.Default.ConfigReport));
    }

    private static int AnswerText(ConfigLoadResult loaded, IReadOnlyList<ConfigEntry> entries, TextWriter stdout, TextWriter stderr)
    {
        foreach (var error in loaded.Errors)
        {
            Output.Note(stderr, $"config error: {error.Display}");
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
    private static int Report(UserConfigWriteResult result, ConfigKey key, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (result is UserConfigWriteResult.Refused refused)
        {
            // The user's config directory is never a protected place: reaching here is a defect in the
            // layout, or the directory could not be inspected or changed under the write (Unresolvable,
            // PathChanged). The reason names paths, which Output keeps on one clean line.
            return Output.Internal(stderr, $"the deletion policy refused the user config file: {refused.Verdict.Reason}");
        }

        var written = (UserConfigWriteResult.Written)result;
        NoteRepairs(written, stderr);
        var effective = ConfigLoader.Load(host.Paths, host.Files).Config.Entry(key);
        return Output.Answer(stdout, Line(effective, effective.Key.Name.Length));
    }

    private static void NoteRepairs(UserConfigWriteResult.Written written, TextWriter stderr)
    {
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
