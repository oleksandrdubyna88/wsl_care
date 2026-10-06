using WslCare.Core.Agents;
using WslCare.Core.Files;

namespace WslCare.Core.Archive;

/// <summary>
/// Review M3, E9.S1 review round m3 — the files an interrupted removal left under their quarantine names
/// (<c>&lt;name&gt;.wsl-care-q-&lt;runId&gt;</c>): inside the units the selection found, at the layout's own level, AND inside the
/// companions of a session whose MAIN file is quarantined — that session no longer matches its glob, so its companions are found
/// through the id of the quarantined name. Each file counted once, however it was reached. Counted only; resolving them is the
/// run's reconcile (E9.S2b).
/// </summary>
public static class QuarantineCount
{
    /// <summary>What the count looks at.</summary>
    public sealed record Look(SelectionInput Input, AgentEntry Entry, string Under, SessionListing Listing, TreeRules Rules);

    public static int Of(Look look, IEnumerable<UnitFile> unitFiles)
    {
        var atLevel = Levels(look.Entry).SelectMany(level => SessionGlob.Find(look.Listing, look.Under, QuarantineGlob(level.Glob)).Sessions.Select(s => (level.Session, s.Session.Name))).ToList();
        var found = new HashSet<string>(StringComparer.Ordinal);
        found.UnionWith(atLevel.Select(q => q.Name));
        found.UnionWith(unitFiles.Select(f => f.Relative).Where(Marked));
        found.UnionWith(atLevel.Where(q => q.Session).SelectMany(q => InCompanions(look, Original(q.Name))));
        return found.Count;
    }

    /// <summary>Each unit's glob, and whether it is a session unit (whose companions follow its id).</summary>
    private static IEnumerable<(string Glob, bool Session)> Levels(AgentEntry entry) =>
        entry.Archive!.Units.Select(u => u.Kind == ArchiveUnitKinds.Session ? (entry.Sessions!.Glob, true) : (u.Glob, false)).Distinct();

    /// <summary>The marked files inside the companions of the session whose main file was <paramref name="original"/>.</summary>
    private static IEnumerable<string> InCompanions(Look look, string original) =>
        Selection.CompanionProblem(original, look.Entry.Sessions!.Companions).Length > 0
            ? []
            : look.Entry.Sessions!.Companions.SelectMany(template => InCompanion(look, Selection.Expand(template, original)));

    private static IEnumerable<string> InCompanion(Look look, string relative)
    {
        var path = Path.Combine(look.Under, relative);
        return look.Input.Files.DirectoryExists(path) ? MarkedIn(look, path) : MarkedBeside(look, relative);
    }

    /// <summary>The marked files a companion FOLDER holds.</summary>
    private static IEnumerable<string> MarkedIn(Look look, string path) =>
        look.Input.Files.WalkTree(path, Folders.FolderSizes.Limits, look.Rules, look.Input.Token) is TreeMeasure.Measured m
            ? m.Listed.Select(f => Relative(look.Under, f.Path)).Where(r => Marked(Path.GetFileName(r)))
            : [];

    /// <summary>A companion FILE under its quarantine name, beside where it was.</summary>
    private static IEnumerable<string> MarkedBeside(Look look, string relative)
    {
        var folder = Path.GetDirectoryName(Path.Combine(look.Under, relative)) ?? look.Under;
        var prefix = Path.GetFileName(relative) + ArchiveNames.QuarantineMark;
        return look.Input.Files.ListEntries(folder).Where(e => e.Name.StartsWith(prefix, StringComparison.Ordinal)).Select(e => Relative(look.Under, Path.Combine(folder, e.Name)));
    }

    private static string Relative(string under, string path) => Path.GetRelativePath(under, path).Replace('\\', '/');

    private static bool Marked(string name) => name.Contains(ArchiveNames.QuarantineMark, StringComparison.Ordinal);

    /// <summary>The name a quarantined file had: everything before its mark.</summary>
    private static string Original(string quarantined) => quarantined[..quarantined.IndexOf(ArchiveNames.QuarantineMark, StringComparison.Ordinal)];

    /// <summary>The glob's last segment replaced by "any file carrying the quarantine mark".</summary>
    private static string QuarantineGlob(string glob)
    {
        var cut = glob.LastIndexOf('/');
        return (cut < 0 ? string.Empty : glob[..(cut + 1)]) + "*" + ArchiveNames.QuarantineMark + "*";
    }
}
