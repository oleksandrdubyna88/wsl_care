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
    private CommandPolicy(CommandCatalogue catalogue)
    {
        Catalogue = catalogue;
    }

    /// <summary>The product's policy: the never-list over <see cref="CommandCatalogue.Product"/>.</summary>
    public static CommandPolicy Product => ProductPolicy.Value;

    /// <summary>The never-list over a catalogue of the caller's — what a test declares for the tool it starts.</summary>
    public static CommandPolicy Over(CommandCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        return new CommandPolicy(catalogue);
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
        if (request.Environment is not CommandEnvironment.Clean)
        {
            return CommandVerdict.Refuse($"refused: a command run as {wrapped.User} must start with a clean environment, never this process's: {Shown(request.Argv)}");
        }

        if (NeverList.FirstBroken(wrapped.Argv) is { } broken)
        {
            return Never(broken, request.Argv);
        }

        if (!TargetUserArgv.IsInABinFolder(wrapped.ExecutablePath))
        {
            return CommandVerdict.Refuse($"refused: {Shown([wrapped.ExecutablePath])} is not a file in one of the target user's bin folders ({string.Join(", ", TargetUserArgv.BinFolderSuffixes)}, nvm's node bin)");
        }

        return Catalogue.MatchUser(wrapped) is not null ? CommandVerdict.Allowed : NoTemplate(request.Argv);
    }

    private static CommandVerdict Never(NeverRule rule, IReadOnlyList<string> argv) =>
        CommandVerdict.Refuse($"refused by the never-list ({rule.Id}: {rule.Description}): {Shown(argv)}");

    private static CommandVerdict NoTemplate(IReadOnlyList<string> argv) =>
        CommandVerdict.Refuse($"refused: no declared command template matches {Shown(argv)} (deny by default)");

    /// <summary>The argv for a refusal line: control characters replaced, long arguments cut — a refusal is ONE line.</summary>
    private static string Shown(IReadOnlyList<string> argv) =>
        string.Join(' ', argv.Select(a => new string([.. (a.Length > 120 ? a[..120] + "..." : a).Select(c => char.IsControl(c) ? '?' : c)])));

    private static readonly Lazy<CommandPolicy> ProductPolicy = new(() => new CommandPolicy(CommandCatalogue.Product));
}
