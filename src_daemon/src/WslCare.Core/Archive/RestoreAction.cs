using System.Globalization;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Archive;

/// <summary>
/// A20 (plan §15r D6, E9.S4 — the restore button; A19 is the idle MCP servers' stop since E14 S2a): put archived AI-agent sessions
/// back into their agents' folders, as the TARGET USER through the product's own binary (<c>archive restore --entry …</c>) — a
/// button only, bound to the entry ids its modal showed and judged again against a fresh list.
/// </summary>
/// <remarks>
/// <para><b>Preview</b> = the child's <c>archive list --json</c>: the VERIFIED entries removed at their source (<c>sourceRemoved</c>,
/// <c>split</c>) — by entry id, agent and month; an unverified entry is never offered (restoring one is a person's decision, in a
/// terminal, with <c>--accept-unverified</c>). <b>Run</b>: the shown ids still restorable, at most <c>archive.maxRestoreEntries</c>
/// (they reach the child as one argument: the S4 plan round's finding 0), STREAMED under <c>archive.restoreLimitMinutes</c>; a
/// refused session fails the run, its why in the user's own answer.</para>
/// </remarks>
public sealed class RestoreAction : ICleanupAction, IBoundToShownList
{
    public const string ButtonNeedsShownEntries =
        "a run of A20 must pass the entries its preview SHOWED (--entry <id>): A20 restores only those, judged again (plan §15r E9.S4)";

    private static readonly IReadOnlySet<string> Restorable = new HashSet<string>(StringComparer.Ordinal) { ArchiveIndex.Events.SourceRemoved, ArchiveIndex.Events.Split };

    public ActionId Id { get; } = ActionId.Find("A20")!;

    public string Summary => "restore archived AI-agent sessions into their agents' folders as the target user — the entries the modal showed, a button only";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [ArchiveChildren.List, ArchiveChildren.Restore];

    /// <summary>Every entry id the preview offers — what the button passes back with <c>--entry</c>.</summary>
    public IReadOnlyList<string> Shown(ActionPreview preview) => [.. preview.Targets.Select(t => t.Key).Take(ShownList.MaxNames)];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (ArchiveGates.Before(context, commands, ArchiveChildren.List, cancellationToken) is { } gated)
        {
            return gated;
        }

