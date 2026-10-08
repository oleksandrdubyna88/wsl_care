using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// Plan §15r D1, E9.S4 — the archive's children are the product's OWN installed binary, run as the target user: the binary is
/// accepted only when it and every folder above it are root's alone; a self-invocation template is built and allowed only with
/// that file, never a <c>wsl-care</c> of the user's bin folders; and that file is allowed only for a self-invocation template.
/// </summary>
public sealed class SelfInvocationTests
{
    private const string Installed = "/opt/wsl-care/bin/wsl-care";
    private const string UsersOwn = "/home/me/.local/bin/wsl-care";
    private const int Directory = 0x4000;
    private const int Regular = 0x8000;
    private const int Link = 0xA000;

    private static readonly CommandTemplate SelfPreview = new("archive-preview", CommandScope.User, SelfBinary.Name, [new ArgPart.Literal("archive"), new ArgPart.Literal("preview"), new ArgPart.Literal("--json")], TimeSpan.FromSeconds(30), 1024) { SelfInvocation = true };

    private static readonly CommandTemplate PlainUserTemplate = new("npm-cache-clean", CommandScope.User, "npm", [new ArgPart.Literal("cache"), new ArgPart.Literal("clean")], TimeSpan.FromSeconds(30), 1024);

    /// <summary>A user tool that happens to be named like the product — what the user could put in <c>~/.local/bin</c>.</summary>
    private static readonly CommandTemplate PlainNamedLikeTheProduct = new("plain-wsl-care", CommandScope.User, SelfBinary.Name, [new ArgPart.Literal("--version")], TimeSpan.FromSeconds(30), 1024);

    private static readonly TargetUser Me = new("me", 1000, "/home/me");

    private static Func<SelfBinaryResult> Self(string path) => () => new SelfBinaryResult.Found(path);

    private static Reading<FileStatus> Status(int type, int mode, uint owner = 0) => Reading.Of(new FileStatus(type, mode, owner, 1, 8, 32));

    /// <summary>The installed layout: every folder root's 0755, the file root's 0755.</summary>
    private static Func<string, Reading<FileStatus>> Installation(string? changed = null, Reading<FileStatus>? status = null) =>
        path => path == changed ? status! : path == Installed ? Status(Regular, 0x1ED) : Status(Directory, 0x1ED);

    [Fact]
    public void The_installed_root_owned_binary_is_accepted()
    {
        SelfBinary.Locate(Installed, Installation()).Should().Be(new SelfBinaryResult.Found(Installed));
    }

    public static TheoryData<string, string, int, int, uint, string> NotRootsAlone => new()
    {
        { "a folder on the way owned by the user", "/opt/wsl-care", Directory, 0x1ED, 1000u, "owned by uid 1000" },
        { "a folder on the way writable by the group", "/opt", Directory, 0x1FD, 0u, "writable by its group or by others" },
        { "a folder on the way that is a link", "/opt/wsl-care/bin", Link, 0x1FF, 0u, "not a directory" },
        { "the file is a link", Installed, Link, 0x1FF, 0u, "not a regular file" },
        { "the file is the user's", Installed, Regular, 0x1ED, 1000u, "not owned by root" },
        { "the file is writable by others", Installed, Regular, 0x1EF, 0u, "writable by its group or by others" },
        { "the file has no execute bit", Installed, Regular, 0x1A4, 0u, "no execute bit" },
    };

    /// <summary>D1: anybody but root who could change the binary or a folder above it could make root start their file as the user.</summary>
    [Theory]
    [MemberData(nameof(NotRootsAlone))]
    public void The_archive_child_is_the_installed_root_owned_binary_never_a_user_writable_one(string why, string changed, int type, int mode, uint owner, string reason)
    {
        SelfBinary.Locate(Installed, Installation(changed, Status(type, mode, owner))).Should().BeOfType<SelfBinaryResult.Refused>(why).Which.Reason.Should().Contain(reason);
    }

