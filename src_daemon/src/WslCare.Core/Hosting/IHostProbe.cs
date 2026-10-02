namespace WslCare.Core.Hosting;

/// <summary>
/// The platform split of plan §8: what one side of the machine can observe. <c>LinuxProbe</c> reads
/// <c>/proc</c>, systemd and Docker; <c>WindowsProbe</c> reads <c>vmmemWSL</c>, host RAM and the
/// Windows AI-agent folders.
/// </summary>
/// <remarks>
/// E1.S2 fixes the seam and nothing else: the collectors of E2 add their readings here as typed
/// records, one method per collector, so that the status snapshot is assembled from whichever
/// probe the binary was built for and a test can hand it a recorded one. Until then the interface
/// says only which side it is.
/// </remarks>
public interface IHostProbe
{
    HostSide Side { get; }
}
