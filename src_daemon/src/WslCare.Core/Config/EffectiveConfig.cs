namespace WslCare.Core.Config;

/// <summary>
/// The merged configuration: every key of <see cref="ConfigKeys.All"/> with its value and the layer
/// that set it. Typed accessors, so a caller holds an <c>int</c> or a <c>bool</c> and never a
/// <see cref="ConfigValue"/> to unwrap.
/// </summary>
public sealed class EffectiveConfig
{
    private readonly IReadOnlyDictionary<string, ConfigEntry> _entries;

    public EffectiveConfig(IReadOnlyDictionary<string, ConfigEntry> entries)
    {
        var missing = ConfigKeys.All.Where(k => !entries.ContainsKey(k.Name)).Select(k => k.Name).ToList();
        if (missing.Count > 0)
        {
            // Only possible when the embedded defaults lack a key — a build defect, not a user error.
            throw new InvalidOperationException($"the effective configuration has no value for: {string.Join(", ", missing)}");
        }

        _entries = entries;
    }

    /// <summary>Every entry, in schema order.</summary>
    public IReadOnlyList<ConfigEntry> Entries => [.. ConfigKeys.All.Select(k => _entries[k.Name])];

    public ConfigEntry Entry(ConfigKey key) => _entries[key.Name];

    public bool Bool(ConfigKey.BoolKey key) => ((ConfigValue.Bool)_entries[key.Name].Value).Value;

    public int Int(ConfigKey.IntKey key) => ((ConfigValue.Int)_entries[key.Name].Value).Value;

    public string Text(ConfigKey.TextKey key) => ((ConfigValue.Text)_entries[key.Name].Value).Value;

    public IReadOnlyList<string> TextList(ConfigKey.TextListKey key) => ((ConfigValue.TextList)_entries[key.Name].Value).Values;

    public IReadOnlyList<Agents.ExtraAgent> Agents(ConfigKey.AgentListKey key) => ((ConfigValue.AgentList)_entries[key.Name].Value).Agents;
}

/// <summary>
/// What loading the layers produced (plan §15a #1): a valid configuration, or one the daemon may
/// only OBSERVE with — collect and report, never act — because a layer is unreadable or invalid.
/// </summary>
/// <remarks>In both cases <see cref="Config"/> is complete: an invalid layer contributes the keys that
/// did validate and nothing else, so a switched-off action stays off, and the broken parts are
/// listed in <see cref="Errors"/> with file and line. A default never silently replaces a user's
/// value; the whole point of observe-only is that a default can re-enable what the user turned off.</remarks>
public abstract record ConfigLoadResult
{
    private ConfigLoadResult()
    {
    }

    public abstract EffectiveConfig Config { get; }

    public abstract IReadOnlyList<ConfigError> Errors { get; }

    /// <summary>Valid user values this run did not take, each said (plan §15q R1.2, R1.3, R1.6).</summary>
    public IReadOnlyList<ConfigNotice> Notices { get; init; } = [];

    /// <summary>The SHA-256 of the user layer as read; empty when there was none (plan §15q R1.7).</summary>
    public string UserLayerDigest { get; init; } = string.Empty;

    /// <summary>Non-empty: the user layer was NOT read (root with no single target user) and this says why. The timer then runs
    /// no action at all (retro gate over PR #7): a switch the person turned off in that layer is invisible to root, and a
    /// default must never re-enable it (plan §15a #1). A button still runs — it previews and asks first.</summary>
    public string UserLayerSkipped { get; init; } = string.Empty;

    /// <summary>The FACT that the user layer was not read — what the engine's timer gate decides on; <see cref="UserLayerSkipped"/>
    /// is only its wording (fix-PR code round: a safety decision never hangs on presentation text).</summary>
    public bool UserLayerUnread { get; init; }

    public bool IsObserveOnly => this is ObserveOnly;

    public sealed record Valid(EffectiveConfig Config) : ConfigLoadResult
    {
        public override EffectiveConfig Config { get; } = Config;

        public override IReadOnlyList<ConfigError> Errors => [];
    }

    public sealed record ObserveOnly(EffectiveConfig Config, IReadOnlyList<ConfigError> Errors) : ConfigLoadResult
    {
        public override EffectiveConfig Config { get; } = Config;

        public override IReadOnlyList<ConfigError> Errors { get; } = Errors;
    }
}
