using System.Globalization;
using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Config;

/// <summary>How a write to the user layer ended.</summary>
public abstract record UserConfigWriteResult
{
    private UserConfigWriteResult()
    {
    }

    /// <summary>The file was written.</summary>
    /// <param name="File">The user layer's path.</param>
    /// <param name="KeyWasPresent">Whether the key was in the file before (a reset of an absent key is a no-op that says so).</param>
    /// <param name="DroppedKeys">Keys the old file held that did not validate and were not carried over.</param>
    /// <param name="MovedAsideTo">Where an unparseable old file was moved; empty when it was readable.</param>
    public sealed record Written(string File, bool KeyWasPresent, IReadOnlyList<string> DroppedKeys, string MovedAsideTo) : UserConfigWriteResult;

    /// <summary>The deletion policy refused the write — a bug in the layout, since the user's config directory is never a protected place.</summary>
    public sealed record Refused(DeletionVerdict.Refused Verdict) : UserConfigWriteResult;
}

/// <summary>
/// <c>config set</c> and <c>config reset</c>: rewrites ONLY the user layer (plan §6), atomically,
/// and repairs it on the way (plan §15a #1 — the commands work when the file is broken).
/// </summary>
/// <remarks>
/// <para>Repair means: the keys of the old file that still validate are kept, the ones that do not
/// are dropped and named in the result, and a file that cannot be parsed at all is MOVED aside to
/// <c>config.json.broken-{utc stamp}</c> (with <c>-2</c>, <c>-3</c>, … when that name is already
/// taken) rather than overwritten — the user's text is never silently discarded. The move and the
/// write both go through <see cref="IFileSystem"/>, so the deletion policy sees them like everything
/// else.</para>
/// <para>The file is written nested (<c>{"volumes":{"anonymousMaxGb":25}}</c>), indented, keys in
/// schema order — the shape a person expects to open in an editor.</para>
/// </remarks>
public sealed class UserConfigWriter(IHostPaths paths, IFileSystem files, TimeProvider clock)
{
    public const string ActionName = "config-set";

    /// <summary>How many <c>-n</c> suffixes the aside name tries before falling back to a GUID.</summary>
    private const int MaxAsideProbes = 100;

    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    public UserConfigWriteResult Set(ConfigKey key, ConfigValue value) =>
        Write(key, entries => new Dictionary<string, ConfigValue>(entries, StringComparer.Ordinal) { [key.Name] = value });

    public UserConfigWriteResult Reset(ConfigKey key) =>
        Write(key, entries => entries.Where(e => e.Key != key.Name).ToDictionary(StringComparer.Ordinal));

    private UserConfigWriteResult Write(ConfigKey key, Func<IReadOnlyDictionary<string, ConfigValue>, IReadOnlyDictionary<string, ConfigValue>> change)
    {
        var file = paths.UserConfigFile;
        var directory = Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"the user config file {file} has no directory");
        var current = ReadCurrent(file, directory);
        var next = change(current.Entries);

