using WslCare.Core.Config;

namespace WslCare.Core.Actions;

/// <summary>
/// One action of plan §5, named as its <c>auto</c> switch is (<c>A1</c>…<c>A17</c>, and <c>A5Testcontainers</c> /
/// <c>A6Unused</c>, the second switches plan §5 gives A5 and A6 — the same names the cleanup rows carry). A CLOSED set,
/// derived from <see cref="ConfigKeys"/> so an id and its switch cannot drift apart; user text becomes an id only through
/// <see cref="Parse"/>, which names the legal values when it refuses.
/// <para><b>Reserved:</b> no action may be named <c>collect</c> (in any case) — it is the meta name of a full check
/// (<see cref="Records.RunKinds.FullCheckName"/>): a request's and <c>running.json</c>'s marker that the run is a full check,
/// never an action (plan §15o; <c>RunKindTests</c> and <c>ContractFilesTests</c> hold it over <see cref="All"/> and
/// <c>contracts/actions.json</c>).</para>
/// </summary>
public sealed record ActionId
{
    private const string AutoPrefix = "auto.";

    private ActionId(string text, TimerSwitch timer)
    {
        Text = text;
        Timer = timer;
    }

    public string Text { get; }

    /// <summary>Whether and how the TIMER may run it: its own <c>auto</c> switch (plan §5), or never — a button only.</summary>
    public TimerSwitch Timer { get; }

    /// <summary>The switch that lets the TIMER run it (plan §5: each action has its own <c>auto</c> switch). A button-only id has
    /// none — asking for it is a defect, so it throws, naming the id.</summary>
    public ConfigKey.BoolKey AutoSwitch => Timer is TimerSwitch.Auto auto
        ? auto.Key
        : throw new InvalidOperationException($"{Text} is a button only: it has no auto switch");

    /// <summary>A button only: no <c>auto</c> key, never selected by the timer, never run by it.</summary>
    public bool ButtonOnly => Timer is TimerSwitch.ButtonOnly;

    /// <summary>The ids that are a button only (plan §15q E7.S2b): A18 ends the target user's orphaned AI-agent processes, and an
    /// owner decision keeps it off the timer whatever any setting says.</summary>
    private static readonly IReadOnlyList<ActionId> ButtonOnlyIds =
    [
        new("A18", new TimerSwitch.ButtonOnly("A18 is a button only (plan §15q E7.S2b): the timer never ends an AI agent's process")),
    ];

    /// <summary>Every id: the <c>auto</c> switches' in the order of <see cref="ConfigKeys.All"/>, then the button-only ones.</summary>
    public static IReadOnlyList<ActionId> All { get; } =
    [
        .. ConfigKeys.All.OfType<ConfigKey.BoolKey>().Where(k => k.Name.StartsWith(AutoPrefix, StringComparison.Ordinal)).Select(k => new ActionId(k.Name[AutoPrefix.Length..], new TimerSwitch.Auto(k))),
        .. ButtonOnlyIds,
    ];

    /// <summary>
    /// The order a run takes actions in, whatever order they were asked for (plan §7.3: containers first frees their
    /// volumes and images — A5 → A4 → A6 → A7 → A8 → A9, the order of the 2026-10-02 run). Then the other caches,
    /// the journal and the archive; then trim and the clock; the memory actions last — build servers and suspects first,
    /// so A1 drops a cache the cleanups no longer refill, and A2 runs after A1 (plan §5).
    /// </summary>
    public static IReadOnlyList<ActionId> ExecutionOrder { get; } =
    [
        .. new[] { "A5Testcontainers", "A5", "A4", "A6", "A6Unused", "A7", "A8", "A9", "A12", "A14", "A17", "A10", "A13", "A15", "A16", "A3", "A11", "A18", "A1", "A2" }
            .Select(text => All.Single(id => id.Text == text)),
    ];

    /// <summary>The id <paramref name="text"/> names exactly, or <c>null</c>.</summary>
    public static ActionId? Find(string text) => All.FirstOrDefault(id => string.Equals(id.Text, text, StringComparison.Ordinal));

    /// <summary>A comma-separated list of ids (<c>A5,A4</c>) — each known, none twice; refused naming the legal values.</summary>
    public static ActionIdList Parse(string text)
    {
        var parts = text.Split(',');
        var unknown = parts.Where(p => Find(p) is null).ToList();
        if (unknown.Count > 0)
        {
            return new ActionIdList.Refused($"unknown action \"{string.Join(",", unknown)}\"; the actions are {string.Join(", ", All.Select(id => id.Text))}");
        }

        var ids = parts.Select(p => Find(p)!).ToList();
        return ids.Distinct().Count() == ids.Count
            ? new ActionIdList.Parsed(ids)
            : new ActionIdList.Refused($"an action is named twice in \"{text}\"");
    }

    public override string ToString() => Text;
}

/// <summary>How the timer may run an action: by its own <c>auto</c> switch, or never (a button only).</summary>
public abstract record TimerSwitch
{
    private TimerSwitch()
    {
    }

    public sealed record Auto(ConfigKey.BoolKey Key) : TimerSwitch;

    public sealed record ButtonOnly(string Why) : TimerSwitch;
}

/// <summary>What reading a list of action ids produced.</summary>
public abstract record ActionIdList
{
    private ActionIdList()
    {
    }

    public sealed record Parsed(IReadOnlyList<ActionId> Ids) : ActionIdList;

    public sealed record Refused(string Reason) : ActionIdList;
}
