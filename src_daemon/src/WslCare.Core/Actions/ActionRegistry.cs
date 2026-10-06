namespace WslCare.Core.Actions;

/// <summary>
/// The actions a build holds, by id — a CLOSED set: <see cref="Product"/> is built here and nowhere else, its templates
/// are the action half of the policy's catalogue, and an id the registry does not hold is refused by name ("not built
/// yet") rather than run.
/// </summary>
/// <remarks>E3.S1 registered A10 (the journal vacuum), the reference that proved the engine end to end; E3.S2 the
/// irreversible deletions A4–A9, A11, A12, A14, A17 (with A5Testcontainers and A6Unused, the second switches of A5 and A6);
/// E3.S3 A1, A2, A3, A15, A16 — so every action but A13 (the archive, E9's) is built.</remarks>

public sealed class ActionRegistry
{
    public ActionRegistry(IReadOnlyList<ICleanupAction> actions)
    {
        if (actions.GroupBy(a => a.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ArgumentException($"the action {twice.Key} is registered twice", nameof(actions));
        }

        Actions = actions;
    }

    /// <summary>What this build can run.</summary>
    public static ActionRegistry Product { get; } = new(
    [
        new DockerCleanups.VolumeRemoval(),
        new DockerCleanups.ContainerRemoval(testcontainers: false),
        new DockerCleanups.ContainerRemoval(testcontainers: true),
        new DockerCleanups.ImagePrune(unused: false),
        new DockerCleanups.ImagePrune(unused: true),
        new DockerCleanups.BuildCachePrune(),
        new UserCaches.NpmCacheClean(),
        new PackageCaches.PackageCacheClean(),
        new JournalVacuum(),
        new Suspects.SuspectTermination(),
        new Suspects.AgentOrphans(),
        new UserCaches.BrowserAndHttpCaches(),
        new UserCaches.EditorServerCleanup(),
        new UserCaches.ToolCacheTrims(),
        new Memory.CacheDrop(),
        new Memory.Compaction(),
        new BuildServers.BuildServerShutdown(),
        new Disk.FilesystemTrim(),
        new Clock.ClockFix(),
    ]);

    public IReadOnlyList<ICleanupAction> Actions { get; }

    public ICleanupAction? Find(ActionId id) => Actions.FirstOrDefault(a => a.Id == id);

    /// <summary>The registered actions among <paramref name="ids"/>, in <see cref="ActionId.ExecutionOrder"/>.</summary>
    public IReadOnlyList<ICleanupAction> InExecutionOrder(IReadOnlyList<ActionId> ids) =>
        [.. ActionId.ExecutionOrder.Where(ids.Contains).Select(Find).OfType<ICleanupAction>()];
}
