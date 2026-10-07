using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Json;

namespace WslCare.Core.Archive;

/// <summary>One archived file of an entry: its path relative to the agent's layout root (where it was, and where a restore puts
/// it back — D6), its path relative to the entry's own <c>&lt;agent&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;side&gt;/</c> folder (it may carry a
/// <c>~2</c>), its size, its SHA-256 and its last write.</summary>
public sealed record IndexFile(string Original, string Archived, long Bytes, string Sha256, DateTimeOffset LastWriteUtc);

/// <summary>
/// One line of a month index (plan §15r D2, D4): an event about one entry. The <c>archived</c> event carries the files; every later
/// event (<c>superseded</c>, <c>damaged</c>, <c>sourceRemoved</c>, <c>split</c>, <c>restored</c>, <c>recovered</c>) its status only.
/// <see cref="Mac"/> is HMAC-SHA-256 over the line written with an empty MAC, under the side's key.
/// </summary>
public sealed record IndexLine(
    int V,
    string Event,
    string EntryId,
    string Agent,
    string Side,
    string Key,
    string Month,
    DateTimeOffset AtUtc,
    string Zone,
    string RunId,
    IReadOnlyList<IndexFile> Files,
    string Mac);

/// <summary>An index line as a reader found it: whether its MAC holds under this side's key (D4: a line anything else wrote is
/// unverified — never invalid, never restored without <c>--accept-unverified</c>).</summary>
public sealed record IndexRecord(IndexLine Line, bool Verified);

/// <summary>One entry merged from its events (coai G3): the files of its <c>archived</c> event, the status of its latest event.</summary>
public sealed record IndexEntry(string EntryId, string Agent, string Key, string Month, IReadOnlyList<IndexFile> Files, string Status, bool Verified, DateTimeOffset ArchivedAtUtc);

/// <summary>What a month index's reader got: its records, and how many lines it skipped (torn, malformed, hostile).</summary>
public sealed record IndexRead(IReadOnlyList<IndexRecord> Records, int Skipped);

/// <summary>
/// Plan §15r D4 — the month index, per side, per agent, per month: written one line per event, read as UNTRUSTED input (it lives on
/// a possibly shared base): every line's shape is checked, every path is a plain relative path, and the MAC says whether this side
/// wrote it. Readers merge per <c>entryId</c>.
/// </summary>
public static partial class ArchiveIndex
{
    public const int SchemaVersion = 1;
    public const string FileName = "index.jsonl";

    public static class Events
    {
        public const string Archived = "archived";
        public const string Superseded = "superseded";
        public const string Damaged = "damaged";
        public const string SourceRemoved = "sourceRemoved";
        public const string Split = "split";
        public const string Restored = "restored";
        public const string Recovered = "recovered";

        public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { Archived, Superseded, Damaged, SourceRemoved, Split, Restored, Recovered };
    }

