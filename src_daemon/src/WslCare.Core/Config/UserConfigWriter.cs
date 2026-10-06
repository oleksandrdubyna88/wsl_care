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
    public sealed record Written(string File, bool KeyWasPresent, IReadOnlyList<string> DroppedKeys, string MovedAsideTo) : UserConfigWriteResult
    {
        /// <summary>The layer was a link (E7.S0 review C3): the LINK was replaced by a regular file holding the values read
        /// through it — the file it pointed at untouched.</summary>
        public bool ReplacedLink { get; init; }

        /// <summary>The repair LOST something the person wrote — an unparseable file, or an entry other than the one being
        /// written that no longer validated — so <c>dryRun = true</c> was written into the new layer (retro gate over PR #4): a
        /// lost <c>auto.A4 = false</c> is <c>auto.A4 = true</c> by default, and the timer must not act on that unseen.</summary>
        public bool PinnedDryRun { get; init; }
    }

    /// <summary>The deletion policy refused the write — a bug in the layout, since the user's config directory is never a protected place.</summary>
    public sealed record Refused(DeletionVerdict.Refused Verdict) : UserConfigWriteResult;

    /// <summary>The rendered layer would be larger than its own reader takes (E7.S1/S2 review R10): nothing was written.</summary>
    public sealed record TooLarge(int Bytes, int Max) : UserConfigWriteResult;

    /// <summary>The value would break a coupled rule with the layers below (E7.S2b/S2c review C-M1): the loader would not take it,
    /// so it is not written.</summary>
    public sealed record BreaksRule(string Message) : UserConfigWriteResult;
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
/// <para><b>A lossy repair turns the timer dry</b> (retro gate over PR #4; plan §15a #1 says a broken layer is never silently
/// replaced by defaults, because a default can re-enable an action the person switched off). When the repair discards
/// anything but the key being written, the new layer also carries <c>dryRun = true</c> — unless the command itself writes
/// <c>dryRun</c>, which is the person deciding it. The move aside happens only once every refusal (too large, a broken
/// coupled rule) has been asked, so a refused command leaves the broken file exactly where it was.</para>
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
        Write(key, writesDryRun: key == ConfigKeys.DryRun, entries => new Dictionary<string, ConfigValue>(entries, StringComparer.Ordinal) { [key.Name] = value });

    public UserConfigWriteResult Reset(ConfigKey key) =>
        Write(key, writesDryRun: false, entries => entries.Where(e => e.Key != key.Name).ToDictionary(StringComparer.Ordinal));

    private UserConfigWriteResult Write(ConfigKey key, bool writesDryRun, Func<IReadOnlyDictionary<string, ConfigValue>, IReadOnlyDictionary<string, ConfigValue>> change)
    {
        // Review C-M4: the cap the READER keeps — config.maxLayerBytes in force — not the key's range maximum.
        var cap = ConfigLoader.UserLayerCap(paths, files);
        var current = ReadCurrent(paths.UserConfigFile, cap);
        var pin = !writesDryRun && LosesSomething(current, key);
        var next = change(current.Entries);
        var rendered = Render(pin ? WithDryRun(next) : next);
        return Refusal(key, rendered, cap) ?? Commit(key, current, rendered, pin);
    }

    /// <summary>What the repair would lose besides the key being written. Nothing is pinned when the command WRITES dryRun — the
    /// person deciding it; a reset of dryRun writes nothing and is pinned like any other (fix-PR code round).</summary>
    private static bool LosesSomething(Current current, ConfigKey key) =>
        current.Broken || current.Dropped.Any(d => d != key.Name);

    private static Dictionary<string, ConfigValue> WithDryRun(IReadOnlyDictionary<string, ConfigValue> entries) =>
        new(entries, StringComparer.Ordinal) { [ConfigKeys.DryRun.Name] = new ConfigValue.Bool(true) };

    /// <summary>The refusals that write nothing — asked BEFORE a broken layer is moved aside, so a refused command moves nothing.</summary>
    private UserConfigWriteResult? Refusal(ConfigKey key, byte[] rendered, int cap)
    {
        if (rendered.Length > cap)
        {
            return new UserConfigWriteResult.TooLarge(rendered.Length, cap);
        }

        return RuleBroken(key, rendered) is { Length: > 0 } broken ? new UserConfigWriteResult.BreaksRule(broken) : null;
    }

    private UserConfigWriteResult Commit(ConfigKey key, Current current, byte[] rendered, bool pinnedDryRun)
    {
        var file = paths.UserConfigFile;
        var directory = Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"the user config file {file} has no directory");
        var aside = current.Broken ? SetAside(file, directory, current) : Aside.None;
        var (verdict, linked) = WriteOrUndo(file, directory, rendered, aside);
        return verdict is DeletionVerdict.Refused refused
            ? new UserConfigWriteResult.Refused(refused)
            : new UserConfigWriteResult.Written(file, current.Entries.ContainsKey(key.Name), current.Dropped, aside.Path) { ReplacedLink = linked, PinnedDryRun = pinnedDryRun };
    }

    /// <summary>Where the broken layer went: COPIED when its bytes were read (the layer stays in place until its replacement is
    /// renamed over it, so a process killed in between leaves the person's file, not none — fix-PR code round), MOVED when it
    /// could not be read (a FIFO, a folder, a file over the cap).</summary>
    private sealed record Aside(string Path, bool Copied)
    {
        public static readonly Aside None = new(string.Empty, false);
    }

    private Aside SetAside(string file, string directory, Current current) =>
        current.WasRead ? new Aside(CopyAside(file, directory, current.Text), Copied: true) : new Aside(MoveAside(file, directory), Copied: false);

    /// <summary>The write — and, when it does not happen, the aside undone (fix-PR plan round, retro gate over PR #4): a move aside
    /// followed by a failed write left no user layer at all, and the next run loaded the defaults.</summary>
    private LayerWrite WriteOrUndo(string file, string directory, byte[] rendered, Aside aside)
    {
        try
        {
            var written = WriteLayer(file, directory, rendered);
            if (written.Verdict is DeletionVerdict.Refused)
            {
                Undo(aside, file, directory);
            }

            return written;
        }
        catch
        {
            // Every failure, not only I/O (fix-PR final round): whatever ended the write, the person's layer goes back first.
            Undo(aside, file, directory);
            throw;
        }
    }

    private LayerWrite WriteLayer(string file, string directory, byte[] rendered)
    {
        // E7.S0 review C3: root never follows a link, so a linked layer is one root refuses; the repair replaces the LINK (the
        // file it points at is never written) with a regular file holding the values read through it.
        var linked = files.ReadLink(file) is LinkReadResult.Target;
        files.CreateDirectory(directory);
        var verdict = linked
            ? files.ReplaceLinkWithFile(file, rendered, new DeletionScope(directory, ActionName))
            : files.WriteFileAtomically(file, rendered, new DeletionScope(directory, ActionName));
        return new LayerWrite(verdict, linked);
    }

    /// <summary>How the layer's write ended, and whether it replaced a link.</summary>
    private sealed record LayerWrite(DeletionVerdict Verdict, bool Linked);

    /// <summary>A copy aside is removed (the layer never left); a move aside is moved back. A refusal of either is SAID, never
    /// swallowed (fix-PR code round): the person must learn where their file is.</summary>
    private void Undo(Aside aside, string file, string directory)
    {
        if (NothingToUndo(aside, file))
        {
            return;
        }

        var scope = new DeletionScope(directory, ActionName);
        var verdict = aside.Copied ? files.DeleteFile(aside.Path, scope) : MoveBack(aside.Path, file, scope);
        if (verdict is DeletionVerdict.Refused refused)
        {
            throw new InvalidOperationException($"could not put the broken user layer back from {aside.Path}: {refused.Reason}");
        }
    }

    /// <summary>No aside — or a moved one whose place is taken again (the write did land), which nothing may overwrite.</summary>
    private bool NothingToUndo(Aside aside, string file) => aside.Path.Length == 0 || (!aside.Copied && Exists(file));

    private DeletionVerdict MoveBack(string aside, string file, DeletionScope scope) =>
        files.DirectoryExists(aside) ? files.MoveDirectory(aside, file, scope) : files.MoveFile(aside, file, scope);

    private bool Exists(string path) => files.FileExists(path) || files.DirectoryExists(path);

    /// <summary>The layer as read: the entries that still validate, the ones dropped, and whether it could not be read at all
    /// (then it is moved aside — but only when the write goes ahead).</summary>
    private sealed record Current(IReadOnlyDictionary<string, ConfigValue> Entries, IReadOnlyList<string> Dropped, bool Broken)
    {
        /// <summary>A broken layer's bytes, when they could be read; empty otherwise.</summary>
        public byte[] Text { get; init; } = [];

        /// <summary>The bytes WERE read — an empty file too (fix-PR consultation): such a layer is copied aside, never moved.</summary>
        public bool WasRead { get; init; }
    }

    /// <summary>Review C-M1: what the loader would say of <paramref name="key"/> in this new layer over the layers below — a coupled
    /// rule it breaks is the notice that it is not taken; empty when it would be taken.</summary>
    private string RuleBroken(ConfigKey key, byte[] rendered)
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            ConfigLoader.MachineLayer(paths, files),
            (new ConfigLayerFile(ConfigLayer.User, paths.UserConfigFile), new FileReadResult.Content(rendered)),
        ]);
        return loaded.Notices.Where(n => n.File.Layer == ConfigLayer.User && n.Key == key.Name && NumberRules.Rules.Any(r => r.Keys.Contains(key)))
            .Select(n => n.Message).FirstOrDefault(m => m.Contains("not taken from this layer", StringComparison.Ordinal)) ?? string.Empty;
    }

    /// <remarks>Bounded (plan §15q R1.1, review minor): a FIFO in the layer's place is refused at once and moved aside, never
    /// waited on; a larger file than a layer may hold is moved aside too.</remarks>
    private Current ReadCurrent(string file, int cap) => files.ReadRegularFile(file, cap) switch
    {
        FileReadResult.Missing => new Current(new Dictionary<string, ConfigValue>(), [], Broken: false),
        FileReadResult.Unreadable => new Current(new Dictionary<string, ConfigValue>(), [], Broken: true),
        FileReadResult.Content content => ReadParsed(content.Bytes),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };

    private static Current ReadParsed(byte[] bytes) => ConfigDocument.Parse(bytes) switch
    {
        ConfigDocumentResult.Malformed => new Current(new Dictionary<string, ConfigValue>(), [], Broken: true) { Text = bytes, WasRead = true },
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

        return new Current(kept, dropped, Broken: false);
    }

    /// <summary>An unparseable file is moved beside itself with a UTC stamp — never overwritten.</summary>
    /// <summary>The kept copy is CREATED EXCLUSIVELY, name by name (fix-PR consultation f4a0e9b4): a replacing write after a
    /// free-name probe overwrote what a concurrent repair had just kept under the same second's name.</summary>
    private string CopyAside(string file, string directory, byte[] text)
    {
        var scope = new DeletionScope(directory, ActionName);
        foreach (var candidate in AsideNames(file, Stamp()))
        {
            var created = files.CreateFileExclusively(candidate, text, scope);
            if (created is ExclusiveCreate.Refused refused)
            {
                throw new InvalidOperationException($"could not keep the broken user config aside: {refused.Reason}");
            }

            if (created is ExclusiveCreate.Created)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"could not keep the broken user config aside: every name tried beside {file} is taken");
    }

    private string Stamp() => clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private string MoveAside(string file, string directory)
    {
        var aside = FreeAsideName(file, Stamp());
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
    private static IEnumerable<string> AsideNames(string file, string stamp) =>
        Enumerable.Range(1, MaxAsideProbes).Select(n => n == 1 ? $"{file}.broken-{stamp}" : $"{file}.broken-{stamp}-{n}")
            .Append($"{file}.broken-{stamp}-{Guid.NewGuid():N}");

    private string FreeAsideName(string file, string stamp) => AsideNames(file, stamp).First(candidate => !Exists(candidate));

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
