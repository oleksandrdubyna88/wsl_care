namespace WslCare.TestSupport;

/// <summary>
/// How <see cref="ChildProcess"/> waits for a child that can SHOW it is working: <paramref name="Mark"/> answers a number
/// that changes whenever the child does something (the scenario harness: the length of its fakes' call log). The child is
/// killed with its tree after <paramref name="Silence"/> without a change, or at <paramref name="Cap"/> whatever it does.
/// </summary>
/// <remarks>
/// <para>Why a heartbeat and not a total: a wall-clock total over a child that starts dozens of processes in a row is a guess
/// about how loaded the machine is. Measured 2026-10-05 in WSL (Release, the timer's full run of <c>LogsFlows</c>, 45 fake
/// calls in a row): the pass took 4 s alone, 12–16 s under 24 CPU burners and 19–29 s under 48 — and failed a 30 s total in
/// 4 of 10 runs there while it was calling a fake at least every 1.3 s. A silent child is still killed in the
/// <see cref="ChildProcess.DefaultCeiling"/> a total gave it.</para>
/// <para><see cref="DefaultCap"/> is the bound on a child that keeps making progress for ever — a loop, not a slow pass.</para>
/// </remarks>
public sealed record ProgressWait(Func<long> Mark, TimeSpan Silence, TimeSpan Cap)
{
    /// <summary>Ten times the slowest progressing pass measured (about 30 s under 48 burners), and well inside the CI job's
    /// own 30 minutes, so a looping child fails as a test rather than as a cancelled job.</summary>
    public static readonly TimeSpan DefaultCap = TimeSpan.FromMinutes(5);
}
