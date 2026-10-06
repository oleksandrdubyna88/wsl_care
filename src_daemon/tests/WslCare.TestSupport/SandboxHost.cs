using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.TestSupport;

/// <summary>
/// A whole host laid out under a temporary root — the <see cref="IHostPaths"/> of THIS operating
/// system sandboxed there, and the real <see cref="PhysicalFileSystem"/> over it — so a test
/// exercises the product's own file system and policy without a real folder in reach.
/// </summary>
public sealed class SandboxHost : IDisposable
{
    public SandboxHost(string purpose)
    {
        Root = new TempRoot(purpose);
        Paths = HostPaths.ForThisMachine(Root.Path);
        Files = new PhysicalFileSystem(Paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true };
    }

    public TempRoot Root { get; }

    public IHostPaths Paths { get; }

    public PhysicalFileSystem Files { get; }

    /// <summary>Writes the user configuration layer as given, creating its directory.</summary>
    public string WriteUserConfig(string json) => Write(Paths.UserConfigFile, json);

    /// <summary>Writes the machine configuration layer as given, creating its directory.</summary>
    public string WriteMachineConfig(string json) => Write(Paths.MachineConfigFile, json);

    public string ReadUserConfig() => File.ReadAllText(Paths.UserConfigFile);

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Root.Dispose();
}
