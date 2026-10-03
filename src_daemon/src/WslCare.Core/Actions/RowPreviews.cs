using WslCare.Core.Collectors;
using WslCare.Core.Preview;

namespace WslCare.Core.Actions;

/// <summary>Turning a cleanup row of <see cref="CleanupPreviews"/> into an action's preview — the SAME what, count, bytes,
/// basis and refusal, so <c>act &lt;A#&gt; --preview</c> equals that row of <c>preview --all</c> (E3.S2).</summary>
public static class RowPreviews
{
    /// <summary>The fact every Docker preview carries for its trigger: how many objects it selected.</summary>
    public const string CountFact = "count";

    /// <summary>The fact: the bytes of the objects it selected (those Docker sized).</summary>
    public const string BytesFact = "bytes";

    public static ActionPreview From(CleanupRow row, IReadOnlyList<ActionItem> targets, IReadOnlyDictionary<string, long> facts) => row.Figures switch
    {
        Reading<RowFigures>.Available { Value: var f } => ActionPreview.Of(row.What, f.Count, f.Bytes, row.Basis, new Dictionary<string, long>(WithCounts(facts, f.Count, f.Bytes), StringComparer.Ordinal) { [UnsizedFact] = f.Unsized }, row.Refusal, targets),
        Reading<RowFigures>.Unavailable u => ActionPreview.Unavailable(row.What, u.Reason) with { Refusal = row.Refusal },
        _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
    };

    /// <summary>A folder row (A8, A9) as a preview: the row's own figures, and the folder as its one target.</summary>
    public static ActionPreview FromFolder(CleanupRow row, string kind, string folder) =>
        From(row, row.Figures is Reading<RowFigures>.Available { Value: var f } ? [new ActionItem(kind, folder, f.Bytes) { Key = folder }] : [], new Dictionary<string, long>(StringComparer.Ordinal));

    /// <summary>One target as a preview item: kind, the name a person reads, its size, and its full id as the key the run
    /// acts on.</summary>
    public static ActionItem Item(string kind, CleanupTarget target, string note = "") =>
        new(kind, target.Name, target.Bytes is Reading<long>.Available { Value: var b } ? b : null, note) { Key = target.Id };

    /// <summary>A preview narrowed to <paramref name="kept"/> of its targets (A4's shown list): the count and the bytes are
    /// those of what is kept; the rest of the row stays.</summary>
    public static ActionPreview Narrowed(ActionPreview preview, IReadOnlyList<ActionItem> kept, string what, IReadOnlyDictionary<string, long> facts)
    {
        var bytes = kept.Sum(t => t.Bytes ?? 0);
        return ActionPreview.Of(what, kept.Count, bytes, preview.Basis, WithCounts(facts, kept.Count, bytes), preview.Refusal, kept);
    }

    /// <summary>The fact: how many of the selected objects Docker gave no size for (the bytes are then a lower bound).</summary>
    public const string UnsizedFact = "unsized";

    private static Dictionary<string, long> WithCounts(IReadOnlyDictionary<string, long> facts, int count, long bytes) =>
        new(facts, StringComparer.Ordinal) { [CountFact] = count, [BytesFact] = bytes };
}
