using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// E19.S4, decision 9: <c>git.exe</c> is started only when no non-administrator can replace it or plant a file beside it, and never because
/// it is on <c>PATH</c>. The rule (<see cref="AdminOnlyJudge"/>) is pure over security-descriptor facts, so every clause is a case here; the
/// two Windows legs run against REAL access-control lists (the supported Program Files install, and a user-writable folder).
/// </summary>
public sealed class GitExecutableTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string System = "S-1-5-18";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";

    private static AclFacts Owned(string owner, params AclEntry[] entries) => new(owner, entries);

    private static AclEntry Allow(string sid, AclRight rights, bool inheritOnly = false) => new(sid, Allow: true, rights, inheritOnly);

    private static readonly AclRight Everything = AclRight.WriteData | AclRight.AppendData | AclRight.DeleteSubdirectoriesAndFiles | AclRight.Delete | AclRight.ChangePermissions | AclRight.TakeOwnership;

    public static TheoryData<AdminOnlyRole> Roles => [AdminOnlyRole.ProgramFile, AdminOnlyRole.ProgramFolder, AdminOnlyRole.Ancestor];

    [Theory]
    [MemberData(nameof(Roles))]
    public void Administrators_system_and_trusted_installer_may_do_anything(AdminOnlyRole role)
    {
        var facts = Owned(Administrators, Allow(Administrators, Everything), Allow(System, Everything), Allow(TrustedInstaller, Everything));

        AdminOnlyJudge.Problem(facts, role).Should().BeEmpty();
    }

    [Theory]
    [InlineData(AclRight.WriteData)]
    [InlineData(AclRight.AppendData)]
    [InlineData(AclRight.Delete)]
    [InlineData(AclRight.ChangePermissions)]
    [InlineData(AclRight.TakeOwnership)]
    public void A_program_file_a_user_could_write_append_delete_or_re_permission_is_refused(AclRight right)
    {
        var facts = Owned(Administrators, Allow(Administrators, Everything), Allow(Users, right));

        AdminOnlyJudge.Problem(facts, AdminOnlyRole.ProgramFile).Should().Contain(Users).And.Contain(right.ToString());
    }

    [Fact]
    public void A_program_file_users_may_only_read_and_execute_is_accepted()
    {
        // Read, execute, synchronize carry none of the forbidden bits.
        var facts = Owned(TrustedInstaller, Allow(Users, AclRight.None), Allow(AuthenticatedUsers, AclRight.None));

        AdminOnlyJudge.Problem(facts, AdminOnlyRole.ProgramFile).Should().BeEmpty();
    }

    [Fact]
    public void A_folder_a_user_could_create_a_file_in_is_refused_but_creating_a_subfolder_is_not_a_plant_beside_the_program()
    {
        AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, AclRight.WriteData)), AdminOnlyRole.ProgramFolder).Should().Contain(Users);
        AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, AclRight.AppendData)), AdminOnlyRole.ProgramFolder).Should().BeEmpty();
    }

    [Theory]
    [InlineData(AclRight.Delete)]
    [InlineData(AclRight.DeleteSubdirectoriesAndFiles)]
    [InlineData(AclRight.ChangePermissions)]
    [InlineData(AclRight.TakeOwnership)]
    public void An_ancestor_a_user_could_delete_a_child_of_or_re_permission_is_refused(AclRight right)
    {
        AdminOnlyJudge.Problem(Owned(System, Allow(AuthenticatedUsers, right)), AdminOnlyRole.Ancestor).Should().Contain(AuthenticatedUsers);
        AdminOnlyJudge.Problem(Owned(System, Allow(AuthenticatedUsers, right)), AdminOnlyRole.ProgramFolder).Should().Contain(AuthenticatedUsers, "the program's own folder is held to the ancestor rule too");
    }

    [Fact]
    public void Creating_an_unrelated_folder_or_file_under_an_ancestor_cannot_replace_a_path_component_and_is_allowed()
    {
        // C:\ grants Authenticated Users CreateDirectories (measured 2026-10-15); that does not touch C:\Program Files.
        var facts = Owned(System, Allow(AuthenticatedUsers, AclRight.AppendData | AclRight.WriteData));

        AdminOnlyJudge.Problem(facts, AdminOnlyRole.Ancestor).Should().BeEmpty();
    }

    [Fact]
    public void An_entry_that_only_applies_to_children_says_nothing_about_the_object_itself()
    {
        var facts = Owned(System, Allow(AuthenticatedUsers, Everything, inheritOnly: true), Allow(CreatorOwner, Everything, inheritOnly: true));

        AdminOnlyJudge.Problem(facts, AdminOnlyRole.ProgramFile).Should().BeEmpty();
        AdminOnlyJudge.Problem(facts, AdminOnlyRole.Ancestor).Should().BeEmpty();
    }

    [Fact]
    public void A_deny_entry_never_admits_anything_and_an_allow_beside_it_is_still_judged()
    {
        var deny = new AclEntry(Users, Allow: false, Everything, InheritOnly: false);

        AdminOnlyJudge.Problem(Owned(Administrators, deny), AdminOnlyRole.ProgramFile).Should().BeEmpty();
        AdminOnlyJudge.Problem(Owned(Administrators, deny, Allow(Users, AclRight.WriteData)), AdminOnlyRole.ProgramFile).Should().Contain(Users);
    }

    [Fact]
    public void An_owner_who_is_no_administrator_can_re_permission_the_object_and_is_refused_even_with_no_entry_for_it()
    {
        var facts = Owned("S-1-5-21-1-2-3-1001", Allow(Administrators, Everything));

        AdminOnlyJudge.Problem(facts, AdminOnlyRole.Ancestor).Should().Contain("S-1-5-21-1-2-3-1001").And.Contain("owned");
    }

    /// <summary>A program file with only a header; one named <c>.../cmd/git.exe</c> comes with the DLL folder git for Windows always has beside it,
    /// <c>mingw64\bin</c> holding a <c>git.exe</c> of its own (the one <c>cmd\git.exe</c> starts), unless <paramref name="withDllFolder"/> is false.</summary>
    private static string DummyProgram(ThrowAwayFolder folder, string relative, string header = "MZ", bool withDllFolder = true)
    {
        var exe = folder.Write(relative, header + "\0\0 this is not a program, only its header");
        if (withDllFolder && relative.EndsWith("/cmd/git.exe", StringComparison.Ordinal))
        {
            folder.Write(relative[..^"/cmd/git.exe".Length] + "/mingw64/bin/git.exe", header + "\0\0 the program cmd\\git.exe starts");
        }

        return exe;
    }

    private sealed class ScriptedAcl(Func<string, AdminOnlyRole, string> answer) : IAdminOnlyPath
    {
        public List<(string Path, AdminOnlyRole Role)> Asked { get; } = [];

        public string Problem(string path, AdminOnlyRole role)
        {
            Asked.Add((path, role));
            return answer(path, role);
        }
    }

    [Fact]
    public void A_pe_file_whose_every_path_is_admin_only_is_accepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var acl = new ScriptedAcl((_, _) => string.Empty);

        var resolved = new GitExecutable(() => [exe], acl).Resolve();

        resolved.Should().Be(new ResolvedExecutable.Found(exe));
    }

    [Theory]
    [InlineData(AclRight.GenericAll)]
    [InlineData(AclRight.GenericWrite)]
    [InlineData(AclRight.MaximumAllowed)]
    public void A_generic_right_that_includes_write_is_refused_in_every_role(AclRight generic)
    {
        // An entry may carry GENERIC_ALL / GENERIC_WRITE / MAXIMUM_ALLOWED unmapped; dropping those bits read such an entry as "no rights".
        foreach (var role in new[] { AdminOnlyRole.ProgramFile, AdminOnlyRole.ProgramFolder, AdminOnlyRole.Ancestor })
        {
            AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, generic)), role).Should().Contain(Users, role.ToString());
        }
    }

    [Fact]
    public void The_raw_generic_all_bit_of_an_access_rule_is_a_right_the_judge_keeps()
    {
        AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, (AclRight)0x10000000)), AdminOnlyRole.ProgramFile).Should().Contain(Users);
        AdminOnlyJudge.KnownRights(0x10000000 | 0x001F01FF).Should().HaveFlag(AclRight.GenericAll).And.HaveFlag(AclRight.WriteData).And.HaveFlag(AclRight.Delete);
        AdminOnlyJudge.KnownRights(0x001200A9).Should().Be(AclRight.None, "read and execute carry none of the bits the rule looks at");
    }

    [Theory]
    [InlineData(unchecked((int)0x80000000))]
    [InlineData(0x20000000)]
    public void Generic_read_and_generic_execute_alone_grant_no_write_and_are_accepted(int bits)
    {
        var rights = AdminOnlyJudge.KnownRights(bits);

        AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, rights)), AdminOnlyRole.ProgramFile).Should().BeEmpty();
    }

    [Fact]
    public void The_file_its_folder_mingw64_bin_and_every_ancestor_up_to_the_volume_root_are_all_asked_with_their_role()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var acl = new ScriptedAcl((_, _) => string.Empty);

        new GitExecutable(() => [exe], acl).Resolve().Should().BeOfType<ResolvedExecutable.Found>();

        var root = Path.GetPathRoot(exe)!;
        acl.Asked.Should().Contain((exe, AdminOnlyRole.ProgramFile));
        acl.Asked.Should().Contain((folder.Under("Git/mingw64/bin/git.exe"), AdminOnlyRole.ProgramFile), "the program cmd\\git.exe starts is judged as a program file too");
        acl.Asked.Should().Contain((folder.Under("Git/cmd"), AdminOnlyRole.ProgramFolder));
        acl.Asked.Should().Contain((folder.Under("Git/mingw64/bin"), AdminOnlyRole.ProgramFolder));
        acl.Asked.Should().Contain((folder.Under("Git/mingw64"), AdminOnlyRole.Ancestor));
        acl.Asked.Should().Contain((folder.Under("Git"), AdminOnlyRole.Ancestor));
        acl.Asked.Should().Contain((folder.Path, AdminOnlyRole.Ancestor));
        acl.Asked.Should().Contain((root, AdminOnlyRole.Ancestor), "the chain runs to the volume root");
        acl.Asked.Select(a => a.Path).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_dll_folder_git_for_windows_may_have_is_checked_when_it_exists_and_its_git_exe_is_judged_as_a_program()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        folder.Write("Git/mingw32/bin/git.exe", "MZ\0\0");
        folder.Write("Git/clangarm64/bin/git.exe", "MZ\0\0");
        var acl = new ScriptedAcl((_, _) => string.Empty);

        new GitExecutable(() => [exe], acl).Resolve().Should().BeOfType<ResolvedExecutable.Found>();

        foreach (var bin in new[] { "mingw64", "mingw32", "clangarm64" })
        {
            acl.Asked.Should().Contain((folder.Under($"Git/{bin}/bin"), AdminOnlyRole.ProgramFolder), bin);
            acl.Asked.Should().Contain((folder.Under($"Git/{bin}/bin/git.exe"), AdminOnlyRole.ProgramFile), bin);
        }
    }

    [Fact]
    public void A_program_in_a_dll_folder_a_user_could_write_to_refuses_the_candidate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var started = folder.Under("Git/mingw64/bin/git.exe");
        var acl = new ScriptedAcl((path, role) => path == started && role == AdminOnlyRole.ProgramFile ? "lets S-1-5-32-545 (WriteData) change it" : string.Empty);

        new GitExecutable(() => [exe], acl).Resolve().Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("is not admin-only").And.Contain(started);
    }

    [Fact]
    public void A_git_with_no_dll_folder_beside_it_is_refused_because_what_it_starts_cannot_be_named()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe", withDllFolder: false);

        new GitExecutable(() => [exe], new ScriptedAcl((_, _) => string.Empty)).Resolve()
            .Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("mingw64").And.Contain("mingw32").And.Contain("clangarm64");
    }

    [Fact]
    public void A_git_exe_that_sits_in_a_dll_folder_itself_is_accepted_and_judged_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        DummyProgram(folder, "Git/cmd/git.exe");
        var exe = folder.Under("Git/mingw64/bin/git.exe");
        var acl = new ScriptedAcl((_, _) => string.Empty);

        new GitExecutable(() => [exe], acl).Resolve().Should().Be(new ResolvedExecutable.Found(exe));
        acl.Asked.Select(a => a.Path).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_file_that_is_not_a_windows_program_is_refused_and_says_so()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe", header: "#!");

        var resolved = new GitExecutable(() => [exe], new ScriptedAcl((_, _) => string.Empty)).Resolve();

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("no MZ header").And.Contain(exe);
    }

    [Fact]
    public void A_candidate_that_does_not_exist_is_refused_and_a_later_one_is_taken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var good = DummyProgram(folder, "Other/cmd/git.exe");
        var missing = folder.Under("Git/cmd/git.exe");

        var resolved = new GitExecutable(() => [missing, good], new ScriptedAcl((_, _) => string.Empty)).Resolve();

        resolved.Should().Be(new ResolvedExecutable.Found(good));
        new GitExecutable(() => [missing], new ScriptedAcl((_, _) => string.Empty)).Resolve()
            .Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("is not a file");
    }

    [Fact]
    public void One_path_a_user_could_change_refuses_the_candidate_and_names_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var writable = folder.Under("Git");
        var acl = new ScriptedAcl((path, _) => path == writable ? "lets S-1-5-32-545 (Delete) change it" : string.Empty);

        var resolved = new GitExecutable(() => [exe], acl).Resolve();

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("is not admin-only").And.Contain(writable).And.Contain("S-1-5-32-545");
    }

    [Fact]
    public void No_candidate_at_all_is_a_named_refusal()
    {
        new GitExecutable(() => [], new ScriptedAcl((_, _) => string.Empty)).Resolve()
            .Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("never looked up on PATH");
    }

    [Fact]
    public void Git_is_never_taken_from_PATH()
    {
        using var folder = new ThrowAwayFolder("gitexe");
        var onPath = DummyProgram(folder, "bin/git.exe");
        var withoutCandidates = new GitExecutable(() => [], new ScriptedAcl((_, _) => string.Empty));

        // The control: the generic resolver WOULD have taken the program on PATH.
        ExecutableResolver.Resolve("git.exe", Path.GetDirectoryName(onPath), windows: true).Should().Be(new ResolvedExecutable.Found(onPath));
        // The product's way of resolving git.exe never asks PATH, whatever PATH holds.
        ProductExecutables.Resolve("git.exe", withoutCandidates, TestContext.Current.CancellationToken).Should().BeOfType<ResolvedExecutable.NotFound>();
    }

    [Fact]
    public void Every_other_program_is_still_resolved_the_way_it_always_was()
    {
        using var folder = new ThrowAwayFolder("gitexe");
        var onPath = DummyProgram(folder, "bin/docker.exe");
        var pathVariable = Path.GetDirectoryName(onPath);

        ExecutableResolver.Resolve("docker.exe", pathVariable, windows: true).Should().Be(new ResolvedExecutable.Found(onPath));
        ProductExecutables.Resolve("not-a-program-anyone-has.exe", new GitExecutable(() => [], new ScriptedAcl((_, _) => string.Empty)), TestContext.Current.CancellationToken)
            .Should().BeOfType<ResolvedExecutable.NotFound>();
    }

    [Fact]
    public void The_candidates_are_each_program_files_install_once_then_the_machine_layers_choice()
    {
        string Env(string name) => name switch { "ProgramFiles" => @"C:\Program Files (x86)", "ProgramW6432" => @"C:\Program Files", _ => string.Empty };

        GitExecutable.CandidatesOf(Env, @"D:\tools\git\cmd\git.exe").Should().Equal(
            @"C:\Program Files (x86)\Git\cmd\git.exe", @"C:\Program Files\Git\cmd\git.exe", @"D:\tools\git\cmd\git.exe");
        GitExecutable.CandidatesOf(_ => @"C:\Program Files", string.Empty).Should().Equal(@"C:\Program Files\Git\cmd\git.exe");
        GitExecutable.CandidatesOf(_ => string.Empty, string.Empty).Should().BeEmpty("with no Program Files variable and no key there is nothing to try, and PATH is not a source");
    }

    [Fact]
    public void The_supported_Program_Files_git_is_accepted()
    {
        Assert.SkipUnless(RealGit.Available, "this machine has no Git for Windows under Program Files");

        var resolved = GitExecutable.Product.Resolve();

        resolved.Should().Be(new ResolvedExecutable.Found(RealGit.Executable), (resolved as ResolvedExecutable.NotFound)?.Reason ?? "the supported install is admin-only by real ACLs");
    }

    [Fact]
    public void A_judgement_is_reused_while_the_program_files_are_the_same_files_and_judged_afresh_when_one_is_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var refuse = false;
        var acl = new ScriptedAcl((_, _) => refuse ? "lets S-1-5-32-545 (WriteData) change it" : string.Empty);
        var git = new GitExecutable(() => [exe], acl, reuseJudgement: true);

        git.Resolve().Should().Be(new ResolvedExecutable.Found(exe));
        var asked = acl.Asked.Count;
        asked.Should().BeGreaterThan(0);
        refuse = true;
        git.Resolve().Should().Be(new ResolvedExecutable.Found(exe), "the same files: the earlier judgement stands within one process");
        acl.Asked.Count.Should().Be(asked, "no descriptor was read again");

        File.WriteAllText(folder.Under("Git/mingw64/bin/git.exe"), "MZ\0\0 a different program, replaced after the judgement");
        git.Resolve().Should().BeOfType<ResolvedExecutable.NotFound>("a replaced program is judged afresh, and the permissions now refuse it");
        acl.Asked.Count.Should().BeGreaterThan(asked);
    }

    [Fact]
    public void A_reused_judgement_is_good_for_sixty_seconds_and_then_the_descriptors_are_read_again()
    {
        // The identity covers the program files, NOT the DLLs they load or the permissions of the folders above: that gap is bounded in time.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var acl = new ScriptedAcl((_, _) => string.Empty);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var git = new GitExecutable(() => [exe], acl, reuseJudgement: true, clock);

        git.Resolve().Should().Be(new ResolvedExecutable.Found(exe));
        var first = acl.Asked.Count;
        clock.Advance(TimeSpan.FromSeconds(59));
        git.Resolve();
        acl.Asked.Count.Should().Be(first, "inside the 60 s window the descriptors are not read again");

        clock.Advance(TimeSpan.FromSeconds(2));
        git.Resolve();
        var second = acl.Asked.Count;
        second.Should().Be(first * 2, "at 61 s the judgement has expired and every descriptor is read again");

        clock.Advance(TimeSpan.FromSeconds(30));
        git.Resolve();
        acl.Asked.Count.Should().Be(second, "the window restarts from the new full pass");
    }

    [Theory]
    [InlineData(AclRight.WriteAttributes)]
    [InlineData(AclRight.WriteExtendedAttributes)]
    public void Writing_attributes_is_refused_on_the_program_and_its_folder_but_not_on_an_ancestor(AclRight right)
    {
        // WriteAttributes moves a file's timestamps — the very identity a reused judgement is checked by — and extended attributes carry alternate data.
        // Not on an ancestor: C:\ grants Authenticated Users append rights by default, and that must keep passing.
        AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, right)), AdminOnlyRole.ProgramFile).Should().Contain(Users);
        AdminOnlyJudge.Problem(Owned(Administrators, Allow(Users, right)), AdminOnlyRole.ProgramFolder).Should().Contain(Users);
        AdminOnlyJudge.Problem(Owned(System, Allow(AuthenticatedUsers, right)), AdminOnlyRole.Ancestor).Should().BeEmpty();
        AdminOnlyJudge.KnownRights((int)right).Should().Be(right);
    }

    [Fact]
    public void Without_reuse_every_resolve_reads_every_descriptor_again()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        var acl = new ScriptedAcl((_, _) => string.Empty);
        var git = new GitExecutable(() => [exe], acl);

        git.Resolve();
        var once = acl.Asked.Count;
        git.Resolve();

        acl.Asked.Count.Should().Be(once * 2);
    }

    [Fact]
    public void Fifty_resolves_of_the_real_install_read_its_descriptors_once_with_reuse_and_fifty_times_without()
    {
        // The decision of E19.S4 gap 3: judging the install afresh cost 12 ms a resolve on this machine (50 in 601 ms, measured 2026-10-21) and a scan makes
        // hundreds, so the product keeps the judgement of a passed candidate for the life of the process. A wall-clock bound flaked on a saturated machine
        // (5.07 ms inside a full suite, under 5 ms alone; a 2x ratio failed too), so the proof is the COUNT of descriptor reads, which is what the time was.
        Assert.SkipUnless(RealGit.Available, "this machine has no Git for Windows under Program Files");
        var afreshReads = new CountingAcl();
        var reusedReads = new CountingAcl();
        var afresh = new GitExecutable(() => [RealGit.Executable], afreshReads);
        var reused = new GitExecutable(() => [RealGit.Executable], reusedReads, reuseJudgement: true);

        for (var i = 0; i < 50; i++)
        {
            afresh.Resolve().Should().BeOfType<ResolvedExecutable.Found>("the install is the supported one");
            reused.Resolve().Should().BeOfType<ResolvedExecutable.Found>();
        }

        reusedReads.Count.Should().BeGreaterThan(0).And.Be(afreshReads.Count / 50, "the reused resolver read the descriptors of the first resolve only");
    }

    private sealed class CountingAcl : IAdminOnlyPath
    {
        private readonly WindowsAdminOnlyPath _real = new();

        public int Count { get; private set; }

        public string Problem(string path, AdminOnlyRole role)
        {
            Count++;
            return _real.Problem(path, role);
        }
    }

    [Fact]
    public void A_git_in_a_user_writable_folder_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "real access-control lists are Windows's");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");

        var resolved = new GitExecutable(() => [exe], new WindowsAdminOnlyPath()).Resolve();

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("is not admin-only");
    }

    [Fact]
    public void A_path_whose_permissions_cannot_be_read_is_a_problem_never_a_pass()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "real access-control lists are Windows's");

        new WindowsAdminOnlyPath().Problem(@"C:\this\folder\does\not\exist-wsl-care", AdminOnlyRole.Ancestor).Should().NotBeEmpty();
    }
}
