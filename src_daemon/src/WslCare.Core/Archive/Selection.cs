using System.Globalization;

using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Folders;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>One file of a unit: its path relative to the layout's folder, as this process sees it, its length and its last write.</summary>
public sealed record UnitFile(string Relative, string OnDisk, long Bytes, DateTimeOffset LastWriteUtc);

/// <summary>Why a unit is not taken although it may be due — a closed set, so a client can count by it.</summary>
public static class SkipRule
{
    public const string InUse = "in-use";
    public const string AgentWorkingHere = "agent-working-here";
    public const string MayBeOpen = "may-be-open";
    public const string Name = "name";
    public const string NeverMoved = "never-moved";
    public const string NotWhole = "not-whole";

    public static IReadOnlyList<string> All { get; } = [InUse, AgentWorkingHere, MayBeOpen, Name, NeverMoved, NotWhole];
}

/// <summary>One unit the archive would move as a whole (plan §15r D2.1): a session with its companions, or a file of its own.</summary>
/// <param name="Key">The unit's main file, relative to the layout's folder.</param>
/// <param name="NewestWriteUtc">The newest last write over ALL its files — what its age and its month are.</param>
/// <param name="Month">The month it goes under, <c>yyyy/MM</c>, in the side's time zone.</param>
/// <param name="SkipRule">Why it is not taken although due (<see cref="Archive.SkipRule"/>); empty when it is taken.</param>
/// <param name="Skip">The sentence.</param>
public sealed record UnitFound(string Kind, string Key, IReadOnlyList<UnitFile> Files, DateTimeOffset NewestWriteUtc, string Month, string SkipRule, string Skip)
{
    public long Bytes => Files.Sum(f => f.Bytes);
}

/// <summary>What the selection found for one agent.</summary>
/// <param name="EffectiveAgeDays">The age a unit is due at: <c>archive.olderThanDays</c>, shortened by the agent's measured retention
/// (§15r D10, review M7).</param>
/// <param name="Due">The due units that may move, oldest first.</param>
/// <param name="Skipped">The due units kept where they are, each with its rule.</param>
/// <param name="Younger">How many units are not due yet.</param>
/// <param name="Quarantined">Files an interrupted removal left under a quarantine name (review M3) — resolved by the run's reconcile.</param>
/// <param name="Note">What the listing could not see; empty when whole.</param>
public sealed record AgentSelection(AgentEntry Entry, string Under, RetentionFound Retention, int EffectiveAgeDays, IReadOnlyList<UnitFound> Due, IReadOnlyList<UnitFound> Skipped, int Younger, int Quarantined, string Note)
{
    /// <summary>Whether <c>archive.agents</c> holds it — an agent asked for by <c>--agent</c> alone is previewed but never moved (E9.S1
    /// review round m4).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The quarantined files themselves, relative to <see cref="Under"/> (E9.S2b: the reconcile renames them back).</summary>
    public IReadOnlyList<string> QuarantinedFiles { get; init; } = [];
}

/// <summary>Everything the selection reads, and when it is.</summary>
public sealed record SelectionInput(IHostPaths Paths, IFileSystem Files, EffectiveConfig Config, DateTimeOffset Now, TimeZoneInfo Zone, InUseView InUse, Func<string, string?> Environment)
{
    /// <summary>Only this agent (<c>archive preview --agent</c>); empty = every agent of <c>archive.agents</c>.</summary>
    public string OnlyAgent { get; init; } = string.Empty;

    /// <summary>The time the selection has left (E9.S1 review round m1: the listing AND every companion walk); unbounded by default.</summary>
    public Func<TimeSpan> TimeLeft { get; init; } = static () => TimeSpan.MaxValue;

    /// <summary>Whether the time is up.</summary>
    public bool OutOfTime => TimeLeft() <= TimeSpan.Zero;

    public CancellationToken Token { get; init; }
}

