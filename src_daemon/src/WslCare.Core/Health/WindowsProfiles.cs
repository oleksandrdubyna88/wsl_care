using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Health;

/// <summary>
/// Where the Windows user's files are seen from this process — the one place a Windows profile path becomes a path
/// here: itself on the Windows binary; inside the distro, the profile a full run's clock probe printed, under the
/// automount root of <c>/etc/wsl.conf</c> (<c>/mnt/c/Users/…</c>), and under the sandbox root when sandboxed.
/// </summary>
public static class WindowsProfiles
{
    /// <summary>Docker Desktop's <c>daemon.json</c> as this process sees it; empty when the profile is unknown.</summary>
    public static string DockerDesktopConfig(IHostPaths paths, IFileSystem files, string windowsProfile)
    {
        if (paths is not LinuxHostPaths linux)
        {
            return paths.DockerDesktopConfigFile;
        }

        return InDistro(linux, files, windowsProfile).Map(p => linux.Rules.Join(p, ".docker", "daemon.json")).ValueOr(string.Empty);
    }

    /// <summary>The profile seen from inside the distro, or why not.</summary>
    public static Reading<string> InDistro(LinuxHostPaths paths, IFileSystem files, string windowsProfile)
    {
        if (windowsProfile.Length == 0)
        {
            return Reading.Missing<string>("the Windows profile is unknown: no full run has found it yet");
        }

        var automount = ProcText.Read(files, paths.WslConfFile).Map(HealthParsers.AutomountRoot).ValueOr("/mnt/");
        return HealthCollector.InDistro(windowsProfile, automount).Map(paths.DistroPath);
    }
}
