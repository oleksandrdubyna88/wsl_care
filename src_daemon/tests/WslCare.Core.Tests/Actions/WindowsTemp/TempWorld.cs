using System.Text;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>
/// A Windows <c>%TEMP%</c> that exists only in memory (WR-T1): a tree of named entries with write times, creation times, an owner and
/// a kind (file, folder, reparse point), behind the product's <see cref="IFileSystem"/>. Deletes are judged by the PRODUCT's
/// <see cref="DeletionPolicy"/>, so the <c>claude</c> rule and the declared root are exercised, not imitated. Nothing here touches a
/// real folder: every path is a string under <c>C:\Users\user</c>, and the inner file system it delegates to is a sandbox that no
/// overridden member reaches.
/// </summary>
internal sealed class TempWorld : DelegatingFileSystem, IDisposable
{
    public const string Sid = "S-1-5-21-1000-1000-1000-1001";
    public const string OtherSid = "S-1-5-21-1000-1000-1000-1002";

    public static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    public static readonly WindowsEnvironment Folders = new(@"C:\Users\user", @"C:\Users\user\AppData\Roaming", @"C:\Users\user\AppData\Local", @"C:\ProgramData", @"C:\Users\user\AppData\Local\Temp");

    private static readonly PathRules Rules = PathRules.Windows;

    private readonly SandboxHost _host;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

    public TempWorld() : this(new SandboxHost("fake-temp"))
    {
    }

    private TempWorld(SandboxHost host) : base(host.Files)
    {
        _host = host;
        Paths = new WindowsHostPaths(Folders);
        Policy = new DeletionPolicy(ProtectedRoots.From(Paths, p => p), Rules);
        Add(Root, EntryKind.Directory, Now.AddDays(-30), Sid);
    }

    public WindowsHostPaths Paths { get; }

    public DeletionPolicy Policy { get; }

    public static string Root => Folders.Temp;

    /// <summary>Every folder a listing was asked for, in order — what the run enumerated.</summary>
    public List<string> Listed { get; } = [];

    /// <summary>Every path a delete was asked for (allowed or not), in order.</summary>
    public List<string> DeleteAsked { get; } = [];

    /// <summary>Runs after each successful delete with the deleted path — a test changes the tree mid-walk here.</summary>
    public Action<string> AfterDelete { get; set; } = static _ => { };

    /// <summary>Runs before the listing of a folder (once per call) — a test changes the tree between two listings here.</summary>
    public Action<string> BeforeList { get; set; } = static _ => { };

    /// <summary>The refusals the policy gave, in order.</summary>
    public List<DeletionVerdict.Refused> Refusals { get; } = [];

    private sealed class Node(string path, EntryKind kind, long length, DateTimeOffset written, DateTimeOffset created, string owner)
    {
        public string Path { get; } = path;

        public EntryKind Kind { get; set; } = kind;

        public long Length { get; } = length;

        public DateTimeOffset Written { get; set; } = written;

        public DateTimeOffset Created { get; } = created;

        public string Owner { get; } = owner;
    }

    public string Full(string relative) => Rules.Join(Root, relative.Split('\\', StringSplitOptions.RemoveEmptyEntries));

    private void Add(string path, EntryKind kind, DateTimeOffset written, string owner, long length = 0, DateTimeOffset? created = null)
    {
        EnsureParent(path, written, owner);
        _nodes[Rules.Normalize(path)] = new Node(Rules.Normalize(path), kind, length, written, created ?? written, owner);
    }

    private void EnsureParent(string path, DateTimeOffset written, string owner)
    {
        var parent = Rules.Parent(path);
        if (!Rules.PathEquals(parent, path) && !_nodes.ContainsKey(Rules.Normalize(parent)) && Rules.IsStrictlyUnder(parent, Folders.UserProfile))
        {
            Add(parent, EntryKind.Directory, written, owner);
        }
    }

    public TempWorld Dir(string relative, DateTimeOffset written, string owner = Sid, DateTimeOffset? created = null)
    {
        Add(Full(relative), EntryKind.Directory, written, owner, 0, created);
        return this;
    }

    public TempWorld File(string relative, DateTimeOffset written, long length = 100, string owner = Sid, DateTimeOffset? created = null)
    {
        Add(Full(relative), EntryKind.File, written, owner, length, created);
        return this;
    }

