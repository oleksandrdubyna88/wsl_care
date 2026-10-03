using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Config;

/// <summary>
/// Reads the three layers of plan §6 — embedded defaults, then the machine file, then the user
/// file, each overriding the last — validates every leaf against <see cref="ConfigKeys"/>, and
/// answers a <see cref="ConfigLoadResult"/>.
/// </summary>
/// <remarks>
/// <para>A missing machine or user file is normal. An unreadable one, one that is not JSON, an
/// unknown key, a wrong type or an out-of-range number are each a <see cref="ConfigError"/> naming
/// the file and the line; the run becomes observe-only (plan §15a #1) and the valid keys of that
/// same file still apply. Nothing here throws for a user's mistake.</para>
/// <para>The embedded defaults must be complete and valid; a test holds <c>default.json</c> to the
/// schema, and <see cref="EffectiveConfig"/> refuses to exist without every key.</para>
/// </remarks>
public static class ConfigLoader
{
    public const string DefaultsResource = "WslCare.Core.Config.default.json";

    /// <summary>The embedded layer's "file name" in error messages and the provenance column.</summary>
    public static readonly ConfigLayerFile DefaultsFile = new(ConfigLayer.Default, "<embedded default.json>");

    public static ConfigLoadResult Load(IHostPaths paths, IFileSystem files) => Load(paths, files, string.Empty);

    /// <summary>The three layers — with the user layer reported unreadable, naming <paramref name="userLayerProblem"/>, when
    /// whose layer it is cannot be told (E3.S2: root with an ambiguous target user). That is an ERROR, not a missing file:
    /// the run becomes observe-only rather than letting a default stand in for a setting the user may have changed.</summary>
    public static ConfigLoadResult Load(IHostPaths paths, IFileSystem files, string userLayerProblem) =>
        userLayerProblem.Length > 0
            ? Load(
            [
                (DefaultsFile, new FileReadResult.Content(EmbeddedDefaults())),
                (new ConfigLayerFile(ConfigLayer.Machine, paths.MachineConfigFile), files.ReadFile(paths.MachineConfigFile)),
                (new ConfigLayerFile(ConfigLayer.User, "<the target user's ~/.config/wsl-care/config.json>"), new FileReadResult.Unreadable(userLayerProblem)),
            ])
            : Load(
        [
            (DefaultsFile, new FileReadResult.Content(EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, paths.MachineConfigFile), files.ReadFile(paths.MachineConfigFile)),
            (new ConfigLayerFile(ConfigLayer.User, paths.UserConfigFile), files.ReadFile(paths.UserConfigFile)),
        ]);

    /// <summary>The pure half: layers already read, lowest precedence first.</summary>
    public static ConfigLoadResult Load(IReadOnlyList<(ConfigLayerFile File, FileReadResult Read)> layers)
    {
        var merged = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);
        var errors = new List<ConfigError>();
        foreach (var (file, read) in layers)
        {
            Apply(file, read, merged, errors);
        }

        var config = new EffectiveConfig(merged);
        return errors.Count == 0 ? new ConfigLoadResult.Valid(config) : new ConfigLoadResult.ObserveOnly(config, errors);
    }

    public static byte[] EmbeddedDefaults()
    {
        using var stream = typeof(ConfigLoader).Assembly.GetManifestResourceStream(DefaultsResource)
            ?? throw new InvalidOperationException($"embedded resource {DefaultsResource} is missing from the build");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Apply(ConfigLayerFile file, FileReadResult read, Dictionary<string, ConfigEntry> merged, List<ConfigError> errors)
    {
        switch (read)
        {
            case FileReadResult.Content content:
                ApplyDocument(file, ConfigDocument.Parse(content.Bytes), merged, errors);
                break;
            case FileReadResult.Unreadable unreadable:
                errors.Add(new ConfigError(file, 0, $"cannot be read: {unreadable.Reason}"));
                break;
        }
    }

    private static void ApplyDocument(ConfigLayerFile file, ConfigDocumentResult document, Dictionary<string, ConfigEntry> merged, List<ConfigError> errors)
    {
        switch (document)
        {
            case ConfigDocumentResult.Malformed malformed:
                errors.Add(new ConfigError(file, malformed.Line, malformed.Message));
                break;
            case ConfigDocumentResult.Parsed parsed:
                foreach (var entry in parsed.Entries)
                {
                    ApplyEntry(file, entry, merged, errors);
                }

                break;
        }
    }

    private static void ApplyEntry(ConfigLayerFile file, RawEntry entry, Dictionary<string, ConfigEntry> merged, List<ConfigError> errors)
    {
        var key = ConfigKeys.Find(entry.Key);
        if (key is null)
        {
            errors.Add(new ConfigError(file, entry.Line, $"unknown key \"{entry.Key}\" — \"wsl-care config get\" lists the keys this build knows"));
            return;
        }

        switch (ConfigValidation.Check(key, entry.Value))
        {
            case ValueCheck.Ok ok:
                merged[key.Name] = new ConfigEntry(key, ok.Value, file.Layer);
                break;
            case ValueCheck.Invalid invalid:
                errors.Add(new ConfigError(file, entry.Line, invalid.Message));
                break;
        }
    }
}
