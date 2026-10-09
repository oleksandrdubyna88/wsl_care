namespace WslCare.Core.Processes.Policy;

/// <summary>
/// The ONE filter every argv passes before a process starts (plan §5 <i>Never</i>, §15 #1, §15c #3) — the only policy
/// <see cref="ProcessCommandRunner"/> accepts. DENY BY DEFAULT: an argv starts only when it breaks no rule of the
/// <see cref="NeverList"/> AND is an instance of a template the <see cref="CommandCatalogue"/> declares.
/// </summary>
/// <remarks>
/// <para>The order is fixed and the never-list comes first, so a template that happens to describe a never-command —
/// a planted one, a typo, a slot too wide — still cannot start it; the property test (<c>CommandPolicyPropertyTests</c>)
/// holds both halves over thousands of generated inputs.</para>
/// <list type="number">
/// <item>The never-list over the argv as given.</item>
/// <item><c>runuser</c>: only in the one shape of <see cref="TargetUserArgv"/>, with a CLEAN environment; the wrapped
/// command is judged by the never-list again, its file must sit in a permitted bin folder, and it must be an instance of
/// a USER-scoped template.</item>
/// <item>Anything else must be an instance of a MACHINE-scoped template.</item>
/// </list>
/// <para>Sealed, with no way to remove a rule: a caller can choose which templates a policy declares (the tests declare
/// their own), never which rules it skips.</para>
/// </remarks>
public sealed class CommandPolicy
{
    private readonly Func<SelfBinaryResult> _self;

    private CommandPolicy(CommandCatalogue catalogue, Func<SelfBinaryResult> self)
    {
        Catalogue = catalogue;
        _self = self;
    }

    /// <summary>The product's policy: the never-list over <see cref="CommandCatalogue.Product"/>.</summary>
    public static CommandPolicy Product => ProductPolicy.Value;

    /// <summary>The never-list over a catalogue of the caller's — what a test declares for the tool it starts.</summary>
    public static CommandPolicy Over(CommandCatalogue catalogue) => Over(catalogue, SelfBinary.Product);

    /// <summary>As above, with where the product's own binary is (<see cref="SelfBinary"/>) — a test's own.</summary>
    public static CommandPolicy Over(CommandCatalogue catalogue, Func<SelfBinaryResult> self)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(self);
        return new CommandPolicy(catalogue, self);
    }

    public CommandCatalogue Catalogue { get; }

    public CommandVerdict Review(CommandRequest request)
    {
        var argv = request.Argv;
        if (NeverList.FirstBroken(argv) is { } broken)
        {
            return Never(broken, argv);
        }

        return string.Equals(argv[0], TargetUserArgv.Runuser, StringComparison.Ordinal)
            ? ReviewWrapped(request)
            : Catalogue.MatchMachine(argv) is not null ? CommandVerdict.Allowed : NoTemplate(argv);
    }

    private CommandVerdict ReviewWrapped(CommandRequest request)
    {
        var wrapped = TargetUserArgv.Parse(request.Argv)!;
        var self = IsSelf(wrapped.ExecutablePath);
        return WrappedRefusal(request, wrapped, self) ?? Matched(request.Argv, wrapped, self);
    }

    /// <summary>Whether <paramref name="path"/> is the product's own binary, checked now (<see cref="SelfBinary"/>).</summary>
    private bool IsSelf(string path) => _self() is SelfBinaryResult.Found found && string.Equals(found.Path, path, StringComparison.Ordinal);

    /// <summary>Why a <c>runuser</c> command is refused before any template is asked — an inherited environment, a never-rule
    /// broken by the WRAPPED command, a file that is neither in the user's bin folders nor the product's own binary — or
    /// <c>null</c> when none of these holds.</summary>
    private static CommandVerdict? WrappedRefusal(CommandRequest request, WrappedCommand wrapped, bool self) => request switch
    {
        { Environment: not CommandEnvironment.Clean } =>
            CommandVerdict.Refuse($"refused: a command run as {wrapped.User} must start with a clean environment, never this process's: {Shown(request.Argv)}"),
        _ when NeverList.FirstBroken(wrapped.Argv) is { } broken => Never(broken, request.Argv),
        _ when !self && !TargetUserArgv.IsInABinFolder(wrapped.ExecutablePath) => NotInABinFolder(wrapped),
        _ => null,
    };

    /// <summary>The user template it is an instance of, and its file the right one: a SELF-INVOCATION only with the product's own
    /// checked binary — never a <c>wsl-care</c> of the user's bin folders — and every other template only with a file of those
    /// folders (plan §15r D1, E9.S4).</summary>
    private CommandVerdict Matched(IReadOnlyList<string> argv, WrappedCommand wrapped, bool self) => Catalogue.MatchUser(wrapped) switch
    {
        null => NoTemplate(argv),
        { SelfInvocation: true } template when !self =>
            CommandVerdict.Refuse($"refused: {template.Name} starts only the product's own root-owned binary, never {Shown([wrapped.ExecutablePath])}"),
        { SelfInvocation: false } when !TargetUserArgv.IsInABinFolder(wrapped.ExecutablePath) => NotInABinFolder(wrapped),
        _ => CommandVerdict.Allowed,
    };

    private static CommandVerdict NotInABinFolder(WrappedCommand wrapped) =>
        CommandVerdict.Refuse($"refused: {Shown([wrapped.ExecutablePath])} is not a file in one of the target user's bin folders ({string.Join(", ", TargetUserArgv.BinFolderSuffixes)}, nvm's node bin)");

    private static CommandVerdict Never(NeverRule rule, IReadOnlyList<string> argv) =>
        CommandVerdict.Refuse($"refused by the never-list ({rule.Id}: {rule.Description}): {Shown(argv)}");

    private static CommandVerdict NoTemplate(IReadOnlyList<string> argv) =>
        CommandVerdict.Refuse($"refused: no declared command template matches {Shown(argv)} (deny by default)");

    /// <summary>The argv for a refusal line: control characters replaced, long arguments cut — a refusal is ONE line.</summary>
    private static string Shown(IReadOnlyList<string> argv) =>
        string.Join(' ', argv.Select(a => new string([.. (a.Length > 120 ? a[..120] + "..." : a).Select(c => char.IsControl(c) ? '?' : c)])));

    private static readonly Lazy<CommandPolicy> ProductPolicy = new(() => new CommandPolicy(CommandCatalogue.Product, SelfBinary.Product));
}
