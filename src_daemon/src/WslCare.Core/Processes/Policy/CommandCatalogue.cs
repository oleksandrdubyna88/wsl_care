namespace WslCare.Core.Processes.Policy;

/// <summary>
/// Every argv template the product declares — the allowlist the <see cref="CommandPolicy"/> matches against. The
/// product's catalogue is the read commands of the collectors (<see cref="ReadCommandTemplates"/>) and the templates
/// every registered action declares (<see cref="Actions.ActionRegistry"/>); nothing else.
/// </summary>
public sealed class CommandCatalogue
{
    public CommandCatalogue(IReadOnlyList<CommandTemplate> templates)
    {
        if (templates.FirstOrDefault(t => t.Parts.SkipLast(1).Any(p => p is ArgPart.Repeat)) is { } misplaced)
        {
            throw new ArgumentException($"template {misplaced.Name}: a repeated slot may only be the last part", nameof(templates));
        }

        if (templates.FirstOrDefault(t => t.Executable.Length == 0 || t.Executable.IndexOfAny(['/', '\\']) >= 0) is { } pathed)
        {
            throw new ArgumentException($"template {pathed.Name}: the executable must be a bare name, never a path", nameof(templates));
        }

        Templates = templates;
    }

    /// <summary>No template at all: a policy over it refuses everything (deny by default).</summary>
    public static CommandCatalogue Empty { get; } = new([]);

    /// <summary>What the product may run.</summary>
    public static CommandCatalogue Product => ProductCatalogue.Value;

    public IReadOnlyList<CommandTemplate> Templates { get; }

    /// <summary>The machine-scoped template <paramref name="argv"/> (executable first) is an instance of; <c>null</c> for none.</summary>
    public CommandTemplate? MatchMachine(IReadOnlyList<string> argv) =>
        Templates.FirstOrDefault(t => t.Scope == CommandScope.Machine && string.Equals(t.Executable, argv[0], StringComparison.Ordinal) && t.Matches([.. argv.Skip(1)]));

    /// <summary>The user-scoped template <paramref name="wrapped"/> is an instance of; <c>null</c> for none.</summary>
    public CommandTemplate? MatchUser(WrappedCommand wrapped) =>
        Templates.FirstOrDefault(t => t.Scope == CommandScope.User && string.Equals(t.Executable, wrapped.ExecutableName, StringComparison.Ordinal) && t.Matches(wrapped.Arguments));

    private static readonly Lazy<CommandCatalogue> ProductCatalogue = new(() =>
        new CommandCatalogue([.. ReadCommandTemplates.All, .. Actions.ActionRegistry.Product.Actions.SelectMany(a => a.Commands)]));
}
