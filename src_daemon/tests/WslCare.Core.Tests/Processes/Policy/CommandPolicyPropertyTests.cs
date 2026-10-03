using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// THE centrepiece of E3.S1 (plan §5: "Tests enforce that no code path can produce these commands"; §12; §15c #3): over
/// thousands of seeded, generated inputs — action ids × config values × preview contents with hostile names, every
/// never-command in many spellings, every declared template with valid and hostile slot values, token soup, all of it
/// also wrapped in <c>runuser</c> — no argv the policy lets through is a never-command (judged by the independent
/// <see cref="NeverOracle"/>), and every argv it lets through is an instance of a DECLARED template.
/// </summary>
/// <remarks>Three properties, each with a companion proving it can go red: the POLICY property (red against a
/// deliberately permissive policy), the TEMPLATE property (red against a planted template whose slot is too wide), and the
/// ACTION property (red against a planted action that builds a shell string). Seeds are fixed, so a failure replays.</remarks>
public sealed class CommandPolicyPropertyTests
{
    private const int Seed = 20261002;
    private const int PolicyCases = 20_000;
    private const int TemplateCasesPerTemplate = 500;
    private const int ActionCases = 2_000;

    /// <summary>What one run of the policy property saw: the violations, and how many of each kind of input it judged —
    /// so a generator that stopped producing never-commands (a property that passes on nothing) is visible.</summary>
    internal sealed record PolicyRun(IReadOnlyList<string> Violations, int NeverCommands, int Allowed, int AllowedTemplateInstances);

    /// <summary>The policy property over <paramref name="cases"/> generated requests.</summary>
    internal static PolicyRun JudgePolicy(Func<CommandRequest, CommandVerdict> review, CommandCatalogue catalogue, int seed, int cases)
    {
        var inputs = new HostileInputs(seed);
        var violations = new List<string>();
        var (never, allowed, instances) = (0, 0, 0);
        for (var i = 0; i < cases; i++)
        {
            var request = inputs.Request(catalogue);
            var isNever = NeverOracle.IsNever(request.Argv) || (Wrapped(request) is { } inner && NeverOracle.IsNever(inner));
            never += isNever ? 1 : 0;
            if (!review(request).IsAllowed)
            {
                continue;
            }

            allowed++;
            var declared = IsDeclaredInstance(request, catalogue);
            instances += declared ? 1 : 0;
            Add(violations, isNever, $"case {i}: ALLOWED a never-command: {Shown(request.Argv)}");
            Add(violations, !declared, $"case {i}: ALLOWED an argv no declared template matches: {Shown(request.Argv)}");
        }

        return new PolicyRun(violations, never, allowed, instances);
    }

    /// <summary>The template property: every declared template, instantiated with values its slots ACCEPT, is never a never-command.</summary>
    internal static IReadOnlyList<string> JudgeTemplates(CommandCatalogue catalogue, int seed, int casesPerTemplate)
    {
        var inputs = new HostileInputs(seed);
        return [.. catalogue.Templates.SelectMany(template => Enumerable.Range(0, casesPerTemplate)
            .Select(_ => inputs.Instance(template, hostile: false))
            .Where(argv => template.Matches([.. argv.Skip(1)]) && NeverOracle.IsNever(argv))
            .Take(3)
            .Select(argv => $"template {template.Name} ({template.Shape}) can produce a never-command: {Shown(argv)}"))];
    }

