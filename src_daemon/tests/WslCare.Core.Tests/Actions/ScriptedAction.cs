using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Tests.Actions;

/// <summary>A test action: its gates and its behaviour are the test's; every call is logged into a shared journal so a test
/// can assert the ORDER of what the engine did.</summary>
internal sealed class ScriptedAction(string id, List<string> journal) : ICleanupAction
{
    public ActionId Id { get; } = ActionId.Find(id) ?? throw new ArgumentException($"no action id {id}");

    public string Summary => $"scripted {id}";

    public CommandScope Scope { get; init; } = CommandScope.Machine;

    public IdleRule Idle { get; init; } = IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; init; } = [HostSide.Wsl, HostSide.Windows];

    public IReadOnlyList<CommandTemplate> Commands { get; init; } = [];

    public bool Fires { get; init; } = true;

    public long PreviewBytes { get; init; } = 100;

    public string Refusal { get; init; } = string.Empty;

    /// <summary>What the preview says to skip on (a tool not installed); empty by default.</summary>
    public string Skip { get; init; } = string.Empty;

    /// <summary>An event that does not wait for idle (E3.S3); empty by default.</summary>
    public string Urgent { get; init; } = string.Empty;

    /// <summary>What the preview also does with its context (a test reads <see cref="ActionContext.RanEarlier"/>).</summary>
    public Action<ActionContext> OnPreview { get; init; } = static _ => { };

    /// <summary>What the run does; by default it succeeds having removed one object of <see cref="PreviewBytes"/>.</summary>
    public Func<ActionContext, Task<ActionRun>> OnRun { get; init; } = _ => Task.FromResult(new ActionRun(1, 100, "scripted", 200, 100, [], [], string.Empty));

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        journal.Add($"preview {id}");
        OnPreview(context);
        return Task.FromResult(new ActionPreview($"scripted {id}", true, null, 1, PreviewBytes, "scripted", new Dictionary<string, long>(), Refusal, []) { Skip = Skip, Urgent = Urgent });
    }

    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => new(Fires, Fires ? "scripted: fired" : "scripted: below its trigger");

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        journal.Add($"run {id}");
        return await OnRun(context);
    }
}
