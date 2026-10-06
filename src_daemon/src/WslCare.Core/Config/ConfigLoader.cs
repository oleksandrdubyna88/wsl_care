using System.Security.Cryptography;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Config;

/// <summary>
/// Whose user layer this process reads and how far it trusts it (plan §15q R1): the account that must own the file, whether
/// a ROOT run reads it (then root-effective keys marked <see cref="KeyTrust.TightenOnlyForRoot"/> only tighten), and — when
/// user→root is a real boundary (WSL interop disabled) — why no user value may LOOSEN a root-effective key.
/// </summary>
/// <param name="Owner">The uid the user layer must be owned by: the target user's for root, this process's own otherwise.</param>
/// <param name="ForRoot">A root run reads another account's layer.</param>
/// <param name="LoosenRefused">Non-empty: every root-effective user value is applied only in its safe direction, and this
/// sentence says why (R1.2, review M3).</param>
/// <param name="Skipped">Non-empty: the user layer is not read at all, and this says why (an ambiguous target user).</param>
public sealed record UserLayerTrust(uint Owner, bool ForRoot, string LoosenRefused, string Skipped)
{
    /// <summary>E7.S0 review C2: how the ROOT timer reads this same layer, when this process is not root inside the distro —
    /// its notices are added ("the root timer ignores this value: …") without changing this run's own configuration.</summary>
    public UserLayerTrust? RootTimerReads { get; init; }

    /// <summary>This process's own layer, fully trusted — every unprivileged run.</summary>
    public static UserLayerTrust OwnLayer() => new(RegularFiles.EffectiveUid(), false, string.Empty, string.Empty);
}

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
/// <para><b>Plan §15q R1.1:</b> the machine layer is read as root's own file (<see cref="IFileSystem.ReadStateFile"/>) and
/// the user layer as a file of its owner (<see cref="IFileSystem.ReadUserFile"/>) — regular, nonblocking, at most
/// <see cref="MaxLayerBytes"/>, never through a link, owner-checked — so a FIFO, a link or a group-writable file is an error
/// naming why, never a hang or a followed link. A user value a root run may not take (a machine-only key, a loosening it does
/// not trust) is a <see cref="ConfigNotice"/>: ignored, said, and not an error.</para>
/// <para>The embedded defaults must be complete and valid; a test holds <c>default.json</c> to the
/// schema, and <see cref="EffectiveConfig"/> refuses to exist without every key.</para>
/// </remarks>
public static class ConfigLoader
{
    public const string DefaultsResource = "WslCare.Core.Config.default.json";

    /// <summary>The most a configuration layer may hold (plan §15q growth table).</summary>
    /// <summary>The machine layer's bootstrap cap — the range maximum of <c>config.maxLayerBytes</c> (E7.S2c: the machine layer is read
    /// before any key is known); the user layer is read with the key's effective value.</summary>
    public static int MaxLayerBytes => ConfigKeys.ConfigLayerLimits.MaxLayerBytes.Max;

    /// <summary>The embedded layer's "file name" in error messages and the provenance column.</summary>
    public static readonly ConfigLayerFile DefaultsFile = new(ConfigLayer.Default, "<embedded default.json>");

    public static ConfigLoadResult Load(IHostPaths paths, IFileSystem files) => Load(paths, files, UserLayerTrust.OwnLayer());

    /// <summary>The three layers — or, when <paramref name="trust"/> names why the user layer is skipped (root with an ambiguous
    /// target user), the embedded defaults and the machine layer only. That is not an error: machine-scoped actions still run,
    /// and every user-scoped one is refused by the engine's target-user gate (plan §15c #2, gate finding #2).</summary>
    public static ConfigLoadResult Load(IHostPaths paths, IFileSystem files, UserLayerTrust trust)
    {
        var machine = MachineLayer(paths, files);
        if (trust.Skipped.Length > 0)
        {
            return Load([(DefaultsFile, new FileReadResult.Content(EmbeddedDefaults())), machine], trust);
        }

        var userCap = Load([(DefaultsFile, new FileReadResult.Content(EmbeddedDefaults())), machine], trust).Config.Int(ConfigKeys.ConfigLayerLimits.MaxLayerBytes);
        var user = Explained(files.ReadUserFile(paths.UserConfigFile, userCap, trust.Owner, paths.Home), paths.UserConfigFile, userCap);
        return Load([(DefaultsFile, new FileReadResult.Content(EmbeddedDefaults())), machine, (new ConfigLayerFile(ConfigLayer.User, paths.UserConfigFile), user)], trust)
            with
        { UserLayerDigest = Digest(user) };
    }

