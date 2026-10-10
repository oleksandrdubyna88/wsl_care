using System.Globalization;

using WslCare.Core.Records;

namespace WslCare.Core.Actions.Suspects;

/// <summary>
/// A button run of a process-ending action bound to what its modal showed (<c>--process &lt;pid:start&gt;</c>; E7.S2b review A-H1,
/// plan E14 S2a and S7b.2): narrowed to the shown processes that are still eligible — or refused when a button run showed none.
/// Shared by A19 and A21; extracted from A19 so the two cannot drift apart.
/// </summary>
public static class ShownProcessBinding
{
    public static ActionPreview Bound(ActionPreview preview, ActionContext context, string buttonNeedsShownProcesses)
    {
        if (context.ShownProcesses.Given)
        {
            var kept = preview.Targets.Where(t => context.ShownProcesses.Names.Contains(SuspectSignals.Shown(t.Key))).ToList();
            var what = string.Create(CultureInfo.InvariantCulture, $"{preview.What}; of the {context.ShownProcesses.Names.Count} process(es) the panel showed, the {kept.Count} still eligible");
            // coai code round 2026-10-08, finding 13: the held memory of what is KEPT, not of the whole preview.
            var facts = new Dictionary<string, long>(preview.Facts, StringComparer.Ordinal) { [SuspectTermination.HeldMemoryFact] = kept.Sum(i => i.Bytes ?? 0) };
            return RowPreviews.Narrowed(preview, kept, what, facts);
        }

        return context.Trigger == RunTrigger.Manual && preview.Available
            ? preview with { Refusal = preview.Refusal.Length > 0 ? preview.Refusal : buttonNeedsShownProcesses }
            : preview;
    }
}
