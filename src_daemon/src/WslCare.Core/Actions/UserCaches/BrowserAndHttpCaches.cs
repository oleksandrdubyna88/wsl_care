using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.UserCaches;

/// <summary>What the Playwright cache holds: the browser folders no project references (with their sizes), or why the
/// action cannot tell — then it removes none (never <c>uninstall --all</c>).</summary>
public sealed record PlaywrightVerdict(IReadOnlyList<ActionItem> Unreferenced, IReadOnlyList<string> Referenced, string Refusal);

/// <summary>
/// A12 (plan §5), a BUTTON only: the Playwright browsers no project references, and NuGet's <c>http-cache</c> — caches,
/// but large re-downloads, so the timer never runs it. User-scoped: the target user's <c>~/.cache/ms-playwright</c> and
/// <c>dotnet nuget locals http-cache --clear</c> run as that user.
/// </summary>
/// <remarks>
/// <para><b>"Referenced" is Playwright's own rule</b> (its registry's install-cache validation): every project that ran
/// <c>playwright install</c> left a file under <c>.links/</c> naming its <c>playwright-core</c> folder; that folder's
/// <c>browsers.json</c> names the revisions it uses, and the browser folder of one is
/// <c>&lt;name with '-' as '_'&gt;-&lt;revision&gt;</c>. A link whose package is gone is stale (Playwright drops it too) and
/// references nothing. CONSERVATIVE everywhere else: no <c>.links/</c> or an empty one, a link file that cannot be read, a
/// package whose <c>browsers.json</c> is missing or unreadable, a running install (<c>__dirlock</c>), an unreadable process
/// table — each refuses the Playwright part whole. A folder some running process names in its command line or working
/// directory is kept. Every folder is deleted through <see cref="IFileSystem.DeleteDirectory"/> with the cache as the
/// declared root, so the deletion policy judges each one (a link into <c>~/git</c> or an agent folder is refused).</para>
/// <para><b>Freed bytes are measured</b>: each folder's size walked before, counted when the folder is gone after; the
/// http-cache walked before and after NuGet cleared it.</para>
/// </remarks>
public sealed class BrowserAndHttpCaches : ICleanupAction
{
    public static readonly CommandTemplate NugetHttpCacheClear = new(
        "dotnet-nuget-locals-http-cache-clear",
        CommandScope.User,
        "dotnet",
        [new ArgPart.Literal("nuget"), new ArgPart.Literal("locals"), new ArgPart.Literal("http-cache"), new ArgPart.Literal("--clear")],
        TimeSpan.FromMinutes(10),
        CommandRequest.DefaultOutputCapChars);

    private const string LinksFolder = ".links";
    private const string InstallLock = "__dirlock";
    private const string HttpCacheKind = "nuget http-cache";
    private const string BrowserKind = "playwright browser";

    public ActionId Id { get; } = ActionId.Find("A12")!;

    public string Summary => "Playwright browsers no project references, and NuGet's http-cache (a button only)";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [NugetHttpCacheClear];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        const string what = "Playwright browsers no project references (~/.cache/ms-playwright), and NuGet's http-cache (~/.local/share/NuGet/http-cache)";
        if (CacheFolders.Home(context).Length == 0)
        {
            return Task.FromResult(ActionPreview.Unavailable(what, CacheFolders.NoHome(context, "the caches")));
        }