    /// <summary>The first 16 hex of the SHA-256 over the side, the agent, the unit's key and its files' hashes (D2).</summary>
    public static string EntryIdOf(string side, string agent, string key, IEnumerable<string> fileHashes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', [side, agent, key, .. fileHashes]))))[..EntryIdLength];

    private const int EntryIdLength = 16;

    /// <summary>The line as written: the MAC computed over the line with an empty MAC, one JSON line, a newline.</summary>
    public static byte[] Line(IndexLine line, byte[] key)
    {
        var mac = MacOf(line with { Mac = string.Empty }, key);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line with { Mac = mac }, WslCareJsonContext.Compact.IndexLine) + "\n");
    }

    private static string MacOf(IndexLine unsigned, byte[] key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unsigned, WslCareJsonContext.Compact.IndexLine))));

    /// <summary>Every whole, well-formed line of <paramref name="bytes"/>; a torn last line, a malformed or hostile one is skipped and
    /// counted. A line is verified when its MAC equals the one this side's <paramref name="key"/> gives (empty key: none is).</summary>
    public static IndexRead Read(byte[] bytes, byte[] key)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        var records = new List<IndexRecord>();
        var skipped = 0;
        foreach (var text in lines.Where(l => l.Length > 0))
        {
            if (Parse(text) is { } line && ShapeProblem(line).Length == 0)
            {
                records.Add(new IndexRecord(line, Signed(line, key)));
            }
            else
            {
                skipped++;
            }
        }

        return new IndexRead(records, skipped);
    }

    /// <summary>The line's MAC holds under this side's key — and it is not a <c>recovered</c> line: D4, correctness M7 / security M-3, a
    /// recovered line names a copy no line of ours did (a crashed run's, or a file someone put on a shared base), so it is unverified
    /// whoever signed it.</summary>
    private static bool Signed(IndexLine line, byte[] key) =>
        key.Length > 0 && line.Event != Events.Recovered && FixedTimeEquals(line.Mac, MacOf(line with { Mac = string.Empty }, key));

    private static IndexLine? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize(text, WslCareJsonContext.Compact.IndexLine);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>Why a parsed line is not one this reader accepts; empty when it is (the shape only — the MAC is separate).</summary>
    public static string ShapeProblem(IndexLine line) =>
        line.V != SchemaVersion ? $"schema {line.V}"
        : !Events.All.Contains(line.Event ?? string.Empty) ? "an unknown event"
        : !EntryIdShape().IsMatch(line.EntryId ?? string.Empty) ? "an entry id that is not 16 hex"
        : MissingField(line) ? "a field missing"
        : !IsPlainRelative(line.Key) ? "a key that is not a plain relative path"
        : FilesProblem(line);

    /// <summary>Correctness review M2: a field the line must carry is absent (JSON reads it as null) — malformed, never a crash later.</summary>
    private static bool MissingField(IndexLine line) =>
        line.Mac is null || line.Agent is null || line.Side is null || line.Key is null || line.Month is null || line.Zone is null || line.RunId is null || line.Files is null;

    private static string FilesProblem(IndexLine line) =>
        line.Files.Any(f => f is null) ? "a file entry that is null"
        : line.Files.FirstOrDefault(f => !IsPlainRelative(f.Original) || !IsPlainRelative(f.Archived) || !Sha256Shape().IsMatch(f.Sha256 ?? string.Empty) || f.Bytes < 0) is { } bad
            ? $"a file that is not a plain relative path with a hash ({bad.Original})"
            : string.Empty;

    /// <summary>Whether <paramref name="id"/> has an entry id's shape: 16 lower-case hex.</summary>
    public static bool IsEntryId(string id) => EntryIdShape().IsMatch(id);

    /// <summary>A path of one or more plain names joined by <c>/</c>: not rooted, no empty, <c>.</c> or <c>..</c> segment, no <c>\</c>.</summary>
    public static bool IsPlainRelative(string path) =>
        !string.IsNullOrEmpty(path) && !path.Contains('\\', StringComparison.Ordinal) && !path.StartsWith('/') && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    /// <summary>The entries of <paramref name="records"/> merged per id: files from the LATEST <c>archived</c> event (correctness M1: a
    /// damaged entry copied again names its repaired copy), the status of the last event in file order. When the entry has a verified
    /// <c>archived</c> event only its verified events count (M5 A: a planted or edited line never changes it) and it is verified;
    /// otherwise every event counts and it is not. An entry with no <c>archived</c> event has no files.</summary>
    public static IReadOnlyList<IndexEntry> Merge(IEnumerable<IndexRecord> records) =>
        [.. records.GroupBy(r => r.Line.EntryId, StringComparer.Ordinal).Select(Merged)];

    private static IndexEntry Merged(IGrouping<string, IndexRecord> events)
    {
        var verified = events.Where(e => e.Verified).ToList();
        var trusted = verified.Any(IsArchived);
        var counted = trusted ? verified : [.. events];
        var archived = counted.LastOrDefault(IsArchived);
        var last = counted[^1];
        return new IndexEntry(
            events.Key,
            last.Line.Agent,
            last.Line.Key,
            last.Line.Month,
            archived?.Line.Files ?? [],
            last.Line.Event,
            trusted,
            archived?.Line.AtUtc ?? DateTimeOffset.MinValue);
    }

    private static bool IsArchived(IndexRecord record) => record.Line.Event is Events.Archived or Events.Recovered;

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex EntryIdShape();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Shape();
}