    /// <summary>The action property: run every action of <paramref name="registry"/> — preview AND run — for generated
    /// configs and generated journal contents, through a runner that applies <paramref name="policy"/>; every argv it asked
    /// for must be allowed, an instance of one of ITS OWN declared templates, and never a never-command.</summary>
    internal static async Task<(IReadOnlyList<string> Violations, int Commands, IReadOnlySet<string> Covered)> JudgeActionsAsync(ActionRegistry registry, CommandPolicy policy, int seed, int cases)
    {
        var inputs = new HostileInputs(seed);
        using var sandbox = new LinuxSandbox("prop-actions");
        var me = new TargetUser("me", 1000, "/home/me");
        foreach (var tool in new[] { "npm", "pnpm", "uv", "pip3", "dotnet" })
        {
            sandbox.Executable("/home/me/.local/bin", tool);
        }

        sandbox.Sized("/home/me/.local/share/NuGet/http-cache/x/y.nupkg", 10, FixedTimeProvider.DefaultNow);
        var violations = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var commands = 0;
        for (var i = 0; i < cases; i++)
        {
            var files = new GeneratedJournal(sandbox.Files, sandbox.Paths, inputs);
            var config = Config(sandbox, inputs);
            TogglePip(sandbox, inputs);
            foreach (var action in registry.Actions)
            {
                var world = new GeneratedWorld(inputs);
                var runner = world.Runner(policy);
                var trigger = inputs.Pick<RunTrigger>([RunTrigger.Timer, RunTrigger.Cli, RunTrigger.Manual]);
                var context = new ActionContext(sandbox.Paths, files, new FixedTimeProvider(), config, trigger, new TargetUserResult.Found(me, "test"))
                {
                    ShownVolumes = inputs.Next(2) == 0 ? ShownList.Of(world.Shown()) : ShownList.None,
                };
                var executor = new ActionCommands(action, runner, context.TargetUser, TargetUserCommands.BinFolders(me, sandbox.Paths, files));
                var preview = await action.PreviewAsync(context, executor, CancellationToken.None);
                await action.RunAsync(context, preview, executor, CancellationToken.None);
                commands += runner.Requests.Count;
                covered.UnionWith(executor.Ran.Where(c => c.Outcome == "exited").Select(c => c.Template));
                violations.AddRange(runner.Requests.SelectMany(r => ActionViolations(action, policy, r, i)));
                violations.AddRange(executor.Ran.Where(c => c.Outcome == "refused").Select(c => $"case {i}: {action.Id} asked for a command its executor refused: {c.Display} ({c.Detail})"));
            }
        }

        return ([.. violations.Distinct().Take(20)], commands, covered);
    }

