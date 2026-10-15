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

    private static string DummyProgram(ThrowAwayFolder folder, string relative, string header = "MZ")
    {
        return folder.Write(relative, header + "\0\0 this is not a program, only its header");
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

    [Fact]
    public void The_file_its_folder_mingw64_bin_and_every_ancestor_up_to_the_volume_root_are_all_asked_with_their_role()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git.exe is a Windows program: run on the Windows legs");
        using var folder = new ThrowAwayFolder("gitexe");
        var exe = DummyProgram(folder, "Git/cmd/git.exe");
        Directory.CreateDirectory(folder.Under("Git/mingw64/bin"));
        var acl = new ScriptedAcl((_, _) => string.Empty);

        new GitExecutable(() => [exe], acl).Resolve().Should().BeOfType<ResolvedExecutable.Found>();

        var root = Path.GetPathRoot(exe)!;
        acl.Asked.Should().Contain((exe, AdminOnlyRole.ProgramFile));
        acl.Asked.Should().Contain((folder.Under("Git/cmd"), AdminOnlyRole.ProgramFolder));
        acl.Asked.Should().Contain((folder.Under("Git/mingw64/bin"), AdminOnlyRole.ProgramFolder));
        acl.Asked.Should().Contain((folder.Under("Git/mingw64"), AdminOnlyRole.Ancestor));
        acl.Asked.Should().Contain((folder.Under("Git"), AdminOnlyRole.Ancestor));
        acl.Asked.Should().Contain((folder.Path, AdminOnlyRole.Ancestor));
        acl.Asked.Should().Contain((root, AdminOnlyRole.Ancestor), "the chain runs to the volume root");
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
