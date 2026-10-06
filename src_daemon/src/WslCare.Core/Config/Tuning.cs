using WslCare.Core.Files;

namespace WslCare.Core.Config;

/// <summary>
/// E7.S2c: how a configured number reaches the code that uses it — the process's effective configuration, read through typed
/// accessors (<see cref="Seconds"/>, <see cref="Bytes"/> …), so a call site names its KEY, never a literal. A process sets its
/// tuning once, after its configuration is loaded (<see cref="ForThisProcess"/>, <c>Program.Main</c>'s second phase); a test
/// scopes its own with <see cref="Use"/> (an <see cref="AsyncLocal{T}"/>, so tests running in parallel never see each
/// other's); everything else — and every call before the load — reads the embedded defaults.
/// </summary>
/// <remarks>Why ambient rather than passed: the numbers reach static command templates, the policy's catalogue and the
/// collectors' constants — places no configuration object reaches today. One process, one configuration: the ambient value
/// is that configuration, set once.</remarks>
public sealed class Tuning(EffectiveConfig config)
{
    private static readonly AsyncLocal<Tuning?> Scoped = new();
    private static readonly Lazy<Tuning> Defaults = new(() => new(ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config));
    private const long BytesPerMebibyte = 1L << 20;
    private const long BytesPerGibibyte = 1L << 30;
    private static Tuning? _process;

    /// <summary>The embedded defaults: today's values (behaviour unchanged).</summary>
    public static Tuning Default => Defaults.Value;

    /// <summary>A test's scope, else the process's configuration, else the defaults.</summary>
    public static Tuning Current => Scoped.Value ?? _process ?? Default;

    /// <summary>The process's configuration, set once after it is loaded.</summary>
    public static void ForThisProcess(EffectiveConfig config) => _process = new(config);

    /// <summary>A scope in which <paramref name="config"/> is <see cref="Current"/> — the test's own flow only.</summary>
    public static IDisposable Use(EffectiveConfig config)
    {
        var before = Scoped.Value;
        Scoped.Value = new(config);
        return new Restore(() => Scoped.Value = before);
    }

    public EffectiveConfig Config => config;

    public int Int(ConfigKey.IntKey key) => config.Int(key);

    public long Bytes(ConfigKey.IntKey key) => config.Int(key);

    /// <summary>A key in mebibytes, as bytes.</summary>
    public long Mebibytes(ConfigKey.IntKey key) => config.Int(key) * BytesPerMebibyte;

    /// <summary>A key in gibibytes, as bytes.</summary>
    public long Gibibytes(ConfigKey.IntKey key) => config.Int(key) * BytesPerGibibyte;

    /// <summary>A key's value as a sentence says it (E7.S2c, review N-6: a sentence derives its number from the key).</summary>
    public string Text(ConfigKey.IntKey key) => config.Int(key).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public TimeSpan Milliseconds(ConfigKey.IntKey key) => TimeSpan.FromMilliseconds(config.Int(key));

    public TimeSpan Seconds(ConfigKey.IntKey key) => TimeSpan.FromSeconds(config.Int(key));

    public TimeSpan Minutes(ConfigKey.IntKey key) => TimeSpan.FromMinutes(config.Int(key));

    public TimeSpan Hours(ConfigKey.IntKey key) => TimeSpan.FromHours(config.Int(key));

    public TimeSpan Days(ConfigKey.IntKey key) => TimeSpan.FromDays(config.Int(key));

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
