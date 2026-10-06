using WslCare.Core.Agents;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Cli;

/// <summary>
/// Which manual agent folders phase two of the host protects (plan §15q R2.2, B2; E7.S1/S2 review S1). Protection is never
/// unbounded: a folder — as spelt AND as it really is — joins the protected roots only when it lies strictly inside a protected
/// home, is no filesystem root, and is neither one of wsl-care's own folders nor holds one. Anything else (<c>/</c>,
/// <c>/var/lib/wsl-care</c>, a drive root) would make root refuse its OWN state writes — a user→root denial — and is dropped
/// with a notice. B2 still holds: every folder a cleanup cleans is inside the home, so every extra that could overlap one is kept.
/// </summary>
/// <param name="Kept">The folders protected, as this process sees them (spelt and real).</param>
/// <param name="Dropped">One sentence per folder not protected.</param>
public sealed record ExtraRoots(IReadOnlyList<string> Kept, IReadOnlyList<string> Dropped)
{
    public static readonly ExtraRoots None = new([], []);

    public static ExtraRoots ForDistro(LinuxHostPaths paths, IFileSystem files, IReadOnlyList<string> spelt)
    {
        var homes = paths.Homes;
        var product = ExtraAgentRules.ProductFolders(paths);
        return Chosen(spelt, s => paths.DistroPath(s), files, place => homes.Any(h => Rules.IsStrictlyUnder(place, h)) && !Holds(place, product));
    }

    public static ExtraRoots ForWindows(WindowsHostPaths paths, IFileSystem files, IReadOnlyList<string> spelt)
    {
        var product = ExtraAgentRules.ProductFolders(paths);
        return Chosen(spelt, s => s, files, place => PathRules.Windows.IsStrictlyUnder(place, paths.Home) && !PathRules.Windows.IsRoot(place) && !Holds(place, product));
    }

    private static readonly PathRules Rules = PathRules.ForThisOs;

    private static ExtraRoots Chosen(IReadOnlyList<string> spelt, Func<string, string> onDisk, IFileSystem files, Func<string, bool> allowed)
    {
        var judged = spelt.Select(s => (Spelt: s, Places: Places(onDisk(s), files))).ToList();
        return new ExtraRoots(
            [.. judged.Where(j => j.Places.All(allowed)).SelectMany(j => j.Places).Distinct(StringComparer.Ordinal)],
            [.. judged.Where(j => !j.Places.All(allowed)).Select(j => $"{j.Spelt} is not protected: a manual agent's folder must lie inside a home and neither be nor hold wsl-care's own folders (plan §15q R2, review S1)")]);
    }

    /// <summary>The folder as spelt and as it really is.</summary>
    private static IReadOnlyList<string> Places(string path, IFileSystem files) =>
        files.ResolvePath(path) is RealPathResult.Resolved real ? [Path.GetFullPath(path), real.Path] : [Path.GetFullPath(path)];

    private static bool Holds(string place, IReadOnlyList<string> product) =>
        product.Any(p => ExtraAgentRules.Overlaps(place, Path.GetFullPath(p)));
}