    /// <summary>Paths whose <see cref="ResolvePath"/> answers somewhere else — a link swapped in under a path the walk already judged.</summary>
    public Dictionary<string, string> Redirected { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose stat cannot be read (access denied).</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TempWorld Link(string relative, DateTimeOffset written, string owner = Sid)
    {
        Add(Full(relative), EntryKind.Link, written, owner);
        return this;
    }

    /// <summary>A file another process holds open without sharing delete: deleting it raises a sharing violation.</summary>
    public TempWorld Hold(string relative)
    {
        _held.Add(Rules.Normalize(Full(relative)));
        return this;
    }

    public void BecomeLink(string relative) => _nodes[Rules.Normalize(Full(relative))].Kind = EntryKind.Link;

    public bool Exists(string relative) => _nodes.ContainsKey(Rules.Normalize(Full(relative)));

    public TempCursor Cursor() => TempCursors.Read(Paths, this);

    public void WriteCursor(TempCursor cursor) => TempCursors.Write(Paths, this, cursor).Should().BeEmpty();

    public string UserConfig { get; set; } = "{}";

    /// <summary>A context for one action call: the product's defaults plus <paramref name="userConfig"/>.</summary>
    public ActionContext Context(string userConfig = "{}", string sid = Sid, RunTrigger trigger = RunTrigger.Cli, DateTimeOffset? now = null) =>
        new(Paths, this, new FixedTimeProvider(now ?? Now), Load(userConfig), trigger, new TargetUserResult.None("the Windows binary has no target user"))
        {
            UserSid = sid,
        };

    public static EffectiveConfig Load(string userConfig) =>
        ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(userConfig))),
        ]) is { IsObserveOnly: false } ok ? ok.Config : throw new InvalidOperationException("the test configuration is invalid");

    public ActionCommands Commands(ICleanupAction action, ActionContext context) =>
        new(action, new RecordingCommandRunner { Policy = WslCare.Core.Processes.Policy.CommandPolicy.Product }, context.TargetUser, []);

    // ---------- the IFileSystem members the action uses ----------

    public override bool DirectoryExists(string path) => _nodes.TryGetValue(Rules.Normalize(path), out var n) && n.Kind != EntryKind.File;

    public override bool FileExists(string path) => _nodes.TryGetValue(Rules.Normalize(path), out var n) && n.Kind == EntryKind.File;

    public override IReadOnlyList<FileEntry> ListEntries(string path)
    {
        BeforeList(path);
        Listed.Add(Rules.Normalize(path));
        var parent = Rules.Normalize(path);
        return [.. _nodes.Values
            .Where(n => !Rules.PathEquals(n.Path, parent) && Rules.PathEquals(Rules.Parent(n.Path), parent))
            .OrderBy(n => Rules.Segments(n.Path)[^1], StringComparer.Ordinal)
            .Select(n => new FileEntry(Rules.Segments(n.Path)[^1], n.Kind, n.Length, n.Written) { CreatedUtc = n.Created })];
    }

    public override LinkReadResult ReadLink(string path) =>
        _nodes.TryGetValue(Rules.Normalize(path), out var n) && n.Kind == EntryKind.Link ? new LinkReadResult.Target(@"C:\elsewhere") : new LinkReadResult.NotALink();

    public override string OwnerSidOf(string path) => _nodes.TryGetValue(Rules.Normalize(path), out var n) ? n.Owner : string.Empty;

    public override DirectoryTimeResult DirectoryLastWrite(string path) =>
        Unreadable.Contains(Rules.Normalize(path)) ? new DirectoryTimeResult.Unreadable("access denied")
        : _nodes.TryGetValue(Rules.Normalize(path), out var n) && n.Kind != EntryKind.File ? new DirectoryTimeResult.Measured(n.Written) { CreatedAt = n.Created } : new DirectoryTimeResult.Missing();

    public override FileSizeResult FileSize(string path) =>
        Unreadable.Contains(Rules.Normalize(path)) ? new FileSizeResult.Unreadable("access denied")
        : _nodes.TryGetValue(Rules.Normalize(path), out var n) && n.Kind == EntryKind.File ? new FileSizeResult.Measured(n.Length, n.Written) { CreatedAt = n.Created } : new FileSizeResult.Missing();

    public override RealPathResult ResolvePath(string path) =>
        new RealPathResult.Resolved(Redirected.TryGetValue(Rules.Normalize(path), out var elsewhere) ? elsewhere : Rules.Normalize(path));

    public override void CreateDirectory(string path) => Add(path, EntryKind.Directory, Now, Sid);

    public override FileReadResult ReadFile(string path) =>
        _files.TryGetValue(Rules.Normalize(path), out var bytes) ? new FileReadResult.Content(bytes) : new FileReadResult.Missing();

    public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope)
    {
        _files[Rules.Normalize(path)] = content.ToArray();
        return DeletionVerdict.Allowed;
    }

    public override DeletionVerdict DeleteFile(string path, DeletionScope scope) => Delete(path, scope, folder: false);

    public override DeletionVerdict DeleteDirectory(string path, DeletionScope scope) => Delete(path, scope, folder: true);

    /// <summary>The non-recursive remove: a folder that still holds anything is the operating system's "directory not empty".</summary>
    public override DeletionVerdict DeleteEmptyDirectory(string path, DeletionScope scope)
    {
        var key = Rules.Normalize(path);
        if (_nodes.Keys.Any(k => Rules.IsStrictlyUnder(k, key)))
        {
            DeleteAsked.Add(key);
            throw new IOException($"The directory is not empty : '{path}'");
        }

        return Delete(path, scope, folder: false);
    }

    private DeletionVerdict Delete(string path, DeletionScope scope, bool folder)
    {
        DeleteAsked.Add(Rules.Normalize(path));
        var verdict = Policy.Decide(new DeletionRequest(FileOperation.Delete, Rules.Normalize(path), string.Empty, Rules.Normalize(scope.Root), scope.Action, scope.Permit));
        if (verdict is DeletionVerdict.Refused refused)
        {
            Refusals.Add(refused);
            return verdict;
        }

        var key = Rules.Normalize(path);
        if (_held.Contains(key))
        {
            throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
        }

        foreach (var gone in _nodes.Keys.Where(k => k == key || (folder && Rules.IsStrictlyUnder(k, key))).ToList())
        {
            _nodes.Remove(gone);
        }

        AfterDelete(path);
        return verdict;
    }

    public void Dispose() => _host.Dispose();
}
