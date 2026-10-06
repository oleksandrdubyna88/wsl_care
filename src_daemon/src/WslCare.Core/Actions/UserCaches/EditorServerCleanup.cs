using System.Globalization;
using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.UserCaches;

/// <summary>
/// A14 (plan §5, §4.5): old VS Code Server builds — and Cursor's and Windsurf's — that NO running process uses, keeping the
/// newest <see cref="Keep"/> of each editor; and the extension folders an editor itself marked obsolete (its
/// <c>extensions/.obsolete</c>). The editor downloads a build again on demand. User-scoped: the target user's home.
/// </summary>
/// <remarks>
/// <para>A build is <c>&lt;editor&gt;/bin/&lt;commit&gt;</c> or <c>&lt;editor&gt;/cli/servers/&lt;Stable|Insiders&gt;-&lt;commit&gt;</c>;
/// "newest" is its folder's last write. A build is IN USE when any process of the distro names its commit or its folder in
/// its command line or working directory — kept, whatever its age. Conservative: an unreadable process table refuses the
/// whole action, a build whose time cannot be read counts as newest, an obsolete entry that is not a plain folder name or
/// whose folder a process names is kept. Every folder goes through <see cref="IFileSystem.DeleteDirectory"/> with the
/// editor's folder as the declared root — the deletion policy judges each one.</para>
/// <para><b>Freed bytes are measured</b>: each folder walked before, counted when it is gone after.</para>
/// </remarks>
public sealed class EditorServerCleanup : ICleanupAction
{
    /// <summary>Plan §5 A14: keep the newest 2.</summary>
    public static int Keep => Tuning.Current.Int(ConfigKeys.EditorServers.KeepNewest);

    public static readonly IReadOnlyList<string> EditorFolders = [".vscode-server", ".vscode-server-insiders", ".cursor-server", ".windsurf-server"];

    private const string BuildKind = "editor server build";
    private const string ExtensionKind = "obsolete extension";

    public ActionId Id { get; } = ActionId.Find("A14")!;

    public string Summary => $"old VS Code / Cursor / Windsurf server builds no process uses (the newest {Keep} kept), and obsolete extensions";

    public CommandScope Scope => CommandScope.User;

    /// <summary>Every editor server folder whose old builds it deletes.</summary>
    public IReadOnlyList<HomeFolder> HomeRoots { get; } = [.. EditorFolders.Select(f => HomeFolder.Of(f))];

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var what = string.Create(CultureInfo.InvariantCulture, $"editor server builds no running process uses, beyond the newest {Keep} of each editor (~/.vscode-server, ~/.cursor-server, ~/.windsurf-server), and obsolete extensions");
        if (CacheFolders.Home(context).Length == 0)
        {
            return Task.FromResult(ActionPreview.Unavailable(what, CacheFolders.NoHome(context, "the editor servers")));
        }