    /// <summary>The machine layer as read (with its bootstrap cap) and explained.</summary>
    public static (ConfigLayerFile File, FileReadResult Read) MachineLayer(IHostPaths paths, IFileSystem files) =>
        (new ConfigLayerFile(ConfigLayer.Machine, paths.MachineConfigFile), Explained(files.ReadStateFile(paths.MachineConfigFile, MaxLayerBytes), paths.MachineConfigFile, MaxLayerBytes));

    /// <summary>The user layer's cap in force: <c>config.maxLayerBytes</c> of the defaults and the machine layer (the writer and the
    /// reader keep ONE cap, E7.S2b/S2c review C-M4).</summary>
    public static int UserLayerCap(IHostPaths paths, IFileSystem files) =>
        Load([(DefaultsFile, new FileReadResult.Content(EmbeddedDefaults())), MachineLayer(paths, files)]).Config.Int(ConfigKeys.ConfigLayerLimits.MaxLayerBytes);

    /// <summary>The pure half, for this process's own layer: layers already read, lowest precedence first.</summary>
    public static ConfigLoadResult Load(IReadOnlyList<(ConfigLayerFile File, FileReadResult Read)> layers) => Load(layers, UserLayerTrust.OwnLayer());

    /// <summary>The pure half: layers already read, lowest precedence first, the user layer applied as <paramref name="trust"/> says.</summary>
    public static ConfigLoadResult Load(IReadOnlyList<(ConfigLayerFile File, FileReadResult Read)> layers, UserLayerTrust trust)
    {
        var state = new LoadState(new Dictionary<string, ConfigEntry>(StringComparer.Ordinal), [], [], trust);
        for (var i = 0; i < layers.Count; i++)
        {
            var before = new Dictionary<string, ConfigEntry>(state.Merged, StringComparer.Ordinal);
            Apply(layers[i].File, layers[i].Read, state);
            if (i > 0)
            {
                HoldRules(layers[i].File, before, state);
            }
        }

        var config = new EffectiveConfig(state.Merged);
        ConfigLoadResult result = state.Errors.Count == 0 ? new ConfigLoadResult.Valid(config) : new ConfigLoadResult.ObserveOnly(config, state.Errors);
        return result with { Notices = [.. state.Notices, .. RootTimerNotices(layers, trust, state.Notices)] };
    }

    /// <summary>
    /// E7.S2b/S2c review C-M1, C-M2: the coupled rules (<see cref="NumberRules"/>) held after each layer — a rule this layer breaks
    /// takes back the keys THIS layer set for it (to the layer below), so a contradiction is never in force, and is said against this
    /// layer: from the machine layer an error (observe-only), from the user layer a notice (the value not taken; root stays able).
    /// </summary>
    private static void HoldRules(ConfigLayerFile file, IReadOnlyDictionary<string, ConfigEntry> before, LoadState state)
    {
        var said = new HashSet<NumberRules.Rule>();
        for (var round = 0; round <= NumberRules.Rules.Count; round++)
        {
            var config = new EffectiveConfig(state.Merged);
            var broken = NumberRules.Broken(config);
            if (broken.Count == 0)
            {
                return;
            }

            foreach (var rule in broken.Where(said.Add))
            {
                TakeBack(file, rule, config, before, state);
            }
        }
    }