        var browsers = Playwright(context, cancellationToken);
        IReadOnlyList<ActionItem> targets = [.. browsers.Unreferenced, .. HttpCache(context, commands, cancellationToken)];
        var notes = browsers.Refusal.Length > 0 ? $"; the Playwright part refuses: {browsers.Refusal}" : string.Empty;
        var preview = ActionPreview.Of(what + notes, targets.Count, CacheFolders.Total(targets), "each folder walked now", new Dictionary<string, long>(StringComparer.Ordinal), string.Empty, targets);
        return Task.FromResult(targets.Count == 0 ? preview with { Skip = $"nothing to remove{notes}" } : preview);
    }

    /// <summary>A button only (plan §5): the timer's trigger never fires.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => new(false, "A12 is a button only (plan 5): browsers and packages are large re-downloads");

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var root = Root(context);
        var browsers = FolderRemovals.Of([.. preview.Targets.Where(t => t.Kind == BrowserKind).Select(b => (b, CacheFolders.RemoveFolder(context, b.Key, root, "A12")))]);
        var (http, httpFailures) = await ClearHttpCacheAsync(context, preview, commands, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ActionItem> removed = [.. browsers.Removed, .. http];
        return new ActionRun(removed.Count, CacheFolders.Total(removed), "each browser folder's size walked before, counted when it is gone after; the http-cache walked before and after", null, null, removed, commands.Ran, string.Join("; ", [.. browsers.Failures, .. httpFailures]))
        {
            NotRemoved = browsers.NotRemoved,
        };
    }

    /// <summary>NuGet's http-cache, when the preview holds it: cleared by <c>dotnet</c> as the user, measured before and after.</summary>
    private static async Task<(IReadOnlyList<ActionItem> Removed, IReadOnlyList<string> Failures)> ClearHttpCacheAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.FirstOrDefault(t => t.Kind == HttpCacheKind) is not { } http)
        {
            return ([], []);
        }

        var before = CacheFolders.Measure(context.Files, http.Key, cancellationToken);
        var failure = CommandFailures.Of("dotnet nuget locals http-cache --clear", await commands.RunAsync(NugetHttpCacheClear, [], cancellationToken).ConfigureAwait(false));
        var freed = CacheFolders.Freed(before, CacheFolders.Measure(context.Files, http.Key, cancellationToken));
        return ([http with { Bytes = freed, Note = freed is null ? "freed unknown: a walk was cut or unreadable" : string.Empty }], failure.Length > 0 ? [failure] : []);
    }

    /// <summary>The verdict over the target user's Playwright cache — pure over the file system and the process table.</summary>
    public static PlaywrightVerdict Playwright(ActionContext context, CancellationToken cancellationToken)
    {
        var root = Root(context);
        if (!context.Files.DirectoryExists(root))
        {
            return new PlaywrightVerdict([], [], string.Empty);
        }

        var referenced = Referenced(context, root);
        return referenced.Refusal.Length > 0 ? new PlaywrightVerdict([], [], referenced.Refusal) : Unreferenced(context, root, referenced.Names, cancellationToken);
    }

    /// <summary>The browser folders no live link references and no process uses — unless an install is running or the
    /// process table cannot be read, which refuse the Playwright part whole.</summary>
    private static PlaywrightVerdict Unreferenced(ActionContext context, string root, IReadOnlyList<string> referenced, CancellationToken cancellationToken)
    {
        if (InstallRunning(context, root))
        {
            return new PlaywrightVerdict([], referenced, $"a Playwright install is running ({InstallLock} exists in {root})");
        }

        if (context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var processes })
        {
            return new PlaywrightVerdict([], referenced, "the process table could not be read, so a browser in use cannot be told");
        }

        var unreferenced = context.Files.ListDirectories(root)
            .Select(d => (Path: d, Name: Leaf(d)))
            .Where(d => Checks.All(d, f => IsBrowserFolder(f.Name), f => !referenced.Contains(f.Name, StringComparer.Ordinal), f => !InUse(context, f.Path, processes)))
            .Select(d => new ActionItem(BrowserKind, d.Name, CacheFolders.Measure(context.Files, d.Path, cancellationToken).CompleteBytes, "no project's browsers.json references it") { Key = d.Path })
            .ToList();
        return new PlaywrightVerdict(unreferenced, referenced, string.Empty);
    }

    private static bool InstallRunning(ActionContext context, string root) =>
        context.Files.FileExists(context.Paths.Rules.Join(root, InstallLock)) || context.Files.DirectoryExists(context.Paths.Rules.Join(root, InstallLock));

    /// <summary>The browser folder names every live link's <c>browsers.json</c> references — or why they cannot be told.</summary>
    private static (IReadOnlyList<string> Names, string Refusal) Referenced(ActionContext context, string root)
    {
        var links = context.Files.ListFiles(context.Paths.Rules.Join(root, LinksFolder));
        if (links.Count == 0)
        {
            return ([], "no project has left a link under .links/, so which browsers are referenced cannot be told (and never all are removed)");
        }

        var names = new List<string>();
        foreach (var link in links)
        {
            var (revisions, problem) = LinkRevisions(context, link);
            if (problem.Length > 0)
            {
                return ([], problem);
            }

            names.AddRange(revisions);
        }

        return ([.. names.Distinct(StringComparer.Ordinal)], string.Empty);
    }

    /// <summary>One link: the package folder it names; a package that is gone references nothing (stale).</summary>
    private static (IReadOnlyList<string> Names, string Problem) LinkRevisions(ActionContext context, string link)
    {
        if (context.Files.ReadFile(link) is not FileReadResult.Content content)
        {
            return ([], $"the link {link} could not be read");
        }

        var package = System.Text.Encoding.UTF8.GetString(content.Bytes).Trim();
        return package.StartsWith('/') && !package.Any(char.IsControl) ? PackageRevisions(context, package) : ([], $"the link {link} does not name an absolute folder");
    }

    /// <summary>The browser folders one package's <c>browsers.json</c> declares; none for a package that is gone (a stale link).</summary>
    private static (IReadOnlyList<string> Names, string Problem) PackageRevisions(ActionContext context, string package)
    {
        var linux = (LinuxHostPaths)context.Paths;
        var folder = linux.DistroPath(package);
        if (!context.Files.DirectoryExists(folder))
        {
            return ([], string.Empty);
        }

        return context.Files.ReadFile(linux.Rules.Join(folder, "browsers.json")) is FileReadResult.Content json && BrowserFolders(json.Bytes) is { } folders
            ? (folders, string.Empty)
            : ([], $"{package}/browsers.json is missing or not Playwright's browsers list, so what that project uses cannot be told");
    }

    /// <summary>The folder names <c>browsers.json</c> declares: <c>chromium-headless-shell</c> 1228 →
    /// <c>chromium_headless_shell-1228</c>; <c>null</c> when it is not that shape.</summary>
    public static IReadOnlyList<string>? BrowserFolders(byte[] browsersJson)
    {
        try
        {
            using var document = JsonDocument.Parse(browsersJson);
            return FoldersOf(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? FoldersOf(JsonElement root)
    {
        if (!root.TryGetProperty("browsers", out var browsers) || browsers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = browsers.EnumerateArray()
            .Select(b => (Name: Docker.DockerText.Field(b, "name"), Revision: Docker.DockerText.Field(b, "revision")))
            .ToList();
        return names.All(IsDeclared) ? [.. names.Select(n => $"{n.Name.Replace('-', '_')}-{n.Revision}")] : null;
    }

    /// <summary>A browser entry with a name and a numeric revision.</summary>
    private static bool IsDeclared((string Name, string Revision) browser) =>
        browser.Name.Length > 0 && browser.Revision.Length > 0 && browser.Revision.All(char.IsAsciiDigit);

    /// <summary><c>chromium-1228</c>, <c>chromium_headless_shell-1228</c>, <c>ffmpeg-1011</c>: a browser folder Playwright
    /// installs, and nothing else under its cache.</summary>
    public static bool IsBrowserFolder(string name)
    {
        var dash = name.LastIndexOf('-');
        return dash > 0 && dash < name.Length - 1 && name[..dash].All(IsBrowserNameChar) && name[(dash + 1)..].All(char.IsAsciiDigit);
    }

    private static bool IsBrowserNameChar(char c) => char.IsAsciiLetterLower(c) || c == '_';

    private static bool InUse(ActionContext context, string folder, ProcessSnapshot processes)
    {
        var distro = CacheFolders.ToDistro(context, folder);
        return processes.All.Any(p => p.CommandLine.Contains(distro, StringComparison.Ordinal) || p.Cwd.Map(c => c.StartsWith(distro, StringComparison.Ordinal)).ValueOr(false));
    }

    /// <summary>NuGet's http-cache, when it exists and <c>dotnet</c> is installed for the user (otherwise nothing to do).</summary>
    private static IReadOnlyList<ActionItem> HttpCache(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var cache = CacheFolders.UnderHome(context, ".local", "share", "NuGet", "http-cache");
        if (!context.Files.DirectoryExists(cache) || commands.Locate(NugetHttpCacheClear) is ResolvedExecutable.NotFound)
        {
            return [];
        }

        return [new ActionItem(HttpCacheKind, cache, CacheFolders.Measure(context.Files, cache, cancellationToken).CompleteBytes) { Key = cache }];
    }

    private static string Root(ActionContext context) => CacheFolders.UnderHome(context, ".cache", "ms-playwright");

    private static string Leaf(string path) => path.Replace('\\', '/').TrimEnd('/')[(path.Replace('\\', '/').TrimEnd('/').LastIndexOf('/') + 1)..];
}
