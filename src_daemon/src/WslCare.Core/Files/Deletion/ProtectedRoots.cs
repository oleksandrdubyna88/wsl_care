using WslCare.Core.Hosting;

namespace WslCare.Core.Files.Deletion;

/// <summary>
/// The places nothing is ever deleted under, taken from <see cref="IHostPaths"/> so the collectors
/// and the policy read one list.
/// </summary>
/// <remarks>
/// Every path here must be REAL — resolved through the same walk the targets go through — or a
/// protected folder that is itself a link (a <c>~/.claude</c> pointing at another disk) would be
/// compared by its spelling while the target is compared by its destination, and the rule would
/// never fire. <see cref="PhysicalFileSystem"/> resolves them at construction; the pure constructor
/// here trusts what it is given, which is what the policy tests use.
/// </remarks>
/// <param name="Home">The home directory — never a declared root.</param>
/// <param name="AgentRoots">Plan §4.6.</param>
/// <param name="GitRoots">Plan §5, the never list.</param>
/// <param name="ClaudeTempRoots">Plan §5 and the Windows plan.</param>
public sealed record ProtectedRoots(
    string Home,
    IReadOnlyList<string> AgentRoots,
    IReadOnlyList<string> GitRoots,
    IReadOnlyList<string> ClaudeTempRoots)
{
    /// <summary>The roots as the host spells them, each passed through <paramref name="resolve"/>.</summary>
    public static ProtectedRoots From(IHostPaths paths, Func<string, string> resolve) =>
        new(
            resolve(paths.Home),
            [.. paths.AgentRoots.Select(resolve)],
            [.. paths.GitRoots.Select(resolve)],
            [.. paths.ClaudeTempRoots.Select(resolve)]);
}