    private static void TakeBack(ConfigLayerFile file, NumberRules.Rule rule, EffectiveConfig config, IReadOnlyDictionary<string, ConfigEntry> before, LoadState state)
    {
        var set = rule.Keys.Where(k => state.Merged[k.Name].Layer == file.Layer && before.ContainsKey(k.Name)).Distinct().ToList();
        foreach (var key in set)
        {
            state.Merged[key.Name] = before[key.Name];
        }

        var message = set.Count == 0 ? rule.Says(config) : $"{rule.Says(config)} — not taken from this layer: {string.Join(", ", set.Select(k => k.Name))}";
        if (file.Layer == ConfigLayer.User)
        {
            state.Notices.Add(new ConfigNotice(file, 0, (set.FirstOrDefault() ?? rule.Keys[0]).Name, message));
        }
        else
        {
            state.Errors.Add(new ConfigError(file, 0, message));
        }
    }

    /// <summary>What the root timer would not take from this layer and this run did (E7.S0 review C2), each said once.</summary>
    private static IEnumerable<ConfigNotice> RootTimerNotices(IReadOnlyList<(ConfigLayerFile File, FileReadResult Read)> layers, UserLayerTrust trust, IReadOnlyList<ConfigNotice> own) =>
        trust.RootTimerReads is { } root
            ? Load(layers, root with { RootTimerReads = null }).Notices
                .Where(n => !own.Any(o => o.Key == n.Key))
                .Select(n => n with { Message = $"the root timer ignores this value: {n.Message}" })
            : [];

