using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>A23, the Windows <c>%TEMP%</c> cleanup (PLAN_windows_runner.md §3.12) — STUB.</summary>
public sealed class WindowsTempAction : ICleanupAction
{
    public ActionId Id { get; } = ActionId.Find("A23")!;

    public string Summary => "Windows TEMP cleanup";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Windows];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken) =>
        Task.FromResult(ActionPreview.Of(Summary, 0, 0, "stub", new Dictionary<string, long>(), string.Empty, []));

    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => new(false, "stub");

    public Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken) =>
        Task.FromResult(ActionRun.Nothing([]));
}