/// <summary>
/// Plan §15r D2.1–D2.2, E9.S1 — which units the archive would move, from listings and stats alone: no file is opened (the
/// agent's own retention setting is the one file read, <see cref="AgentRetentionReader"/>) and nothing is written. Per agent of
/// <c>archive.agents</c> whose layout this side has: every session of its layout with its companions — a companion folder
/// walked by the agent walk's rules (no link followed, <c>memory</c> never entered, the device kept) — and every file of a file
/// unit; each unit's NEWEST last write over all its files decides its age and its month; due when older than the effective age.
/// A due unit is kept where it is, with its rule, when a file of it is open, Claude Code works in its project, a
/// <c>skipWhilePresent</c> companion exists, a name cannot be held by NTFS or two differ by case only, a path names what never
/// moves (a session named <c>memory.jsonl</c> refused WHOLE), or its listing was cut. Oldest first.
/// </summary>
public static class Selection
{
    /// <summary>The agents asked for — <c>archive.agents</c> (or the one named): the catalogue's with an archive block, and the manual
    /// agents it names (<see cref="ArchiveTargets"/>).</summary>
    public static IReadOnlyList<AgentSelection> Select(SelectionInput input) =>
        [.. ArchiveTargets.Of(input.Paths, input.Files, input.Config, input.OnlyAgent, input.Environment).Select(target => Agent(input, target) with { Enabled = target.Enabled })];

    /// <summary>§15r D10: min(<c>archive.olderThanDays</c>, the measured retention − <c>archive.marginDays</c> − the removal's
    /// whole days), at least 1.</summary>
    public static int EffectiveAgeDays(EffectiveConfig config, RetentionFound retention)
    {
        var older = config.Int(ConfigKeys.Archive.OlderThanDays);
        return retention is RetentionFound.Known { Days: var days }
            ? Math.Max(1, Math.Min(older, days - config.Int(ConfigKeys.Archive.MarginDays) - RemovalDays(config)))
            : older;
    }

    private static int RemovalDays(EffectiveConfig config) =>
        (int)Math.Ceiling(TimeSpan.FromHours(config.Int(ConfigKeys.Archive.RemoveAfterHours)).TotalDays);

    private static AgentSelection Agent(SelectionInput input, ArchiveTarget target)
    {
        var (entry, under) = (target.Entry, target.Under);
        var retention = AgentRetentionReader.Read(entry, input.Paths, input.Files, input.Environment);
        var age = EffectiveAgeDays(input.Config, retention);
        return (target.Refusal.Length > 0 ? target.Refusal : Placed(input, entry, under)) is { Length: > 0 } why
            ? new AgentSelection(entry, under, retention, age, [], [], 0, 0, why)
            : Listed(input, entry, under, retention, age);
    }

    /// <summary>Why the layout's folder is not listed here; empty when it may be.</summary>
    private static string Placed(SelectionInput input, AgentEntry entry, string under) =>
        under.Length == 0 ? $"{entry.Name} keeps no session layout on this side"
        : !input.Files.DirectoryExists(under) ? $"{under} does not exist"
        : AgentWalk.PlaceProblem(input.Files, input.Paths.Home, under);

