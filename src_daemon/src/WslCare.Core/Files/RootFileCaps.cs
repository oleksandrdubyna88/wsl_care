using WslCare.Core.Config;

namespace WslCare.Core.Files;

/// <summary>
/// E7.S2c, review N-5: how much of a file ROOT reads back from its own state directory — never the whole of whatever is there.
/// A state file (one small JSON document: the running marker, the dry-run window, the clock state, the volumes seen, the events
/// summary) is capped by <c>records.maxStateFileBytes</c>; a file that grows by a line per run or per event (the history, a run's
/// detail, the event lines) by <c>records.maxHistoryBytes</c> — which is also the bound on every other uncapped read
/// (the one-argument read of <see cref="PhysicalFileSystem"/>). A file past its cap is <see cref="FileReadResult.Unreadable"/>, read as
/// far as the cap and no further.
/// </summary>
public static class RootFileCaps
{
    public static int State => Tuning.Current.Int(ConfigKeys.Records.MaxStateFileBytes);

    public static int History => Tuning.Current.Int(ConfigKeys.Records.MaxHistoryBytes);
}