        var list = await ArchiveGates.ChildTextAsync(commands, ArchiveChildren.List, [0], cancellationToken).ConfigureAwait(false);
        var preview = list.Failure.Length > 0 ? ActionPreview.Unavailable("restore", list.Failure) : Listed(context, ArchiveChildAnswers.List(list.Text, context.Config));
        return Bound(preview, context);
    }

    /// <summary>A button only: the timer never gets here (its auto gate refuses a button-only id first).</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => new(false, "A20 is a button only");

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var ids = preview.Targets.Select(t => t.Key).ToList();
        if (RunRefusal(context, ids.Count) is { Length: > 0 } refusal)
        {
            return ArchiveGates.Failed(commands, refusal);
        }

        if (ids.Count == 0)
        {
            return ActionRun.Nothing(commands.Ran, "none of the entries the modal showed is still restorable");
        }

        var answer = await ArchiveGates.StreamedAsync(context, commands, ArchiveChildren.Restore, [string.Join(',', ids)], ArchiveChildren.Restore.Ceiling, cancellationToken).ConfigureAwait(false);
        return answer.Failure.Length > 0 ? ArchiveGates.Failed(commands, answer.Failure) : Restored(commands, ArchiveChildAnswers.Run(answer.Text, context.Config));
    }

    /// <summary>Why this run restores nothing at all: no shown list, or more entries than one restore takes (the S4 plan round's
    /// finding 0: they reach the child as ONE argument); empty otherwise.</summary>
    private static string RunRefusal(ActionContext context, int count)
    {
        var most = context.Config.Int(ConfigKeys.Archive.MaxRestoreEntries);
        return !context.ShownEntries.Given ? ButtonNeedsShownEntries
            : count > most ? string.Create(CultureInfo.InvariantCulture, $"{count} entries are more than one restore takes ({ConfigKeys.Archive.MaxRestoreEntries.Name}: {most}); show fewer")
            : string.Empty;
    }

    private static ActionPreview Listed(ActionContext context, ChildAnswer<ArchiveListReport> answer)
    {
        if (answer is not ChildAnswer<ArchiveListReport>.Valid { Value: var list })
        {
            return ActionPreview.Unavailable("restore", $"the archive child's list could not be believed: {((ChildAnswer<ArchiveListReport>.Invalid)answer).Why}");
        }

        var offered = list.Entries.Where(e => e.Verified && Restorable.Contains(e.Status)).ToList();
        var unverified = list.Entries.Count(e => !e.Verified && Restorable.Contains(e.Status));
        IReadOnlyList<ActionItem> targets = [.. offered.Select(e => new ActionItem("archived session", e.EntryId, e.Bytes, $"{e.Agent} {e.Month}, {e.Files} file(s)") { Key = e.EntryId })];
        var what = string.Create(CultureInfo.InvariantCulture, $"restore archived AI-agent sessions as {ArchiveGates.UserOf(context)}: {offered.Count} removed at their source and verified{(unverified > 0 ? $"; {unverified} unverified, not offered (restore one in a terminal with --accept-unverified)" : string.Empty)}");
        return ActionPreview.Of(what, offered.Count, offered.Sum(e => e.Bytes), "the archive child's own list (archive list --json), run as the target user; entry ids only", new Dictionary<string, long>(StringComparer.Ordinal) { ["unverified"] = unverified }, string.Empty, targets);
    }

    /// <summary>A run narrowed to what its modal showed — or refused when it showed nothing.</summary>
    private static ActionPreview Bound(ActionPreview preview, ActionContext context)
    {
        if (Settled(preview))
        {
            return preview;
        }

        if (!context.ShownEntries.Given)
        {
            return preview with { Refusal = ButtonNeedsShownEntries };
        }

        var kept = preview.Targets.Where(t => context.ShownEntries.Names.Contains(t.Key)).ToList();
        return RowPreviews.Narrowed(preview, kept, string.Create(CultureInfo.InvariantCulture, $"{preview.What}; of the {context.ShownEntries.Names.Count} entries the panel showed, the {kept.Count} still restorable"), preview.Facts);
    }

    /// <summary>A preview that already says no — unread, a skip, a refusal — is not narrowed.</summary>
    private static bool Settled(ActionPreview preview) => !preview.Available || preview.Skip.Length > 0 || preview.Refusal.Length > 0;

    /// <summary>The restore's judged answer: each session by entry id and agent — never its key — and a refusal fails the run.</summary>
    private static ActionRun Restored(ActionCommands commands, ChildAnswer<ArchiveRunReport> answer)
    {
        if (answer is not ChildAnswer<ArchiveRunReport>.Valid { Value.Restore: var restore })
        {
            return ArchiveGates.Failed(commands, $"the archive child's answer could not be believed: {((ChildAnswer<ArchiveRunReport>.Invalid)answer).Why}");
        }

        IReadOnlyList<ActionItem> sessions = [.. restore.Sessions.Where(s => s.Outcome is RestoreOutcomes.Restored or RestoreOutcomes.Partial).Select(s => new ActionItem("archived session", s.EntryId, s.Bytes, $"{s.Agent} {s.Month}: {s.Outcome}, {s.Files} file(s)"))];
        var refused = restore.Refused > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{restore.Refused} session(s) were refused ({string.Join(", ", restore.Sessions.Where(s => s.Outcome is not (RestoreOutcomes.Restored or RestoreOutcomes.Partial or RestoreOutcomes.AlreadyThere)).Select(s => $"{s.EntryId}: {s.Outcome}"))}); \"wsl-care archive restore --entry <id>\", run as the user, says why")
            : string.Empty;
        return new ActionRun(restore.Restored, null, "a restore frees nothing: it puts archived sessions back", null, null, sessions, commands.Ran, refused)
        {
            Notes = [string.Create(CultureInfo.InvariantCulture, $"restored {restore.Restored}, already there {restore.AlreadyThere}, refused {restore.Refused}")],
        };
    }
}
