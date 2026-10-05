using WslCare.Core.Actions;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;

namespace WslCare.Core.Config;

/// <summary>
/// How far this process trusts the user configuration layer it reads (plan §15q R1.1, R1.2) — decided once, where the host is
/// built, from whose home the per-user paths follow.
/// </summary>
/// <remarks>The premise of trusting another account's layer at all is WSL interop: with it, any process of that user can
/// already run <c>wsl.exe -u root</c>, so the layer is the user's intent and the boundary is a confused-deputy one (plan §15,
/// §15f #2). Without interop the premise fails and user→root is a real boundary: the layer may then only TIGHTEN what root does
/// (review M3), and the target user keeps every user-scoped action — <see cref="HomeOwner.Unknown"/> is not reused.</remarks>
public static class UserLayerTrusts
{
    /// <summary>The trust for a process whose per-user paths follow <paramref name="owner"/>; <paramref name="interopRefusal"/>
    /// answers why WSL interop is unavailable (empty when it is available) and is asked only for a root run.</summary>
    public static UserLayerTrust For(HomeOwner owner, Func<string> interopRefusal) => owner switch
    {
        HomeOwner.Target target => new((uint)target.User.Uid, true, LoosenRefused(interopRefusal()), string.Empty),
        HomeOwner.Unknown => new(RegularFiles.EffectiveUid(), true, string.Empty, owner.UserLayerSkipped),
        _ => UserLayerTrust.OwnLayer(),
    };

    /// <summary>The interop check over the distro's binfmt_misc entries as this layout sees them (a sandbox's fixture tree, or
    /// <c>/proc</c> on the machine).</summary>
    public static string InteropRefusal(LinuxHostPaths paths, IFileSystem files) =>
        WindowsSystemDrive.InteropRefusal(entry => ProcText.Read(files, paths.DistroPath(entry)));

    private static string LoosenRefused(string interopRefusal) =>
        interopRefusal.Length == 0
            ? string.Empty
            : $"{interopRefusal} — so the user layer cannot loosen what root does (it may only tighten it); set machine-wide values in /etc/wsl-care/config.json";
}
