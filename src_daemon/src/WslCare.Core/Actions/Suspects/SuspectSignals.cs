using System.Globalization;

using WslCare.Core.Hosting;
using WslCare.Core.Processes;

namespace WslCare.Core.Actions.Suspects;

/// <summary>
/// The ONE way A11 and A18 end processes (extracted from A11 for E7.S2b, plan §15q): every target read again just before its
/// signal — the same process (its start), no CPU used since the preview, still no terminal, not root's — and the ones that pass
/// signalled TOGETHER by identity through <see cref="IProcessSignals"/>: one <c>SIGTERM</c> each, one shared grace, then
/// <c>SIGKILL</c> to the survivors; each outcome a verdict for the record.
/// </summary>
public static class SuspectSignals
{
    /// <summary>The key a preview item carries: <c>pid:start:cpu:uid</c> — who it is, the CPU ticks the preview saw and the account
    /// it ran as (E7.S2b review A-L2: the kill-time re-check compares the account, not only "not root").</summary>
    public static string Key(SuspectSample sample) =>
        string.Create(CultureInfo.InvariantCulture, $"{sample.Pid}:{sample.StartTicks}:{sample.CpuTicks}:{sample.Uid}");

    /// <summary>The <c>pid:start</c> a key names — what a modal shows and a button run passes back (review A-H1).</summary>
    public static string Shown(string key)
    {
        var parts = key.Split(':');
        return parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : key;
    }

    /// <summary>Whether <paramref name="text"/> is a shown process key: <c>&lt;pid&gt;:&lt;start ticks&gt;</c>, both whole numbers.</summary>
    public static bool IsShownKey(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 1
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Every target read again — the same process, still no CPU, still no terminal — and the ones that pass signalled
    /// TOGETHER by identity: one <c>SIGTERM</c> each, ONE shared grace, then <c>SIGKILL</c> to the survivors (gate finding #9:
    /// three that ignore <c>SIGTERM</c> take one grace, not three).</summary>
    public static async Task<IReadOnlyList<(ActionItem Item, SignalOutcome Outcome)>> EndAllAsync(ActionContext context, LinuxHostPaths linux, IReadOnlyList<ActionItem> targets, TimeSpan grace, CancellationToken cancellationToken)
    {
        var checkedTargets = targets.Select(t => (Item: t, Identity: Identity(t), Refusal: Recheck(SuspectTermination.Sample(context, linux, Identity(t).Pid), t))).ToList();
        var passing = checkedTargets.Where(c => c.Refusal is null).ToList();
        var outcomes = passing.Count == 0 ? [] : await context.Signals.TerminateAllAsync([.. passing.Select(c => c.Identity)], grace, cancellationToken).ConfigureAwait(false);
        return [.. checkedTargets.Where(c => c.Refusal is not null).Select(c => (c.Item, c.Refusal!)), .. passing.Zip(outcomes, (c, o) => (c.Item, o))];
    }

    /// <summary>The pid and start a preview item's key names.</summary>
    private static ProcessIdentity Identity(ActionItem target)
    {
        var parts = target.Key.Split(':');
        return new ProcessIdentity(int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Why a target is not signalled after all — gone, another process now, or it used CPU / gained a terminal /
    /// became root's since the preview; <c>null</c> when it is still the idle suspect the preview saw.</summary>
    private static SignalOutcome? Recheck(SuspectSample? now, ActionItem target)
    {
        var parts = target.Key.Split(':');
        var (identity, cpu, uid) = (Identity(target), long.Parse(parts[2], CultureInfo.InvariantCulture), parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : -1);
        return now switch
        {
            null => new SignalOutcome.AlreadyGone(),
            { StartTicks: var start } when start != identity.StartTicks => new SignalOutcome.NotTheSame($"pid {identity.Pid} started at tick {start}, not {identity.StartTicks}: another process now"),
            { } changed when Changed(changed, cpu, uid) => new SignalOutcome.NotTheSame($"pid {identity.Pid} used CPU, gained a terminal or changed owner since the preview: kept"),
            _ => null,
        };
    }

    private static bool Changed(SuspectSample now, long cpu, int uid) => now.CpuTicks != cpu || now.Tty != 0 || now.Uid == 0 || now.Uid != uid;

    /// <summary>What one outcome means for the record: ended (and how), kept (and why), and the failure it counts as, if any.</summary>
    public static (bool Ended, ActionItem Item, string Failure) Verdict(ActionItem item, SignalOutcome outcome) => outcome switch
    {
        SignalOutcome.Ended e => (true, item with { Note = (e.NeededKill ? "ended on SIGKILL: " : "ended on SIGTERM: ") + item.Note }, string.Empty),
        SignalOutcome.AlreadyGone => (false, item with { Note = "already gone" }, string.Empty),
        SignalOutcome.NotTheSame n => (false, item with { Note = n.Reason }, string.Empty),
        SignalOutcome.StillRunning s => (false, item with { Note = s.Reason }, s.Reason),
        SignalOutcome.Refused r => (false, item with { Note = "not signalled: " + r.Reason }, "not signalled: " + r.Reason),
        SignalOutcome.Failed f => (false, item with { Note = f.Reason }, f.Reason),
        _ => throw new System.Diagnostics.UnreachableException("SignalOutcome is a closed set"),
    };
}
