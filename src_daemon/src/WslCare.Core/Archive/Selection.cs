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
public sealed record AgentSelection(AgentEntry Entry, string Under, RetentionFound Retention, int EffectiveAgeDays, IReadOnlyList<UnitFound> Due, IReadOnlyList<UnitFound> Skipped, int Younger, int Quarantined, string Note);

/// <summary>Everything the selection reads, and when it is.</summary>
public sealed record SelectionInput(IHostPaths Paths, IFileSystem Files, EffectiveConfig Config, DateTimeOffset Now, TimeZoneInfo Zone, InUseView InUse, Func<string, string?> Environment)
{
    /// <summary>Only this agent (<c>archive preview --agent</c>); empty = every agent of <c>archive.agents</c>.</summary>
    public string OnlyAgent { get; init; } = string.Empty;

    /// <summary>When the listing must stop; never by default.</summary>
    public Func<bool> OutOfTime { get; init; } = static () => false;

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
    public static IReadOnlyList<AgentSelection> Select(SelectionInput input) =>
        [.. Agents(input).Select(entry => Agent(input, entry))];

    /// <summary>The agents asked for: <c>archive.agents</c> (or the one named), each with an archive block.</summary>
    private static IEnumerable<AgentEntry> Agents(SelectionInput input)
    {
        var enabled = input.Config.TextList(ConfigKeys.Archive.Agents);
        return AgentCatalogue.Agents.Where(a => a.Archive is not null && enabled.Contains(a.Id, StringComparer.Ordinal) && (input.OnlyAgent.Length == 0 || a.Id == input.OnlyAgent));
    }

    /// <summary>§15r D10: min(<c>archive.olderThanDays</c>, the measured retention − <c>archive.marginDays</c> − the removal's
    /// whole days), at least 1.</summary>
    public static int EffectiveAgeDays(EffectiveConfig config, RetentionFound retention)
    {
        var older = config.Int(ConfigKeys.Archive.OlderThanDays);
        return retention.Days is { } days
            ? Math.Max(1, Math.Min(older, days - config.Int(ConfigKeys.Archive.MarginDays) - RemovalDays(config)))
            : older;
    }

    private static int RemovalDays(EffectiveConfig config) =>
        (int)Math.Ceiling(TimeSpan.FromHours(config.Int(ConfigKeys.Archive.RemoveAfterHours)).TotalDays);