        files.CreateDirectory(directory);
        var verdict = files.WriteFileAtomically(file, Render(next), new DeletionScope(directory, ActionName));
        return verdict is DeletionVerdict.Refused refused
            ? new UserConfigWriteResult.Refused(refused)
            : new UserConfigWriteResult.Written(file, current.Entries.ContainsKey(key.Name), current.Dropped, current.MovedAsideTo);
    }

    private sealed record Current(IReadOnlyDictionary<string, ConfigValue> Entries, IReadOnlyList<string> Dropped, string MovedAsideTo);

    /// <remarks>Bounded (plan §15q R1.1, review minor): a FIFO in the layer's place is refused at once and moved aside, never
    /// waited on; a larger file than a layer may hold is moved aside too.</remarks>
    private Current ReadCurrent(string file, string directory) => files.ReadRegularFile(file, ConfigLoader.MaxLayerBytes) switch
    {
        FileReadResult.Missing => new Current(new Dictionary<string, ConfigValue>(), [], string.Empty),
        FileReadResult.Unreadable => new Current(new Dictionary<string, ConfigValue>(), [], MoveAside(file, directory)),
        FileReadResult.Content content => ReadParsed(ConfigDocument.Parse(content.Bytes), file, directory),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    private Current ReadParsed(ConfigDocumentResult document, string file, string directory) => document switch
    {
        ConfigDocumentResult.Malformed => new Current(new Dictionary<string, ConfigValue>(), [], MoveAside(file, directory)),
        ConfigDocumentResult.Parsed parsed => KeepValid(parsed.Entries),
        _ => throw new System.Diagnostics.UnreachableException("ConfigDocumentResult is a closed set"),
    };

    /// <summary>The leaves that still validate are kept; every other leaf is dropped and named.</summary>
    private static Current KeepValid(IReadOnlyList<RawEntry> entries)
    {
        var kept = new Dictionary<string, ConfigValue>(StringComparer.Ordinal);
        var dropped = new List<string>();
        foreach (var entry in entries)
        {
            var key = ConfigKeys.Find(entry.Key);
            if (key is not null && ConfigValidation.Check(key, entry.Value) is ValueCheck.Ok ok)
            {
                kept[key.Name] = ok.Value;
            }
            else
            {
                dropped.Add(entry.Key);
            }
        }

        return new Current(kept, dropped, string.Empty);
    }

    /// <summary>An unparseable file is moved beside itself with a UTC stamp — never overwritten.</summary>
    private string MoveAside(string file, string directory)
    {
        var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var aside = FreeAsideName(file, stamp);
        var scope = new DeletionScope(directory, ActionName);
        var verdict = files.DirectoryExists(file) ? files.MoveDirectory(file, aside, scope) : files.MoveFile(file, aside, scope);
        if (verdict is DeletionVerdict.Refused refused)
        {
            // The user's own config directory can never be a protected place; this is a layout defect.
            throw new InvalidOperationException($"could not move the broken user config aside: {refused.Reason}");
        }

        return aside;
    }

    /// <summary>
    /// <c>{file}.broken-{stamp}</c>, or <c>-2</c>, <c>-3</c>, … after it when that name is taken: the
    /// stamp has whole-second precision, so a second repair in the same second asked for the first
    /// one's name and the move failed. The move itself never overwrites (it fails if the name was
    /// taken between this probe and the move), so the probe only has to find a name, not reserve one.
    /// Past <see cref="MaxAsideProbes"/> taken names a GUID suffix ends the search.
    /// </summary>
    private string FreeAsideName(string file, string stamp) =>
        Enumerable.Range(1, MaxAsideProbes)
            .Select(n => n == 1 ? $"{file}.broken-{stamp}" : $"{file}.broken-{stamp}-{n}")
            .FirstOrDefault(candidate => !files.FileExists(candidate) && !files.DirectoryExists(candidate))
        ?? $"{file}.broken-{stamp}-{Guid.NewGuid():N}";

    private static byte[] Render(IReadOnlyDictionary<string, ConfigValue> entries)
    {
        var ordered = ConfigKeys.All
            .Where(k => entries.ContainsKey(k.Name))
            .Select(k => (Segments: (IReadOnlyList<string>)k.Name.Split('.'), Value: entries[k.Name]))
            .ToList();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented))
        {
            WriteObject(writer, ordered, level: 0);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Dotted keys become nested objects: items sharing a segment at this level form one object.</summary>
    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyList<(IReadOnlyList<string> Segments, ConfigValue Value)> items, int level)
    {
        writer.WriteStartObject();
        foreach (var group in items.GroupBy(i => i.Segments[level], StringComparer.Ordinal))
        {
            writer.WritePropertyName(group.Key);
            var members = group.ToList();
            if (members.Count == 1 && members[0].Segments.Count == level + 1)
            {
                members[0].Value.WriteTo(writer);
            }
            else
            {
                WriteObject(writer, members, level + 1);
            }
        }

        writer.WriteEndObject();
    }
}