    [Theory]
    [InlineData("wsl-care", "no full path")]
    [InlineData("/opt/wsl-care/bin/../bin/wsl-care", "holds . or ..")]
    [InlineData("/opt/wsl-care/bin/dotnet", "not named wsl-care")]
    public void A_path_that_is_not_a_full_plain_path_of_the_binary_is_refused(string path, string reason)
    {
        SelfBinary.Locate(path, Installation()).Should().BeOfType<SelfBinaryResult.Refused>().Which.Reason.Should().Contain(reason);
    }

    /// <summary>A self-invocation never resolves its name in the user's bin folders, even when a <c>wsl-care</c> sits there.</summary>
    [Fact]
    public void A_self_invocation_starts_the_checked_binary_never_one_in_the_users_bin_folders()
    {
        using var temp = new TempRoot("self-invocation");
        var bin = temp.Dir("home/me/.local/bin");
        temp.File("home/me/.local/bin/" + SelfBinary.Name, "the user's own");

        var built = TargetUserCommands.Build(SelfPreview, ["archive", "preview", "--json"], Me, [new UserBinFolder("/home/me/.local/bin", bin)], Self(Installed));

        var request = built.Should().BeOfType<UserCommand.Ready>().Subject.Request;
        request.Argv.Should().Equal("runuser", "-u", "me", "--", Installed, "archive", "preview", "--json");
        request.Environment.Should().BeOfType<CommandEnvironment.Clean>();
        request.StdinClosed.Should().BeTrue("risk consult 9/9.4 #2: the child's stdin reads end-of-file");
    }

    [Fact]
    public void A_self_invocation_is_refused_when_the_binary_is_not_roots_alone()
    {
        var built = TargetUserCommands.Build(SelfPreview, ["archive", "preview", "--json"], Me, [], () => new SelfBinaryResult.Refused("the product's binary /x/wsl-care is not owned by root"));

        built.Should().BeOfType<UserCommand.Refused>().Which.Reason.Should().Contain("not owned by root");
    }

    [Fact]
    public void The_policy_allows_a_self_invocation_only_with_the_products_own_binary()
    {
        var policy = CommandPolicy.Over(new CommandCatalogue([SelfPreview, PlainUserTemplate]), Self(Installed));

        policy.Review(Wrapped(Installed, "archive", "preview", "--json")).Should().Be(CommandVerdict.Allowed);
        policy.Review(Wrapped(UsersOwn, "archive", "preview", "--json")).Should().BeOfType<CommandVerdict.Refused>()
            .Which.Reason.Should().Contain("only the product's own root-owned binary");
    }

    [Fact]
    public void The_policy_refuses_every_self_invocation_when_the_binary_is_not_roots_alone()
    {
        var policy = CommandPolicy.Over(new CommandCatalogue([SelfPreview]), () => new SelfBinaryResult.Refused("not root's"));

        policy.Review(Wrapped(Installed, "archive", "preview", "--json")).Should().BeOfType<CommandVerdict.Refused>();
    }

    /// <summary>The product's own path is allowed for a self-invocation ONLY: a plain template named like it never runs that file
    /// (it must be in the user's bin folders), and a plain tool is never started from the product's folder.</summary>
    [Fact]
    public void The_products_own_binary_is_allowed_only_for_a_self_invocation_template()
    {
        var policy = CommandPolicy.Over(new CommandCatalogue([PlainNamedLikeTheProduct, PlainUserTemplate]), Self(Installed));

        policy.Review(Wrapped(Installed, "--version")).Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("bin folders");
        policy.Review(Wrapped("/opt/wsl-care/bin/npm", "cache", "clean")).Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("bin folders");
        policy.Review(Wrapped("/home/me/.local/bin/npm", "cache", "clean")).Should().Be(CommandVerdict.Allowed);
    }

    private static CommandRequest Wrapped(string path, params string[] arguments) =>
        new(TargetUserArgv.Build("me", path, arguments), TimeSpan.FromSeconds(30))
        {
            Environment = new CommandEnvironment.Clean(new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = "/home/me" }),
        };
}
