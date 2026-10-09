using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Agents;

/// <summary>One session a listing found: its name relative to the layout's folder, its length, its last write.</summary>
public sealed record SessionFound(SessionName Session, DateTimeOffset LastWrite);

/// <summary>What a session listing found.</summary>
/// <param name="Reached">The listing reached the level the sessions are on; <c>false</c> = it stopped before (a ceiling, the
/// deadline) and counted nothing — "not counted", never 0 (review R4).</param>
/// <param name="Complete">Every folder of that level was listed to its end.</param>
/// <param name="Note">Why it is not complete or not reached; empty when whole.</param>
public sealed record SessionScan(IReadOnlyList<SessionFound> Sessions, bool Reached, bool Complete, string Note);

/// <summary>How a session listing may look: through what, what it never enters, its deadline, its cancellation, and the device
/// it must stay on (review R4 / S3: a listing never crosses into another filesystem).</summary>
public sealed record SessionListing(IFileSystem Files, IReadOnlySet<string> NeverEnter, Func<bool> OutOfTime, CancellationToken Token)
{
    /// <summary>The device the listing stays on; <c>null</c> = not checked (a test of the pattern alone).</summary>
    public (uint Major, uint Minor)? Device { get; init; }

    /// <summary>The most entries the whole listing may see — <see cref="SessionGlob.MaxEntries"/> unless the caller has its own
    /// key (the MCP servers' log listing, plan §15q E7.S2d: <c>mcpServers.maxLogEntries</c>).</summary>
    public int MaxEntries { get; init; } = SessionGlob.MaxEntries;
}

/// <summary>
/// The sessions of one layout (plan §15q D2): a glob of name patterns, one per folder level, matched over directory LISTINGS
/// — each folder listed ONCE, the matching folders entered, the matching files of the last level taken with the length and
/// last write the listing gives. No file is opened; a link is never followed; a folder of a never-enter name (<c>memory</c>)
/// is never entered, nor one on another filesystem; a ceiling on entries, the caller's deadline and its cancellation bound
/// every listing.
/// </summary>
public static class SessionGlob
{
    /// <summary>The most entries one layout's listing may see.</summary>
    public static int MaxEntries => Tuning.Current.Int(ConfigKeys.Agents.SessionMaxEntries);

    /// <summary>A whole segment that matches the folder it is in and every folder below it (a manual agent's glob, plan §15q R2.1).</summary>
    public const string AnyDepth = "**";

    public static SessionScan Find(SessionListing listing, string under, string glob)
    {
        var walk = new Walk(listing);
        var level = new List<(string Path, string Relative)> { (under, string.Empty) };
        foreach (var segment in FolderSegments(glob))
        {
            level = segment == AnyDepth ? walk.Descendants(level) : walk.Matching(level, segment);
            if (walk.Stop() is { Length: > 0 } why)
            {
                return new SessionScan([], false, false, why);
            }
        }

        return walk.Sessions(level, LastSegment(glob));
    }

    /// <summary>The same, with no device rule — what a test of the pattern alone uses.</summary>
    public static SessionScan Find(IFileSystem files, string under, string glob, IReadOnlySet<string> neverEnter, Func<bool> outOfTime) =>
        Find(new SessionListing(files, neverEnter, outOfTime, CancellationToken.None), under, glob);