    private static AgentSelection Agent(SelectionInput input, AgentEntry entry)
    {
        var retention = AgentRetentionReader.Read(entry, input.Paths, input.Files, input.Environment);
        var age = EffectiveAgeDays(input.Config, retention);
        var under = AgentDiscovery.SessionsUnderOf(input.Paths, entry);
        return Placed(input, entry, under) is { Length: > 0 } why
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
        var listing = new SessionListing(input.Files, rules.NeverEnter, input.OutOfTime, input.Token) { Device = input.Files.DeviceOf(under) };
        var units = entry.Archive!.Units.SelectMany(unit => Units(input, entry, under, unit, listing, rules)).ToList();
        var cutoff = input.Now - TimeSpan.FromDays(age);
        var due = units.Where(u => u.Unit.NewestWriteUtc < cutoff).OrderBy(u => u.Unit.NewestWriteUtc).ThenBy(u => u.Unit.Key, StringComparer.Ordinal).ToList();
        var quarantined = Quarantined(entry, under, listing, units.SelectMany(u => u.Unit.Files));
        return new AgentSelection(
            entry, under, retention, age,
            [.. due.Where(u => u.Unit.SkipRule.Length == 0).Select(u => u.Unit)],
            [.. due.Where(u => u.Unit.SkipRule.Length > 0).Select(u => u.Unit)],
            units.Count - due.Count,
            quarantined,
            string.Join("; ", units.Select(u => u.ListingNote).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)));
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
        var expanded = companions.Select(c => Expand(c, found.Session.Name)).ToList();
        var gathered = expanded.Select(c => Companion(input.Files, under, c, rules, input.Token)).ToList();
        IReadOnlyList<UnitFile> files = [main, .. gathered.SelectMany(g => g.Files)];
        var newest = files.Max(f => f.LastWriteUtc);
        var month = TimeZoneInfo.ConvertTime(newest, input.Zone).ToString("yyyy/MM", CultureInfo.InvariantCulture);
        var (rule, why) = Judged(new UnitCheck(input, entry, under, unit, found.Session.Name, files, expanded, gathered.Select(g => g.Note).FirstOrDefault(n => n.Length > 0) ?? string.Empty));
        return new UnitFound(unit.Kind, found.Session.Name, files, newest, month, rule, why);
    }

    /// <summary>A companion template with <c>{dir}</c> (the session file's folder) and <c>{id}</c> (its name without the extension).</summary>
    private static string Expand(string template, string sessionName)
    {
        var name = sessionName.Replace('\\', '/');
        var dir = name.Contains('/', StringComparison.Ordinal) ? name[..name.LastIndexOf('/')] : string.Empty;
        return template.Replace("{dir}", dir, StringComparison.Ordinal).Replace("{id}", Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal).TrimStart('/');
    }

    /// <summary>The files of one companion — a file by stat, a folder by the walk's rules; nothing when absent.</summary>
    private sealed record Gathered(IReadOnlyList<UnitFile> Files, string Note);

    private static Gathered Companion(IFileSystem files, string under, string relative, TreeRules rules, CancellationToken token)
    {
        var path = Path.Combine(under, relative);
        return files.ReadLink(path) is LinkReadResult.Target ? new Gathered([], $"{relative} is a link, never followed")
            : files.DirectoryExists(path) ? Folder(files, under, path, rules, token)
            : files.FileSize(path) is FileSizeResult.Measured m ? new Gathered([new UnitFile(relative, path, m.Bytes, m.ModifiedAt)], string.Empty)
            : new Gathered([], string.Empty);
    }

    private static Gathered Folder(IFileSystem files, string under, string path, TreeRules rules, CancellationToken token) => files.WalkTree(path, FolderSizes.Limits, rules, token) switch
    {
        TreeMeasure.Measured { Complete: true } m => new Gathered([.. m.Listed.Select(f => new UnitFile(Relative(under, f.Path), f.Path, f.Length, f.LastWriteUtc))], Excluded(m)),
        TreeMeasure.Measured m => new Gathered([], $"{Relative(under, path)}: {m.Note}"),
        TreeMeasure.Unreadable u => new Gathered([], $"{Relative(under, path)}: {u.Reason}"),
        _ => new Gathered([], string.Empty),
    };

    /// <summary>A companion folder holding a folder the walk declined to enter (<c>memory</c>, another filesystem) is not whole.</summary>
    private static string Excluded(TreeMeasure.Measured measured) =>
        measured.Excluded.Count == 0 ? string.Empty : $"left out: {string.Join(", ", measured.Excluded)}";

    private static string Relative(string under, string path) => Path.GetRelativePath(under, path).Replace('\\', '/');

    /// <summary>One unit as the rules that may keep it in place look at it.</summary>
    private sealed record UnitCheck(SelectionInput Input, AgentEntry Entry, string Under, ArchiveUnit Unit, string Key, IReadOnlyList<UnitFile> Files, IReadOnlyList<string> Companions, string GatherNote);

    /// <summary>The rules that keep a due unit where it is, in order.</summary>
    private static readonly Func<UnitCheck, (string Rule, string Why)?>[] Keepers =
    [
        c => NeverMoved(c.Entry, c.Files, c.Companions),
        c => NameProblem(c.Files),
        c => c.GatherNote.Length > 0 ? (SkipRule.NotWhole, $"not every file of it was seen ({c.GatherNote}); a unit moves whole or not at all") : null,
        c => MayBeOpen(c.Input.Files, c.Under, c.Unit, c.Key),
        c => InUse(c.Input, c.Files),
        c => AgentHere(c.Input, c.Entry, c.Key),
    ];

    /// <summary>The first rule that keeps a due unit where it is; ("", "") when none does.</summary>
    private static (string Rule, string Why) Judged(UnitCheck check) =>
        Keepers.Select(keeper => keeper(check)).FirstOrDefault(kept => kept is not null) ?? (string.Empty, string.Empty);

    /// <summary>Plan §15q H2, §15r: a unit any of whose files — or a companion it names, present or not — lies at or under a name that
    /// never moves is refused WHOLE (a session named <c>memory.jsonl</c> names the companion <c>projects/&lt;p&gt;/memory</c>).</summary>
    private static (string, string)? NeverMoved(AgentEntry entry, IReadOnlyList<UnitFile> files, IReadOnlyList<string> companions) =>
        files.Select(f => f.Relative).Concat(companions).FirstOrDefault(p => AgentArchiveRules.IsNeverMoved(entry.Archive!, p)) is { } named
            ? (SkipRule.NeverMoved, $"it names {named}, which never moves — the whole unit stays")
            : null;

    private static (string, string)? NameProblem(IReadOnlyList<UnitFile> files)
    {
        var problem = files.SelectMany(f => f.Relative.Split('/')).Select(ArchiveNames.Problem).FirstOrDefault(p => p.Length > 0)
            ?? ArchiveNames.CaseCollision(files.Select(f => f.Relative));
        return problem.Length > 0 ? (SkipRule.Name, $"{problem}; the archive could not hold it on a Windows drive or a share") : null;
    }

    private static (string, string)? MayBeOpen(IFileSystem files, string under, ArchiveUnit unit, string key) =>
        unit.SkipWhilePresent.Select(t => Expand(t, key)).FirstOrDefault(c => files.FileExists(Path.Combine(under, c))) is { } present
            ? (SkipRule.MayBeOpen, $"{present} exists, so the database may be open; it is left until it is gone")
            : null;

    private static (string, string)? InUse(SelectionInput input, IReadOnlyList<UnitFile> files)
    {
        var open = files.FirstOrDefault(f => input.InUse.OpenFiles.Contains(Distro(input.Paths, f.OnDisk)));
        return open is not null ? (SkipRule.InUse, $"{open.Relative} is open in a process") : null;
    }

    /// <summary>A live Claude Code process whose working directory is this session's project (§15r D2.2).</summary>
    private static (string, string)? AgentHere(SelectionInput input, AgentEntry entry, string key)
    {
        var segments = key.Split('/');
        return entry.Id == "claude-code" && segments.Length > 1 && input.InUse.ClaudeProjects.Contains(segments[1])
            ? (SkipRule.AgentWorkingHere, $"Claude Code is working in the project {segments[1]}")
            : null;
    }

    private static string Distro(IHostPaths paths, string onDisk) => paths is LinuxHostPaths linux ? linux.ToDistro(onDisk) : onDisk;

    /// <summary>Review M3: files an interrupted removal left under the quarantine name, at the unit level and inside the units.</summary>
    private static int Quarantined(AgentEntry entry, string under, SessionListing listing, IEnumerable<UnitFile> unitFiles)
    {
        var levels = entry.Archive!.Units.Select(u => u.Kind == ArchiveUnitKinds.Session ? entry.Sessions!.Glob : u.Glob).Distinct(StringComparer.Ordinal);
        var atLevel = levels.Sum(glob => SessionGlob.Find(listing, under, QuarantineGlob(glob)).Sessions.Count);
        return atLevel + unitFiles.Count(f => f.Relative.Contains(ArchiveNames.QuarantineMark, StringComparison.Ordinal));
    }

    /// <summary>The glob's last segment replaced by "any file carrying the quarantine mark".</summary>
    private static string QuarantineGlob(string glob)
    {
        var cut = glob.LastIndexOf('/');
        return (cut < 0 ? string.Empty : glob[..(cut + 1)]) + "*" + ArchiveNames.QuarantineMark + "*";
    }
}