    public static byte[] EmbeddedDefaults()
    {
        using var stream = typeof(ConfigLoader).Assembly.GetManifestResourceStream(DefaultsResource)
            ?? throw new InvalidOperationException($"embedded resource {DefaultsResource} is missing from the build");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record LoadState(Dictionary<string, ConfigEntry> Merged, List<ConfigError> Errors, List<ConfigNotice> Notices, UserLayerTrust Trust);

    /// <summary>A refusal of the hardened reader with how to fix it (E7.S0 review C3) — every refusal, not only one.</summary>
    private static FileReadResult Explained(FileReadResult read, string path, int cap) =>
        read is FileReadResult.Unreadable { Reason: var reason } ? new FileReadResult.Unreadable($"{reason}; {FixFor(reason, path, cap)}") : read;

    private static string FixFor(string reason, string path, int cap) => reason switch
    {
        _ when reason.Contains("writable by group or others", StringComparison.Ordinal) => $"run chmod go-w {path} (config set writes it 0644)",
        _ when reason.Contains("link", StringComparison.Ordinal) => "replace the link with a regular file — config set does that, keeping the values it can read; root never follows a link",
        _ when reason.Contains("owned by uid", StringComparison.Ordinal) => "run config set as the account that owns this home, or give the file to that account (chown)",
        _ when reason.Contains("larger than", StringComparison.Ordinal) => $"this layer holds at most {cap} bytes (config.maxLayerBytes in force): remove what is not a setting",
        _ when reason.Contains(RegularFiles.NotRegular, StringComparison.Ordinal) => "remove it; config set writes a regular file in its place",
        _ => "fix or remove the file; config set rewrites it",
    };

    /// <summary>The SHA-256 of the user layer as read (plan §15q R1.7: <c>status</c> carries it so an outside change is noticed);
    /// empty when there is no readable layer.</summary>
    private static string Digest(FileReadResult read) =>
        read is FileReadResult.Content content ? Convert.ToHexStringLower(SHA256.HashData(content.Bytes)) : string.Empty;

    private static void Apply(ConfigLayerFile file, FileReadResult read, LoadState state)
    {
        switch (read)
        {
            case FileReadResult.Content content:
                ApplyDocument(file, ConfigDocument.Parse(content.Bytes), state);
                break;
            case FileReadResult.Unreadable unreadable:
                state.Errors.Add(new ConfigError(file, 0, $"cannot be read: {unreadable.Reason}"));
                break;
        }
    }

    private static void ApplyDocument(ConfigLayerFile file, ConfigDocumentResult document, LoadState state)
    {
        switch (document)
        {
            case ConfigDocumentResult.Malformed malformed:
                state.Errors.Add(new ConfigError(file, malformed.Line, malformed.Message));
                break;
            case ConfigDocumentResult.Parsed parsed:
                foreach (var entry in parsed.Entries)
                {
                    ApplyEntry(file, entry, state);
                }

                break;
        }
    }

    private static void ApplyEntry(ConfigLayerFile file, RawEntry entry, LoadState state)
    {
        var key = ConfigKeys.Find(entry.Key);
        if (key is null)
        {
            state.Errors.Add(new ConfigError(file, entry.Line, $"unknown key \"{entry.Key}\" — \"wsl-care config get\" lists the keys this build knows"));
            return;
        }

        if (file.Layer == ConfigLayer.User && key.Trust.MachineOnly)
        {
            // E7.S0 review C4: decided before validation — an old, invalid value here must not make the whole run observe-only.
            state.Notices.Add(new ConfigNotice(file, entry.Line, key.Name, MachineOnlyNotice(key)));
            return;
        }

        switch (ConfigValidation.Check(key, entry.Value))
        {
            case ValueCheck.Ok ok:
                Take(file, key, ok.Value, entry.Line, state);
                break;
            case ValueCheck.Invalid invalid:
                state.Errors.Add(new ConfigError(file, entry.Line, invalid.Message));
                break;
        }
    }

    /// <summary>A valid value enters the merge — unless it is a user value the trust does not take (plan §15q R1.2, R1.3, R1.6),
    /// which is a notice instead.</summary>
    private static void Take(ConfigLayerFile file, ConfigKey key, ConfigValue value, int line, LoadState state)
    {
        var refusal = file.Layer == ConfigLayer.User ? UserValueRefusal(file, key, value, state) : string.Empty;
        if (refusal.Length > 0)
        {
            state.Notices.Add(new ConfigNotice(file, line, key.Name, refusal));
            return;
        }

        state.Merged[key.Name] = new ConfigEntry(key, value, file.Layer);
    }

    /// <summary>Why a user-layer value is not taken; empty when it is.</summary>
    private static string UserValueRefusal(ConfigLayerFile file, ConfigKey key, ConfigValue value, LoadState state) =>
        key.Trust.MachineOnly ? MachineOnlyNotice(key)
        : TightenOnly(key, state.Trust) is { Length: > 0 } why && !IsNoLooser(key, value, state) ? Ignored(file, key, value, why, state)
        : string.Empty;

    /// <summary>coai E7 code round #8: the long interop explanation is said ONCE, as a notice without a key; each key's notice carries
    /// the short fact. Any other reason is short and stays with its key.</summary>
    private static string Ignored(ConfigLayerFile file, ConfigKey key, ConfigValue value, string why, LoadState state)
    {
        if (why != state.Trust.LoosenRefused)
        {
            return $"{key.Name} = {value.Describe()} is ignored: {why}";
        }

        if (!state.Notices.Any(n => n.Key.Length == 0))
        {
            state.Notices.Add(new ConfigNotice(file, 0, string.Empty, why));
        }

        return $"{key.Name} = {value.Describe()} is ignored: it is looser than the layers below, and this layer is taken only in the safe direction (why: the notice without a key)";
    }

    private static string MachineOnlyNotice(ConfigKey key) =>
        $"{key.Name} is set only in the machine layer (/etc/wsl-care/config.json); the user value is ignored";

    /// <summary>Why this key's user value may only tighten; empty when it may move either way.</summary>
    private static string TightenOnly(ConfigKey key, UserLayerTrust trust) =>
        !key.Trust.RootEffective ? string.Empty
        : trust.LoosenRefused.Length > 0 ? trust.LoosenRefused
        : RootsOwnKeyRule(key, trust);

    /// <summary>R1.6: a key root keeps for itself (its audit log) only tightens from another account's layer.</summary>
    private static string RootsOwnKeyRule(ConfigKey key, UserLayerTrust trust) =>
        trust.ForRoot && key.Trust.TightenOnlyForRoot
            ? "a root run takes this key from the user layer only when it is no looser than the layers below"
            : string.Empty;

    private static bool IsNoLooser(ConfigKey key, ConfigValue value, LoadState state) =>
        !state.Merged.TryGetValue(key.Name, out var below) || KeySafety.IsNoLooser(key, value, below.Value);
}
