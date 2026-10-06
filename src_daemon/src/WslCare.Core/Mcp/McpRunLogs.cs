using System.Globalization;

using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Files;

namespace WslCare.Core.Mcp;

/// <summary>One run log of a server: the pid in its name, the instant its name says (UTC), its last write.</summary>
public sealed record McpLogFile(int Pid, DateTimeOffset NamedAt, DateTimeOffset LastWrite)
{
    /// <summary>Named at a midnight: the family contract's continuation segment of a run that outlived the day — or a start that
    /// happened at 00:00:00 (decided by the caller, plan §15q E7.S2d Decided 8).</summary>
    public bool AtMidnight => NamedAt.TimeOfDay == TimeSpan.Zero;
}

/// <summary>A server's run logs of today and yesterday (UTC).</summary>
public sealed record McpLogs(string Root, IReadOnlyList<McpLogFile> Files)
{
    /// <summary>The files grouped by pid once (coai code round finding 4): an instance's activity is a lookup, not a scan.</summary>
    public ILookup<int, McpLogFile> ByPid { get; } = Files.ToLookup(f => f.Pid);
}

/// <summary>
/// Reads a server's run logs per the family logging contract (<see cref="McpLogLayout.FamilyRunLogs"/>): the day folders of
/// today and yesterday (UTC) — the starts window is at most a day, so they cover it — listed through <see cref="SessionGlob"/>
/// (each folder once, no link followed, no file opened, the device held, the entry cap and the deadline asked at every entry),
/// after <see cref="AgentWalk.PlaceProblem"/> accepted the root: its real path under the real home, the home's device.
/// </summary>
public static class McpRunLogs
{
    /// <summary>Today and yesterday: the folders a window of at most one day (<c>mcpServers.startsWindowMinutes</c>' maximum) can reach.</summary>
    private static readonly TimeSpan[] DaysBack = [TimeSpan.Zero, TimeSpan.FromDays(1)];

    private const string LogSuffix = ".log";

    /// <summary>Why a server's activity and starts cannot come from its logs: it has no log layout.</summary>
    public static string NoLayout(string server) => $"not derivable: {server} has no log layout in the catalogue";

    public static Reading<McpLogs> Read(IFileSystem files, string home, McpServerEntry server, DateTimeOffset now, McpSettings settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (server.Logs is not McpLogLayout.FamilyRunLogs layout)
        {
            return Reading.Missing<McpLogs>(NoLayout(server.Name));
        }

        var root = AgentCatalogue.LinuxFolder("~/" + layout.UnderHome, home);
        var started = clock.GetTimestamp();
        var bounds = new ListingBounds(settings.MaxLogEntries, () => clock.GetElapsedTime(started) >= settings.LogListBudget, cancellationToken);
        return Present(files, home, layout.UnderHome, bounds) switch
        {
            Reading<bool>.Available { Value: false } => Reading.Of(new McpLogs(root, [])),
            Reading<bool>.Available when AgentWalk.PlaceProblem(files, home, root) is { Length: > 0 } problem => Reading.Missing<McpLogs>($"{root} is {problem}"),
            Reading<bool>.Available => List(files, root, layout, now, settings, bounds),
            var unknown => Reading.Missing<McpLogs>($"whether {root} exists cannot be told: {unknown.ReasonOrEmpty}"),
        };
    }

    /// <summary>Whether the log root exists, walked down from the home with the BOUNDED listing (coai code round finding 2):
    /// <c>Directory.Exists</c> answers false for a folder it may not traverse, which read as "no logs, no starts". A folder that
    /// cannot be listed — or is cut short — is no answer; only a whole listing without the next folder is "absent".</summary>
    private static Reading<bool> Present(IFileSystem files, string home, string underHome, ListingBounds bounds)
    {
        var at = home;
        foreach (var part in underHome.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = Step(files.ListEntries(at, bounds), part, at);
            if (step is not Reading<bool>.Available { Value: true })
            {
                return step;
            }

            at = AgentCatalogue.LinuxFolder("~/" + part, at);
        }

        return Reading.Of(true);
    }

    /// <summary>One level: true = the next folder is there, false = a whole listing without it, missing = no answer.</summary>
    private static Reading<bool> Step(EntryListing listing, string part, string folder) => listing switch
    {
        EntryListing.Listed { Complete: true } whole => Reading.Of(whole.Entries.Any(e => e.Name == part)),
        EntryListing.Listed cut => Reading.Missing<bool>($"{folder} was not listed to its end: {cut.Note}"),
        EntryListing.Unreadable unreadable => Reading.Missing<bool>(unreadable.Reason),
        _ => throw new System.Diagnostics.UnreachableException("EntryListing is a closed set"),
    };

    private static Reading<McpLogs> List(IFileSystem files, string root, McpLogLayout.FamilyRunLogs layout, DateTimeOffset now, McpSettings settings, ListingBounds bounds)
    {
        var listing = new SessionListing(files, new HashSet<string>(StringComparer.Ordinal), bounds.OutOfTime, bounds.Token)
        {
            Device = files.DeviceOf(root),
            MaxEntries = settings.MaxLogEntries,
        };
        var days = DaysBack.Select(back => (now - back).UtcDateTime.Date).ToList();
        var scans = days.Select(day => (Day: day, Scan: SessionGlob.Find(listing, root, $"{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}/{layout.Prefix}-*{LogSuffix}"))).ToList();
        var cut = scans.Select(s => s.Scan).FirstOrDefault(s => !s.Complete);
        return cut is not null
            ? Reading.Missing<McpLogs>($"the log listing of {root} is incomplete: {cut.Note}")
            : Reading.Of(new McpLogs(root, [.. scans.SelectMany(s => s.Scan.Sessions.SelectMany(f => Parse(s.Day, layout.Prefix, f)))]));
    }

    /// <summary>The file a listing found, when its name is the contract's <c>{prefix}-HH-mm-ss-{pid}.log</c>; nothing otherwise.</summary>
    private static IEnumerable<McpLogFile> Parse(DateTime day, string prefix, SessionFound found)
    {
        var name = found.Session.Name[(found.Session.Name.LastIndexOf('/') + 1)..];
        var fields = name.StartsWith(prefix + "-", StringComparison.Ordinal) && name.EndsWith(LogSuffix, StringComparison.Ordinal)
            ? name[(prefix.Length + 1)..^LogSuffix.Length].Split('-')
            : [];
        return Named(day, fields) is { } at && int.TryParse(fields[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? [new McpLogFile(pid, at, found.LastWrite)]
            : [];
    }

    /// <summary>The instant a name's <c>HH-mm-ss</c> marks on <paramref name="day"/>, when the fields are exactly hours, minutes,
    /// seconds and the pid.</summary>
    private static DateTimeOffset? Named(DateTime day, string[] fields) =>
        fields.Length == NameFields.Length && TimeSpan.TryParseExact(string.Join(':', fields[..^1]), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var time)
            ? new DateTimeOffset(day + time, TimeSpan.Zero)
            : null;

    /// <summary>The fields of a run log's name after its prefix (the family logging contract's format).</summary>
    private static readonly string[] NameFields = ["HH", "mm", "ss", "pid"];
}
