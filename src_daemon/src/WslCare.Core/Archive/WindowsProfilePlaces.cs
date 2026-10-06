using WslCare.Core.Agents;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>
/// E9.S0 review round S1 — the Windows places a base on a Windows drive must stay clear of, judged from inside the distribution
/// where the Windows side's own rules never run: the profile's agent folders, its temporary folder and Claude's in it, its
/// repositories folder and wsl-care's own folders, as the catalogue names them under the profile a full run found; and — known
/// profile or not — any profile's <c>AppData</c> and any profile's agent folder (<c>Users\&lt;anyone&gt;\.claude</c>), a profile
/// itself and the folder of all profiles, which hold them. Compared as NTFS compares: case-blind. No Windows cleanup action exists
/// yet, so no cleanup folder joins the list (Windows care, its own plan, adds them).
/// </summary>
public static class WindowsProfilePlaces
{
    private const string Users = "Users";

    private const string AppData = "AppData";

    private const string ProgramData = "ProgramData";

    private const string Product = "wsl-care";

    private const string Git = "git";

    /// <summary>The first segment of every catalogue folder spelt under the profile (<c>.claude</c>, <c>.codex</c>, <c>.gemini</c> …).</summary>
    private static IReadOnlyList<string> AgentFolderNames { get; } =
        [.. AgentCatalogue.Agents.SelectMany(a => a.Windows).Where(f => f.StartsWith(@"%USERPROFILE%\", StringComparison.Ordinal)).Select(f => f.Split('\\')[1]).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whose place <paramref name="windowsFolder"/> is, holds or lies inside; empty when none.</summary>
    /// <param name="profile">The Windows profile (<c>C:\Users\me</c>); empty when unknown.</param>
    /// <param name="windowsAgentFolders">The data folders of the manual agents the Windows side walks (E9.S1 review round M3).</param>
    public static string Clash(string windowsFolder, string profile, IReadOnlyList<string> windowsAgentFolders) =>
        Places(profile).Concat(windowsAgentFolders.Select(f => new ForbiddenFolder(f, $"the Windows manual agent folder {f}")))
            .FirstOrDefault(p => p.Path.Length > 0 && Overlaps(windowsFolder, p.Path)) is { } place
            ? place.Whose
            : AnyProfile(windowsFolder);

    /// <summary>The profile's places, as the Windows binary's own rules name them; none when the profile is unknown.</summary>
    public static IReadOnlyList<ForbiddenFolder> Places(string profile)
    {
        if (profile.Length < 3)
        {
            return [];
        }

        var windows = new WindowsHostPaths(EnvironmentOf(profile));
        return
        [
            .. ExtraAgentRules.ProtectedPlaces(windows, windows.AgentRoots.Select(r => new ForbiddenFolder(r, $"the Windows AI agent folder {r}")), []),
            new ForbiddenFolder(windows.TempDirectory, $"the Windows temporary folder {windows.TempDirectory}"),
        ];
    }

    /// <summary>A profile's folders as Windows lays them out by default (a redirected AppData is a residual: the full run reports
    /// the profile only).</summary>
    private static WindowsEnvironment EnvironmentOf(string profile)
    {
        var rules = PathRules.Windows;
        var local = rules.Join(profile, AppData, "Local");
        return new WindowsEnvironment(profile, rules.Join(profile, AppData, "Roaming"), local, rules.Join(profile[..3], "ProgramData"), rules.Join(local, "Temp"), profile[..3]);
    }

    private static bool Overlaps(string a, string b) => PathRules.Windows.IsSameOrUnder(a, b) || PathRules.Windows.IsStrictlyUnder(b, a);

    /// <summary>Any profile's AppData or agent folder, a profile, or the folder of all profiles — on any drive or share.</summary>
    private static string AnyProfile(string windowsFolder) =>
        UnderTheRoot(windowsFolder) is var segments && ProductPlace(segments) is { Length: > 0 } product ? product : ProfilesPlace(segments);

    /// <summary>E9.S1 review round m1: wsl-care's own Windows folder (<c>ProgramData\wsl-care</c>) on any drive, profile or not.</summary>
    private static string ProductPlace(string[] segments) => segments switch
    {
        [var data] when Is(data, ProgramData) => $"the folder {data}, which holds wsl-care's own Windows folder",
        [var data, var product, ..] when Is(data, ProgramData) && Is(product, Product) => $"wsl-care's own Windows folder {data}\\{product}",
        _ => string.Empty,
    };

    private static string ProfilesPlace(string[] segments) => segments switch
    {
        [var users] when IsUsers(users) => "the folder of every Windows profile, which holds their AI agents' folders",
        [var users, var who] when IsUsers(users) => $"the Windows profile {who}, which holds its AI agents' folders",
        [var users, var who, var place, ..] when IsUsers(users) => ProfilePlace(who, place),
        _ => string.Empty,
    };

    private static string ProfilePlace(string who, string place) =>
        string.Equals(place, AppData, StringComparison.OrdinalIgnoreCase) ? $"the AppData of the Windows profile {who}, where AI agents and the temporary folder live"
        : AgentFolderNames.Contains(place, StringComparer.OrdinalIgnoreCase) ? $"the AI agent folder {place} of the Windows profile {who}"
        : Is(place, Git) ? $"the repositories folder {place} of the Windows profile {who}, under which nothing is ever touched"
        : string.Empty;

    private static bool Is(string segment, string name) => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsUsers(string segment) => string.Equals(segment, Users, StringComparison.OrdinalIgnoreCase);

    /// <summary>The segments after the drive (<c>C:\</c>) or the share (<c>\\server\share</c>).</summary>
    private static string[] UnderTheRoot(string windowsFolder)
    {
        var segments = windowsFolder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return windowsFolder.StartsWith(@"\\", StringComparison.Ordinal) ? [.. segments.Skip(2)] : [.. segments.Skip(1)];
    }
}