        if (context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var processes })
        {
            return Task.FromResult(ActionPreview.Unavailable(what, "the process table could not be read, so a build in use cannot be told"));
        }

        var targets = EditorFolders
            .Select(e => CacheFolders.UnderHome(context, e))
            .Where(context.Files.DirectoryExists)
            .SelectMany(root => Builds(context, root, processes, cancellationToken).Concat(Obsolete(context, root, processes, cancellationToken)))
            .ToList();
        var preview = ActionPreview.Of(what, targets.Count, CacheFolders.Total(targets), "each folder walked now", new Dictionary<string, long>(StringComparer.Ordinal), string.Empty, targets);
        return Task.FromResult(targets.Count == 0 ? preview with { Skip = "nothing to remove: no editor holds more than its newest builds, nor an obsolete extension" } : preview);
    }

    /// <summary>Plan §5 A14: more than <see cref="Keep"/> builds of an editor (or an obsolete extension) — i.e. a target.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} old build(s) or obsolete extension(s); the trigger is more than {Keep} builds of an editor"));

    public Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var removals = FolderRemovals.Of([.. preview.Targets.Select(t => (t, RemoveTarget(context, t)))]);
        return Task.FromResult(new ActionRun(removals.Removed.Count, CacheFolders.Total(removals.Removed), "each folder walked before, counted when it is gone after", null, null, removals.Removed, commands.Ran, string.Join("; ", removals.Failures.Take(5)))
        {
            NotRemoved = removals.NotRemoved,
        });
    }

    /// <summary>One target: its key is <c>root|folder</c> — the editor's folder (the deletion's scope) and the folder itself.</summary>
    private static FolderDeletion RemoveTarget(ActionContext context, ActionItem target)
    {
        var root = target.Key[..target.Key.IndexOf('|', StringComparison.Ordinal)];
        return CacheFolders.RemoveFolder(context, target.Key[(root.Length + 1)..], root, "A14");
    }

    /// <summary>The builds of one editor to remove: neither among the newest <see cref="Keep"/> nor in use.</summary>
    private static IEnumerable<ActionItem> Builds(ActionContext context, string root, ProcessSnapshot processes, CancellationToken cancellationToken)
    {
        var rules = context.Paths.Rules;
        var builds = context.Files.ListDirectories(rules.Join(root, "bin"))
            .Concat(context.Files.ListDirectories(rules.Join(root, "cli", "servers")))
            .Select(path => (Path: path, Commit: Commit(Leaf(path)), Written: context.Files.DirectoryLastWrite(path) is DirectoryTimeResult.Measured m ? m.ModifiedAt : DateTimeOffset.MaxValue))
            .Where(b => b.Commit.Length > 0)
            .OrderByDescending(b => b.Written)
            .ToList();
        return builds.Skip(Keep)
            .Where(b => !Named(processes, b.Commit) && !Named(processes, CacheFolders.ToDistro(context, b.Path)))
            .Select(b => new ActionItem(BuildKind, $"{Leaf(root)}/{Leaf(b.Path)}", CacheFolders.Measure(context.Files, b.Path, cancellationToken).CompleteBytes, $"last written {b.Written.UtcDateTime:yyyy-MM-dd}; no process uses it") { Key = $"{root}|{b.Path}" });
    }

    /// <summary><c>.obsolete</c> is a small JSON object (plan §15q R1.1: the user's file, read owner-checked and capped).</summary>
    private static int MaxObsoleteBytes => Tuning.Current.Int(ConfigKeys.UserFiles.MaxJsonBytes);

    /// <summary>The extension folders <c>extensions/.obsolete</c> lists that exist, are plain names and no process names.</summary>
    private static IEnumerable<ActionItem> Obsolete(ActionContext context, string root, ProcessSnapshot processes, CancellationToken cancellationToken)
    {
        var extensions = context.Paths.Rules.Join(root, "extensions");
        if (context.Files.ReadUserFile(context.Paths.Rules.Join(extensions, ".obsolete"), MaxObsoleteBytes, context.HomeFileOwner, context.Paths.Home) is not FileReadResult.Content content)
        {
            return [];
        }

        return ObsoleteNames(content.Bytes)
            .Select(name => context.Paths.Rules.Join(extensions, name))
            .Where(path => context.Files.DirectoryExists(path) && !Named(processes, CacheFolders.ToDistro(context, path)))
            .Select(path => new ActionItem(ExtensionKind, $"{Leaf(root)}/extensions/{Leaf(path)}", CacheFolders.Measure(context.Files, path, cancellationToken).CompleteBytes, "the editor marked it obsolete") { Key = $"{root}|{path}" })
            .ToList();
    }

    /// <summary>The keys of <c>.obsolete</c> (<c>{"publisher.name-1.2.3": true}</c>) that are plain folder names marked true.</summary>
    public static IReadOnlyList<string> ObsoleteNames(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? []
                : [.. document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True && IsPlainName(p.Name)).Select(p => p.Name)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The commit of a build folder: <c>07f806f9…</c> or <c>Stable-07f806f9…</c> → the hex; empty for anything else.</summary>
    public static string Commit(string folder)
    {
        var hex = WithoutQuality(folder);
        return hex.Length is >= 7 and <= 64 && hex.All(char.IsAsciiHexDigitLower) ? hex : string.Empty;
    }

    /// <summary>The folder name without a <c>Stable-</c> / <c>Insiders-</c> / <c>Exploration-</c> prefix.</summary>
    private static string WithoutQuality(string folder)
    {
        var dash = folder.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && folder[..dash] is "Stable" or "Insiders" or "Exploration" ? folder[(dash + 1)..] : folder;
    }

    private static bool IsPlainName(string name) =>
        Checks.All(name, n => n.Length is > 0 and <= 255, n => n is not ("." or ".."), n => n.IndexOfAny(['/', '\\', ':']) < 0, n => !n.Any(char.IsControl));

    private static bool Named(ProcessSnapshot processes, string text) =>
        processes.All.Any(p => p.CommandLine.Contains(text, StringComparison.Ordinal) || p.Cwd.Map(c => c.Contains(text, StringComparison.Ordinal)).ValueOr(false));

    private static string Leaf(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));
}
