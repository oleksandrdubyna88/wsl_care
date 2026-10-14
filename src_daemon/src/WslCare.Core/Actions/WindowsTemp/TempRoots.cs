using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>What resolving the cleanup's root produced — a closed set.</summary>
public abstract record TempRootResult
{
    private TempRootResult()
    {
    }

    public sealed record Resolved(string Root) : TempRootResult;

    public sealed record Refused(string Reason) : TempRootResult;
}

/// <summary>The root of the cleanup — STUB.</summary>
public static class TempRoots
{
    public static TempRootResult Resolve(IHostPaths paths) => new TempRootResult.Resolved(paths.TempDirectory);
}
