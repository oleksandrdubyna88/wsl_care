using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// E19.S4: the three read-only git questions as DECLARED templates (<see cref="ReadCommandTemplates.GitTopLevel"/> and its siblings), the
/// two new slot kinds, and the proof that no instance of them can be bent into <c>worktree prune</c>, a removal or <c>clean</c>. The
/// property suite (<c>CommandPolicyPropertyTests</c>) covers the same templates with generated values; these are the named cases.
/// </summary>
public sealed class GitTemplatesTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);
    private static readonly SlotKind.RelativeName Name = new();
    private static readonly SlotKind.AdminFolder Admin = new();

    private static CommandRequest Request(IReadOnlyList<string> argv) => new(argv, Ceiling, @"D:\rsd\_wt\x");

    public static TheoryData<string> RefusedNames =>
    [
        "", "-x", "--force", "..", "../x", "a/../b", "a/./b", "./a", "a//b", "//a", "/abs", @"\x", @"a\b", @"C:\x", "C:x", ":x", "a:b", ":(glob)x", ":(top)", "clean", "CLEAN", "prune", "worktree", "worktree/",
        "a*b", "a?b", "a[b", "*", "a\nb", "a\0b", "a\tb", "a\u001bb", "a\"b", "a<b", "a>b", "a|b", new string('a', 261),
    ];

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public void A_relative_name_with_dot_dot_or_magic_is_refused(string name)
    {
        Name.Accepts(name).Should().BeFalse($"\"{name.Replace("\n", "\\n", StringComparison.Ordinal)}\" is not a literal name beneath the working directory");
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj/")]
    [InlineData("node_modules")]
    [InlineData("src/App/bin")]
    [InlineData("my project/bin")]
    [InlineData("résumé/obj")]
    [InlineData("собранный/bin")]
    [InlineData("a..b/bin")]
    public void A_name_a_candidate_can_have_is_accepted(string name)
    {
        Name.Accepts(name).Should().BeTrue();
    }

    [Fact]
    public void A_relative_name_of_exactly_260_characters_is_accepted_and_one_more_is_not()
    {
        Name.Accepts(new string('a', 260)).Should().BeTrue();
        Name.Accepts(new string('a', 261)).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\repo\.git")]
    [InlineData(@"D:/rsd/x/.git")]
    [InlineData(@"E:\a b\.git\")]
    public void An_admin_folder_is_a_plain_drive_path(string path)
    {
        Admin.Accepts(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:")]
    [InlineData(@"C:\")]
    [InlineData(@"repo\.git")]
    [InlineData("/home/me/repo/.git")]
    [InlineData(@"\\server\share\.git")]
    [InlineData(@"\\?\C:\repo\.git")]
    [InlineData(@"C:\repo\..\x")]
    [InlineData(@"C:\repo\.\x")]
    [InlineData(@"C:\repo\\x")]
    [InlineData(@"C:\re*po\.git")]
    [InlineData(@"C:\re?po\.git")]
    [InlineData(@"C:\a|b")]
    [InlineData(@"C:\repo:stream\.git")]
    [InlineData("C:\\repo\n\\.git")]
    [InlineData("--git-dir=C:\\repo")]
    public void An_admin_folder_that_is_anything_else_is_refused(string path)
    {
        Admin.Accepts(path).Should().BeFalse();
    }

    private static IReadOnlyList<string> Questions(params string[] names) => names;

    public static TheoryData<string> GitTemplateNames => ["git-toplevel", "git-ls-files", "git-check-ignore", "git-check-ignore-orphan"];

    private static CommandTemplate Template(string name) => ReadCommandTemplates.All.Single(t => t.Name == name);

    [Theory]
    [MemberData(nameof(GitTemplateNames))]
    public void Every_git_template_is_declared_on_the_bare_program_and_begins_with_the_isolating_options(string name)
    {
        var template = Template(name);

        template.Executable.Should().Be("git.exe", "the bare name is judged; the file started is GitExecutable's, never a PATH lookup");
        template.Parts.Take(7).Select(p => ((ArgPart.Literal)p).Text).Should().Equal(
            "-c", "core.fsmonitor=false", "-c", "core.excludesFile=", "-c", "core.quotePath=false", "--no-optional-locks");
        template.Scope.Should().Be(CommandScope.Machine);
    }

    [Fact]
    public void The_four_commands_the_scan_builds_are_allowed_by_the_product_policy_and_are_instances_of_their_own_template()
    {
        IReadOnlyList<(IReadOnlyList<string> Argv, string Template)> built =
        [
            (GitCommands.TopLevel(), "git-toplevel"),
            (GitCommands.TrackedFiles(Questions("bin", "obj/")), "git-ls-files"),
            (GitCommands.CheckIgnore(Questions("bin", "obj/")), "git-check-ignore"),
            (GitCommands.CheckIgnoreOrphan(@"C:\repo\.git", Questions("bin", "obj/")), "git-check-ignore-orphan"),
        ];

        foreach (var (argv, name) in built)
        {
            var verdict = CommandPolicy.Product.Review(Request(argv));
            verdict.Should().Be(CommandVerdict.Allowed, (verdict as CommandVerdict.Refused)?.Reason ?? name);
            CommandCatalogue.Product.MatchMachine(argv)!.Name.Should().Be(name);
        }
    }

    [Fact]
    public void The_check_ignore_commands_carry_no_z_and_no_literal_pathspecs_because_git_refuses_both()
    {
        // Measured 2026-10-15 (research/2026-10-15_disk_hygiene_git_probe.md): `check-ignore -z` without --stdin and
        // `check-ignore --literal-pathspecs` both exit 128. ls-files keeps both.
        GitCommands.CheckIgnore(Questions("bin")).Should().NotContain(["-z", "--literal-pathspecs"]);
        GitCommands.CheckIgnoreOrphan(@"C:\r\.git", Questions("bin")).Should().NotContain(["-z", "--literal-pathspecs"]);
        GitCommands.TrackedFiles(Questions("bin")).Should().Contain(["-z", "--literal-pathspecs", "--cached"]);
    }

    [Fact]
    public void A_template_takes_at_most_the_most_names_one_call_may_carry()
    {
        var most = Core.Config.ConfigKeys.WorktreeGit.NamesPerCall.Max;
        var many = Enumerable.Range(0, most).Select(i => $"d{i}/bin").ToArray();

        CommandPolicy.Product.Review(Request(GitCommands.CheckIgnore(many))).IsAllowed.Should().BeTrue();
        CommandPolicy.Product.Review(Request(GitCommands.CheckIgnore([.. many, "one/more"]))).IsAllowed.Should().BeFalse();
        CommandPolicy.Product.Review(Request(GitCommands.CheckIgnore([]))).IsAllowed.Should().BeFalse("a call with no name asks nothing");
    }

    [Fact]
    public void The_git_templates_cannot_be_bent_into_prune_remove_or_clean()
    {
        var prefix = GitCommands.Options;
        string[][] bent =
        [
            ["git.exe", .. prefix, "worktree", "prune"],
            ["git.exe", .. prefix, "worktree", "remove", "--force", "x"],
            ["git.exe", .. prefix, "clean", "-fdx"],
            ["git.exe", .. prefix, "rm", "-r", "bin"],
            ["git.exe", .. prefix, "rev-parse", "--show-toplevel", "clean"],
            ["git.exe", .. prefix, "check-ignore", "-v", "-n", "--", "bin", "--force"],
            ["git.exe", .. prefix, "check-ignore", "-v", "-n", "--", "-fdx"],
            ["git.exe", .. prefix, "check-ignore", "-v", "-n", "--", ".."],
            ["git.exe", .. prefix, "check-ignore", "-v", "-n", "--", ":(top)bin"],
            ["git.exe", .. prefix, "--literal-pathspecs", "ls-files", "-z", "--cached", "--", "bin", "*"],
            ["git.exe", .. prefix, "--literal-pathspecs", "ls-files", "-z", "--cached", "--others", "--", "bin"],
            ["git.exe", .. prefix, "--literal-pathspecs", "ls-files", "-z", "--cached", "--", "bin", @"..\other"],
            ["git.exe", .. prefix, "--git-dir=C:\\repo\\.git", "--work-tree=.", "worktree", "prune"],
            ["git.exe", .. prefix, "--git-dir=C:\\repo\\.git", "--work-tree=C:\\", "check-ignore", "--no-index", "-v", "-n", "--", "bin"],
            ["git.exe", .. prefix, "--git-dir=C:\\repo\\..\\.git", "--work-tree=.", "check-ignore", "--no-index", "-v", "-n", "--", "bin"],
            ["git.exe", "-c", "alias.x=!rm -rf .", .. prefix, "rev-parse", "--show-toplevel"],
            ["git.exe", "-c", "core.fsmonitor=true", "-c", "core.excludesFile=", "-c", "core.quotePath=false", "--no-optional-locks", "rev-parse", "--show-toplevel"],
            ["git.exe", .. prefix, "worktree", "prune", "--", "bin"],
        ];

        foreach (var argv in bent)
        {
            CommandPolicy.Product.Review(Request(argv)).IsAllowed.Should().BeFalse($"{string.Join(' ', argv)} must be refused");
        }
    }

    [Fact]
    public void Names_that_spell_clean_or_worktree_prune_are_refused_by_the_slot_and_would_be_refused_by_the_never_list_anyway()
    {
        // The slot refuses the destructive words as WHOLE names, so no instance of a git template can ever carry them (the template
        // property found "worktree" and "prune" side by side). The never-list stays the second wall: asked first, whatever a slot says.
        CommandPolicy.Product.Review(Request(GitCommands.CheckIgnore(Questions("bin", "clean")))).IsAllowed.Should().BeFalse();
        CommandPolicy.Product.Review(Request(GitCommands.CheckIgnore(Questions("worktree", "prune")))).IsAllowed.Should().BeFalse();
        NeverList.FirstBroken(GitCommands.CheckIgnore(Questions("worktree", "prune")))!.Id.Should().Be("git-worktree-prune");
        NeverList.FirstBroken(GitCommands.CheckIgnore(Questions("clean")))!.Id.Should().Be("delete-by-command");
        Name.Accepts("cleaner").Should().BeTrue("only the whole word is a git subcommand");
        Name.Accepts("tools/clean").Should().BeTrue("a name with a folder in front is not an argv word");
    }

    [Fact]
    public void An_orphan_admin_folder_under_a_protected_home_folder_is_refused_by_the_never_list_not_worked_around()
    {
        // A path on argv under <home>\git is the never-list's protected-path rule. The orphan question then fails SAFE (it is retained as
        // "not confirmed by git"); nothing weakens the rule to make it pass.
        var verdict = CommandPolicy.Product.Review(Request(GitCommands.CheckIgnoreOrphan(@"C:\Users\me\git\repo\.git", Questions("bin"))));

        verdict.Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("protected-path");
    }

    [Fact]
    public void Every_git_template_instance_the_generator_builds_matches_only_its_own_template()
    {
        var inputs = new HostileInputs(20261015);
        var git = ReadCommandTemplates.All.Where(t => t.Executable == "git.exe").ToList();
        git.Should().HaveCount(4);

        foreach (var template in git)
        {
            var argv = inputs.Instance(template, hostile: false);
            CommandCatalogue.Product.Templates.Where(t => t.Scope == CommandScope.Machine && t.Executable == argv[0] && t.Matches([.. argv.Skip(1)]))
                .Select(t => t.Name).Should().Equal(template.Name);
        }
    }
}