    /// <summary>pip installed or not, per case: A17 runs <c>pip3</c> only where there is no <c>pip</c>, so both are reached.</summary>
    private static void TogglePip(LinuxSandbox sandbox, HostileInputs inputs)
    {
        var pip = sandbox.Paths.DistroPath("/home/me/.local/bin/pip" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (inputs.Next(2) == 0)
        {
            sandbox.Executable("/home/me/.local/bin", "pip");
        }
        else if (File.Exists(pip))
        {
            File.Delete(pip);
        }
    }

    [Fact]
    public void No_generated_input_gets_a_never_command_past_the_product_policy_and_everything_it_allows_matches_a_declared_template()
    {
        var run = JudgePolicy(CommandPolicy.Product.Review, CommandCatalogue.Product, Seed, PolicyCases);

        run.Violations.Should().BeEmpty();
        run.NeverCommands.Should().BeGreaterThan(PolicyCases / 10, "the generator must keep producing never-commands, or the property passes on nothing");
        run.AllowedTemplateInstances.Should().BeGreaterThan(PolicyCases / 10, "the generator must keep producing allowed template instances, or it only proves that everything is refused");
    }

    [Fact]
    public void A_deliberately_permissive_policy_is_caught_by_the_policy_property()
    {
        // The companion: the property above is only evidence because THIS goes red — a policy that lets everything through.
        var run = JudgePolicy(_ => CommandVerdict.Allowed, CommandCatalogue.Product, Seed, PolicyCases);

        run.Violations.Should().Contain(v => v.Contains("ALLOWED a never-command", StringComparison.Ordinal));
        run.Violations.Should().Contain(v => v.Contains("no declared template matches", StringComparison.Ordinal));
    }

    [Fact]
    public void The_naive_policy_that_asks_the_never_list_of_the_outer_argv_only_lets_undeclared_argv_and_wrapped_never_commands_through()
    {
        // The refuted shape, kept as a test: deny-lists alone admit everything nobody thought of, and a never-command
        // behind runuser's ONE legal shape passes an outer-only check — which is why the policy also requires a declared
        // template AND judges the wrapped command a second time.
        var neverOnly = (CommandRequest r) => NeverList.FirstBroken(r.Argv) is null ? CommandVerdict.Allowed : CommandVerdict.Refuse("never");

        var run = JudgePolicy(neverOnly, CommandCatalogue.Product, Seed, PolicyCases);

        run.Violations.Should().Contain(v => v.Contains("no declared template matches", StringComparison.Ordinal));
        run.Violations.Should().Contain(v => v.Contains("ALLOWED a never-command: runuser", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_declared_template_instantiated_with_any_value_its_slots_accept_is_never_a_never_command()
    {
        JudgeTemplates(CommandCatalogue.Product, Seed, TemplateCasesPerTemplate).Should().BeEmpty();
    }

    [Fact]
    public void A_planted_template_whose_slot_is_too_wide_is_caught_by_the_template_property_and_still_refused_by_the_policy()
    {
        // A1 written wrongly: the value of drop_caches as a slot of 0..3 instead of the literal 1.
        var planted = new CommandTemplate("planted-drop-caches", CommandScope.Machine, "sysctl", [new ArgPart.Literal("-w"), new ArgPart.Slot("value", new SlotKind.Prefixed("vm.drop_caches=", new SlotKind.Number(0, 3)))], TimeSpan.FromSeconds(5), 1024);
        var catalogue = new CommandCatalogue([.. CommandCatalogue.Product.Templates, planted]);

        JudgeTemplates(catalogue, Seed, TemplateCasesPerTemplate).Should().Contain(v => v.Contains("planted-drop-caches", StringComparison.Ordinal));
        CommandPolicy.Over(catalogue).Review(new CommandRequest(["sysctl", "-w", "vm.drop_caches=3"], TimeSpan.FromSeconds(5))).IsAllowed
            .Should().BeFalse("the never-list is asked BEFORE any template: a planted template cannot start a never-command");
        JudgePolicy(CommandPolicy.Over(catalogue).Review, catalogue, Seed, PolicyCases).Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_argv_a_registered_action_asks_for_under_any_config_and_any_journal_is_allowed_declared_and_never_a_never_command()
    {
        var (violations, commands, covered) = await JudgeActionsAsync(ActionRegistry.Product, CommandPolicy.Product, Seed, ActionCases);

        violations.Should().BeEmpty();
        commands.Should().BeGreaterThan(ActionCases * 5, "every case previews and runs every action");
        // Derived, not retyped: EVERY template a registered action declares must have run in some generated case, or the
        // property passed without exercising it (E3.S2: the removals and prunes are reached with hostile names around them).
        ActionRegistry.Product.Actions.SelectMany(a => a.Commands).Select(t => t.Name).Distinct().Should().BeSubsetOf(covered);
    }

    [Fact]
    public async Task A_planted_action_that_builds_a_shell_string_from_a_preview_name_is_caught_by_the_action_property()
    {
        var registry = new ActionRegistry([new PlantedShellAction()]);
        var policy = CommandPolicy.Over(new CommandCatalogue([.. CommandCatalogue.Product.Templates, .. registry.Actions.SelectMany(a => a.Commands)]));

        var (violations, _, _) = await JudgeActionsAsync(registry, policy, Seed, 50);

        violations.Should().Contain(v => v.Contains("refused", StringComparison.Ordinal) && v.Contains("never-list", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ActionViolations(ICleanupAction action, CommandPolicy policy, CommandRequest request, int i)
    {
        if (!policy.Review(request).IsAllowed)
        {
            yield return $"case {i}: {action.Id} produced an argv the policy refused: {Shown(request.Argv)} ({((CommandVerdict.Refused)policy.Review(request)).Reason})";
        }

        if (NeverOracle.IsNever(request.Argv) || (Wrapped(request) is { } inner && NeverOracle.IsNever(inner)))
        {
            yield return $"case {i}: {action.Id} produced a never-command: {Shown(request.Argv)}";
        }

        var wrapped = TargetUserArgv.Parse(request.Argv);
        var declared = wrapped is null
            ? action.Commands.Any(t => t.Scope == CommandScope.Machine && t.Executable == request.Argv[0] && t.Matches([.. request.Argv.Skip(1)]))
            : action.Commands.Any(t => t.Scope == CommandScope.User && t.Executable == wrapped.ExecutableName && t.Matches(wrapped.Arguments));
        if (!declared)
        {
            yield return $"case {i}: {action.Id} produced an argv none of ITS templates declares: {Shown(request.Argv)}";
        }
    }

    /// <summary>A generated configuration: the user layer with random values for every key the actions read (the loader
    /// validates them as it always does — an out-of-range one makes the run observe-only, which is also an input).</summary>
    private static EffectiveConfig Config(LinuxSandbox sandbox, HostileInputs inputs)
    {
        var keep = inputs.Pick<long>([1, 2, 7, 30, 365, 3650, inputs.Next(3650) + 1]);
        int Pick(params int[] values) => inputs.Pick<int>(values);
        sandbox.Write("/home/me/.config/wsl-care/config.json", $$"""
            { "journal": { "keepDays": {{keep}} }, "dryRun": {{(inputs.Next(2) == 0 ? "true" : "false")}},
              "volumes": { "anonymousOlderThanDays": {{Pick(0, 1)}} }, "containers": { "stoppedOlderThanDays": {{Pick(0, 7)}}, "testcontainersOlderThanHours": {{Pick(0, 2)}} },
              "images": { "unusedOlderThanDays": {{Pick(0, 7)}} }, "buildCache": { "maxGb": {{Pick(0, 20)}}, "olderThanDays": {{Pick(0, 7)}} } }
            """);
        return ConfigLoader.Load(sandbox.Paths, sandbox.Files).Config;
    }

    private static IReadOnlyList<string>? Wrapped(CommandRequest request) => TargetUserArgv.Parse(request.Argv)?.Argv;

    private static bool IsDeclaredInstance(CommandRequest request, CommandCatalogue catalogue) =>
        TargetUserArgv.Parse(request.Argv) is { } wrapped
            ? catalogue.Templates.Any(t => t.Scope == CommandScope.User && t.Executable == wrapped.ExecutableName && t.Matches(wrapped.Arguments))
            : catalogue.Templates.Any(t => t.Scope == CommandScope.Machine && t.Executable == request.Argv[0] && t.Matches([.. request.Argv.Skip(1)]));

    private static void Add(List<string> violations, bool when, string violation)
    {
        if (when && violations.Count < 50)
        {
            violations.Add(violation);
        }
    }

    private static string Shown(IReadOnlyList<string> argv) => string.Join(' ', argv.Select(a => a.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\0", "\\0", StringComparison.Ordinal)));

    /// <summary>A journal of generated files — hostile names, random sizes and ages — answered from memory, so thousands of
    /// cases cost no disk.</summary>
    private sealed class GeneratedJournal(IFileSystem inner, Core.Hosting.LinuxHostPaths paths, HostileInputs inputs) : DelegatingFileSystem(inner)
    {
        private readonly Dictionary<string, FileSizeResult> _files = Enumerable.Range(0, inputs.Next(12))
            .Select(i => paths.Rules.Join(paths.JournalDirectories[0], "machine", $"{(inputs.Next(2) == 0 ? "system@" : string.Empty)}{inputs.HostileValue().Replace('/', '_').Replace('\\', '_')}{i}.journal"))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, _ => (FileSizeResult)new FileSizeResult.Measured(inputs.Next(1 << 20), FixedTimeProvider.DefaultNow.AddDays(-inputs.Next(400))), StringComparer.Ordinal);

        public override IReadOnlyList<string> ListDirectories(string path) =>
            string.Equals(path, paths.JournalDirectories[0], StringComparison.Ordinal) ? [paths.Rules.Join(path, "machine")] : [];

        public override IReadOnlyList<string> ListFiles(string path) =>
            [.. _files.Keys.Where(f => string.Equals(f[..f.LastIndexOf('/')], path, StringComparison.Ordinal))];

        public override FileSizeResult FileSize(string path) => _files.GetValueOrDefault(path) ?? new FileSizeResult.Missing();
    }

    /// <summary>A WRONG action, planted: it declares a shell template and puts a preview name into its command string —
    /// exactly what plan §15c #3 forbids. The policy refuses it at run time; the property must SEE that it tried.</summary>
    private sealed class PlantedShellAction : ICleanupAction
    {
        private static readonly CommandTemplate Shell = new("planted-shell", CommandScope.Machine, "sh", [new ArgPart.Literal("-c"), new ArgPart.Slot("script", new SlotKind.OneOf(["rm -rf x; rm -rf ~/git"]))], TimeSpan.FromSeconds(5), 1024);

        public ActionId Id { get; } = ActionId.Find("A17")!;

        public string Summary => "planted: a shell string built from names";

        public CommandScope Scope => CommandScope.Machine;

        public IdleRule Idle => IdleRule.Never;

        public IReadOnlyList<Core.Hosting.HostSide> Sides { get; } = [Core.Hosting.HostSide.Wsl];

        public IReadOnlyList<CommandTemplate> Commands { get; } = [Shell];

        public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionPreview("planted", true, null, 1, 1, "planted", new Dictionary<string, long>(), string.Empty, [new ActionItem("file", "x; rm -rf ~/git", 1)]));

        public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => new(true, "planted");

        public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
        {
            await commands.RunAsync(Shell, [$"rm -rf {preview.Items[0].Name}"], cancellationToken);
            return new ActionRun(0, 0, "planted", null, null, [], commands.Ran, string.Empty);
        }
    }
}