    private static AgentSelection Listed(SelectionInput input, AgentEntry entry, string under, RetentionFound retention, int age)
    {
        var rules = AgentWalk.RulesFor(entry) with { ListFiles = true };
        var listing = new SessionListing(input.Files, rules.NeverEnter, () => input.OutOfTime, input.Token) { Device = input.Files.DeviceOf(under) };
        var units = entry.Archive!.Units.SelectMany(unit => Units(input, entry, under, unit, listing, rules)).ToList();
        var cutoff = input.Now - TimeSpan.FromDays(age);
        var due = units.Where(u => u.Unit.NewestWriteUtc < cutoff).OrderBy(u => u.Unit.NewestWriteUtc).ThenBy(u => u.Unit.Key, StringComparer.Ordinal).ToList();
        var quarantined = QuarantineCount.Found(new QuarantineCount.Look(input, entry, under, listing, rules), units.SelectMany(u => u.Unit.Files));
        return new AgentSelection(
            entry, under, retention, age,
            [.. due.Where(u => u.Unit.SkipRule.Length == 0).Select(u => u.Unit)],
            [.. due.Where(u => u.Unit.SkipRule.Length > 0).Select(u => u.Unit)],
            units.Count - due.Count,
            quarantined.Count,
            string.Join("; ", units.Select(u => u.ListingNote).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)))
        { QuarantinedFiles = [.. quarantined.Order(StringComparer.Ordinal)] };
    }

    /// <summary>A unit, and what the listing that found it could not see.</summary>
    private sealed record FoundUnit(UnitFound Unit, string ListingNote);

    private static IEnumerable<FoundUnit> Units(SelectionInput input, AgentEntry entry, string under, ArchiveUnit unit, SessionListing listing, TreeRules rules)
    {
        var glob = unit.Kind == ArchiveUnitKinds.Session ? entry.Sessions!.Glob : unit.Glob;
        var companions = unit.Kind == ArchiveUnitKinds.Session ? entry.Sessions!.Companions : [];
        var scan = SessionGlob.Find(listing, under, glob);
        var note = scan.Complete ? string.Empty : scan.Note;
        return scan.Sessions.Select(found => new FoundUnit(Unit(input, entry, under, unit, found, companions, rules), note));
    }

    private static UnitFound Unit(SelectionInput input, AgentEntry entry, string under, ArchiveUnit unit, SessionFound found, IReadOnlyList<string> companions, TreeRules rules)
    {
        var main = new UnitFile(found.Session.Name, Path.Combine(under, found.Session.Name), found.Session.Bytes, found.LastWrite);
        if (CompanionProblem(found.Session.Name, companions) is { Length: > 0 } bad)
        {
            return new UnitFound(unit.Kind, found.Session.Name, [main], main.LastWriteUtc, MonthOf(main.LastWriteUtc, input.Zone), SkipRule.Name, bad);
        }

        var expanded = companions.Select(c => Expand(c, found.Session.Name)).ToList();
        var gathered = expanded.Select(c => Companion(input, under, c, rules)).ToList();
        IReadOnlyList<UnitFile> files = [main, .. gathered.SelectMany(g => g.Files)];
        var newest = files.Max(f => f.LastWriteUtc);
        var month = MonthOf(newest, input.Zone);
        var (rule, why) = Judged(new UnitCheck(input, entry, under, unit, found.Session.Name, files, expanded, gathered.Select(g => g.Note).FirstOrDefault(n => n.Length > 0) ?? string.Empty));
        return new UnitFound(unit.Kind, found.Session.Name, files, newest, month, rule, why);
    }

    private static string MonthOf(DateTimeOffset newest, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(newest, zone).ToString("yyyy/MM", CultureInfo.InvariantCulture);

    /// <summary>A companion template with <c>{dir}</c> (the session file's folder) and <c>{id}</c> (its name without the extension).</summary>
    internal static string Expand(string template, string sessionName)
    {
        var name = sessionName.Replace('\\', '/');
        var dir = name.Contains('/', StringComparison.Ordinal) ? name[..name.LastIndexOf('/')] : string.Empty;
        return template.Replace("{dir}", dir, StringComparison.Ordinal).Replace("{id}", IdOf(name), StringComparison.Ordinal).TrimStart('/');
    }

    private static string IdOf(string sessionName) => Path.GetFileNameWithoutExtension(sessionName.Replace('\\', '/'));

    /// <summary>E9.S1 review round M1: a session whose id is empty or a dot name (<c>.jsonl</c>, <c>..jsonl</c>, <c>...jsonl</c>) would
    /// expand <c>{dir}/{id}</c> to the folder around it — or the agent's folder itself — and take every file there as its own. Such a
    /// unit is refused by name, and every expanded companion must stay strictly inside its template's own folder. Empty when sound.</summary>
    internal static string CompanionProblem(string sessionName, IReadOnlyList<string> templates) =>
        IdOf(sessionName) is "" or "." or ".." ? $"its id \"{IdOf(sessionName)}\" is empty or a dot name, so its companions would name the folder around it; it is never taken"
        : templates.FirstOrDefault(t => !StaysInside(t, sessionName)) is { } template ? $"its companion {template} expands to {Expand(template, sessionName)}, outside its own folder; it is never taken"
        : string.Empty;

    private static bool StaysInside(string template, string sessionName)
    {
        var expanded = Expand(template, sessionName);
        var cut = template.LastIndexOf('/');
        var parent = cut < 0 ? string.Empty : Expand(template[..cut], sessionName);
        return !expanded.Split('/').Any(s => s is "" or "." or "..") && (parent.Length == 0 || expanded.StartsWith(parent + "/", StringComparison.Ordinal));
    }

    /// <summary>The files of one companion — a file by stat, a folder by the walk's rules; nothing when absent.</summary>
    private sealed record Gathered(IReadOnlyList<UnitFile> Files, string Note);

    private static Gathered Companion(SelectionInput input, string under, string relative, TreeRules rules)
    {
        var path = Path.Combine(under, relative);
        return input.OutOfTime ? new Gathered([], $"{relative}: the listing ran out of time")
            : input.Files.ReadLink(path) is LinkReadResult.Target ? new Gathered([], $"{relative} is a link, never followed")
            : input.Files.DirectoryExists(path) ? Folder(input, under, path, rules)
            : CompanionFile(input.Files.FileSize(path), relative, path);
    }

    /// <summary>A companion FILE: measured, absent — or, E9.S1 review round m2, unreadable, which keeps its unit as not whole.</summary>
    private static Gathered CompanionFile(FileSizeResult size, string relative, string path) => size switch
    {
        FileSizeResult.Measured m => new Gathered([new UnitFile(relative, path, m.Bytes, m.ModifiedAt)], string.Empty),
        FileSizeResult.Unreadable u => new Gathered([], $"{relative}: {u.Reason}"),
        _ => new Gathered([], string.Empty),
    };

    /// <summary>A companion folder, walked with the time the listing has LEFT (E9.S1 review round m1).</summary>
    private static Gathered Folder(SelectionInput input, string under, string path, TreeRules rules) => input.Files.WalkTree(path, LimitsLeft(input), rules, input.Token) switch
    {
        TreeMeasure.Measured { Complete: true } m => new Gathered([.. m.Listed.Select(f => new UnitFile(Relative(under, f.Path), f.Path, f.Length, f.LastWriteUtc))], Excluded(m)),
        TreeMeasure.Measured m => new Gathered([], $"{Relative(under, path)}: {m.Note}"),
        TreeMeasure.Unreadable u => new Gathered([], $"{Relative(under, path)}: {u.Reason}"),
        _ => new Gathered([], string.Empty),
    };

    private static TreeLimits LimitsLeft(SelectionInput input)
    {
        var walk = FolderSizes.Limits;
        var left = input.TimeLeft();
        return walk with { MaxDuration = left < walk.MaxDuration ? left : walk.MaxDuration };
    }

    /// <summary>A companion folder holding a folder the walk declined to enter (<c>memory</c>, another filesystem) is not whole.</summary>
    private static string Excluded(TreeMeasure.Measured measured) =>
        measured.Excluded.Count == 0 ? string.Empty : $"left out: {string.Join(", ", measured.Excluded)}";

    private static string Relative(string under, string path) => Path.GetRelativePath(under, path).Replace('\\', '/');

    /// <summary>One unit as the rules that may keep it in place look at it.</summary>
    private sealed record UnitCheck(SelectionInput Input, AgentEntry Entry, string Under, ArchiveUnit Unit, string Key, IReadOnlyList<UnitFile> Files, IReadOnlyList<string> Companions, string GatherNote);

    /// <summary>The rules that keep a due unit where it is, in order.</summary>
    private static readonly Func<UnitCheck, RuleVerdict>[] Keepers =
    [
        c => NeverMoved(c.Entry, c.Files, c.Companions),
        c => NameProblem(c.Files),
        c => RuleVerdict.When(c.GatherNote.Length > 0, SkipRule.NotWhole, () => $"not every file of it was seen ({c.GatherNote}); a unit moves whole or not at all"),
        c => MayBeOpen(c.Input.Files, c.Under, c.Unit, c.Key),
        c => ScanIncomplete(c.Input.InUse),
        c => InUse(c.Input, c.Files),
        c => AgentHere(c.Input, c.Entry, c.Key),
    ];

    /// <summary>The first rule that keeps a due unit where it is; ("", "") when none does.</summary>
    private static (string Rule, string Why) Judged(UnitCheck check) => RuleVerdict.First(Keepers, check) switch
    {
        RuleVerdict.Refuses kept => (kept.Rule, kept.Why),
        _ => (string.Empty, string.Empty),
    };

    /// <summary>Plan §15q H2, §15r: a unit any of whose files — or a companion it names, present or not — lies at or under a name that
    /// never moves is refused WHOLE (a session named <c>memory.jsonl</c> names the companion <c>projects/&lt;p&gt;/memory</c>).</summary>
    private static RuleVerdict NeverMoved(AgentEntry entry, IReadOnlyList<UnitFile> files, IReadOnlyList<string> companions) =>
        files.Select(f => f.Relative).Concat(companions).FirstOrDefault(p => AgentArchiveRules.IsNeverMoved(entry.Archive!, p)) is { } named
            ? new RuleVerdict.Refuses(SkipRule.NeverMoved, $"it names {named}, which never moves — the whole unit stays")
            : RuleVerdict.Holds;

    private static RuleVerdict NameProblem(IReadOnlyList<UnitFile> files)
    {
        var problem = files.SelectMany(f => f.Relative.Split('/')).Select(ArchiveNames.Problem).FirstOrDefault(p => p.Length > 0)
            ?? ArchiveNames.CaseCollision(files.Select(f => f.Relative));
        return RuleVerdict.When(problem.Length > 0, SkipRule.Name, () => $"{problem}; the archive could not hold it on a Windows drive or a share");
    }

    private static RuleVerdict MayBeOpen(IFileSystem files, string under, ArchiveUnit unit, string key) =>
        unit.SkipWhilePresent.Select(t => Expand(t, key)).FirstOrDefault(c => files.FileExists(Path.Combine(under, c))) is { } present
            ? new RuleVerdict.Refuses(SkipRule.MayBeOpen, $"{present} exists, so the database may be open; it is left until it is gone")
            : RuleVerdict.Holds;

    /// <summary>E9.S1 review round M1: only a COMPLETE open-file scan lets a due unit move — a cut one saw nothing of what it did not
    /// reach, and one that never ran (Windows until E9.S5) saw nothing at all.</summary>
    private static RuleVerdict ScanIncomplete(InUseView view) => view.State switch
    {
        InUseState.Complete => RuleVerdict.Holds,
        InUseState.Cut => new RuleVerdict.Refuses(SkipRule.InUse, $"the open-file scan was cut ({view.Note}); what it did not reach may be open"),
        _ => new RuleVerdict.Refuses(SkipRule.InUse, $"which files are open was not checked ({view.Note})"),
    };

    private static RuleVerdict InUse(SelectionInput input, IReadOnlyList<UnitFile> files) =>
        files.FirstOrDefault(f => input.InUse.OpenFiles.Contains(Distro(input.Paths, f.OnDisk))) is { } open
            ? new RuleVerdict.Refuses(SkipRule.InUse, $"{open.Relative} is open in a process")
            : RuleVerdict.Holds;

    /// <summary>A live Claude Code process whose working directory is this session's project (§15r D2.2).</summary>
    private static RuleVerdict AgentHere(SelectionInput input, AgentEntry entry, string key)
    {
        var segments = key.Split('/');
        return RuleVerdict.When(
            entry.Id == "claude-code" && segments.Length > 1 && input.InUse.ClaudeProjects.Contains(segments[1]),
            SkipRule.AgentWorkingHere,
            () => $"Claude Code is working in the project {segments[1]}");
    }

    private static string Distro(IHostPaths paths, string onDisk) => paths is LinuxHostPaths linux ? linux.ToDistro(onDisk) : onDisk;
}