    /// <summary>The folder levels of <paramref name="glob"/>: every segment but the file pattern (a trailing <c>**</c> is a level
    /// and its file pattern is <c>*</c>).</summary>
    private static IEnumerable<string> FolderSegments(string glob)
    {
        var segments = glob.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1] == AnyDepth ? segments : segments.SkipLast(1);
    }

    private static string LastSegment(string glob)
    {
        var segments = glob.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 || segments[^1] == AnyDepth ? "*" : segments[^1];
    }

    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/>: <c>*</c> any run of characters, <c>?</c> one.</summary>
    /// <remarks>E9.S1 review round P1: a user-layer pattern is matched by ROOT's walk against every name it lists, so the matcher is
    /// never exponential — two cursors and the last star's mark, backtracking only to that mark, at most pattern × name steps (the
    /// recursive form was exponential: forty stars against a 200-character name never answered).</remarks>
    public static bool Matches(string pattern, string name)
    {
        var cursor = new MatchCursor();
        while (cursor.Name < name.Length)
        {
            if (!Step(pattern, name, ref cursor))
            {
                return false;
            }
        }

        return pattern.AsSpan(cursor.Pattern).IndexOfAnyExcept('*') < 0;
    }

    /// <summary>Where the match stands: the pattern and name positions, and the last star with the name position it was tried at.</summary>
    private struct MatchCursor
    {
        public int Pattern;
        public int Name;
        public int Star;
        public int StarName;

        public MatchCursor()
        {
            Star = -1;
        }
    }

    /// <summary>One step: consume a matching character, mark a star, or back up to the last star with one more character taken by
    /// it; <c>false</c> when there is no star to back up to.</summary>
    private static bool Step(string pattern, string name, ref MatchCursor at) =>
        at.Pattern >= pattern.Length ? BackToStar(ref at)
        : pattern[at.Pattern] == '*' ? MarkStar(ref at)
        : OneMatches(pattern[at.Pattern], name[at.Name]) ? Advance(ref at)
        : BackToStar(ref at);

    private static bool Advance(ref MatchCursor at)
    {
        (at.Pattern, at.Name) = (at.Pattern + 1, at.Name + 1);
        return true;
    }

    private static bool MarkStar(ref MatchCursor at)
    {
        (at.Star, at.StarName, at.Pattern) = (at.Pattern, at.Name, at.Pattern + 1);
        return true;
    }

    /// <summary>The last star takes one more character; <c>false</c> when no star was seen.</summary>
    private static bool BackToStar(ref MatchCursor at)
    {
        if (at.Star < 0)
        {
            return false;
        }

        (at.Pattern, at.StarName) = (at.Star + 1, at.StarName + 1);
        at.Name = at.StarName;
        return true;
    }

    private static bool OneMatches(char pattern, char name) => pattern == '?' || pattern == name;

    /// <summary>One listing's state: what it has listed (each folder once), how many entries it saw, what it left out.</summary>
    private sealed class Walk(SessionListing listing)
    {
        private readonly Dictionary<string, IReadOnlyList<FileEntry>> _listed = new(StringComparer.Ordinal);
        private int _seen;
        private string _skipped = string.Empty;
        private string _lost = string.Empty;

        /// <summary>A folder's entries, listed once; the cancellation and the deadline asked before every listing AND at every
        /// entry, with what is left of the whole listing's entry cap (plan §15q E7.S2d, consultation C-1).</summary>
        private IReadOnlyList<FileEntry> List(string path)
        {
            listing.Token.ThrowIfCancellationRequested();
            if (!_listed.TryGetValue(path, out var entries))
            {
                entries = Stop().Length > 0 ? [] : Bounded(path);
                _listed[path] = entries;
                _seen += entries.Count;
            }

            return entries;
        }

        /// <summary>One bounded listing. A folder cut short or that could not be read leaves the scan INCOMPLETE, whatever the
        /// folders after it answer: what it lost may be a session (consultation C-1 — an unreadable folder used to read as an
        /// empty one, and the scan as whole).</summary>
        private IReadOnlyList<FileEntry> Bounded(string path)
        {
            switch (listing.Files.ListEntries(path, new ListingBounds(listing.MaxEntries - _seen, listing.OutOfTime, listing.Token)))
            {
                case EntryListing.Listed { Complete: true } whole:
                    return whole.Entries;
                case EntryListing.Listed cut:
                    _lost = $"{cut.Note}; the count is a lower bound";
                    return cut.Entries;
                case EntryListing.Unreadable unreadable:
                    _lost = $"{unreadable.Reason}; the count is a lower bound";
                    return [];
                default:
                    throw new System.Diagnostics.UnreachableException("EntryListing is a closed set");
            }
        }

        public string Stop() =>
            _seen >= listing.MaxEntries ? $"stopped after {listing.MaxEntries} entries"
            : listing.OutOfTime() ? "stopped at the walk's time budget"
            : string.Empty;

        /// <summary>The matching child folders of <paramref name="level"/> that may be entered.</summary>
        public List<(string Path, string Relative)> Matching(List<(string Path, string Relative)> level, string pattern) =>
            [.. level.SelectMany(folder => List(folder.Path)
                .Where(e => e.Kind == EntryKind.Directory && Matches(pattern, e.Name) && MayEnter(Path.Combine(folder.Path, e.Name), e.Name))
                .Select(e => (Path.Combine(folder.Path, e.Name), folder.Relative + e.Name + "/")))];

        /// <summary><paramref name="level"/> and every folder below it, breadth first, each listed once.</summary>
        public List<(string Path, string Relative)> Descendants(List<(string Path, string Relative)> level)
        {
            var all = new List<(string Path, string Relative)>(level);
            for (var at = 0; at < all.Count && Stop().Length == 0; at++)
            {
                all.AddRange(Matching([all[at]], "*"));
            }

            return all;
        }

        public SessionScan Sessions(List<(string Path, string Relative)> level, string pattern)
        {
            var found = level.SelectMany(folder => List(folder.Path)
                    .Where(e => e.Kind == EntryKind.File && Matches(pattern, e.Name))
                    .Select(e => new SessionFound(new SessionName(folder.Relative + e.Name, e.Length), e.LastWriteUtc)))
                .ToList();
            var stopped = Stop();
            var why = stopped.Length > 0 ? stopped + "; the count is a lower bound" : string.Join("; ", new[] { _lost, _skipped }.Where(n => n.Length > 0));
            return new SessionScan(found, true, why.Length == 0, why);
        }

        /// <summary>Not a never-enter name, and on the listing's device — one on another is left out and said.</summary>
        private bool MayEnter(string path, string name)
        {
            if (listing.NeverEnter.Contains(name))
            {
                return false;
            }

            if (listing.Device is { } own && listing.Files.DeviceOf(path) != own)
            {
                _skipped = "a folder on another filesystem was not listed; the count is a lower bound";
                return false;
            }

            return true;
        }
    }
}
