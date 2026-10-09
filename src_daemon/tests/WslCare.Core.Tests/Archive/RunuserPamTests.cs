using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r risk consult 9/9.4 #2 — the runuser PAM gate the archive's children start behind (<see cref="RunuserPam"/>): a stack that
/// names <c>pam_systemd</c> would give a child a login session root does not bound. The S4 code round's finding 3 (every include
/// followed, each once) and the S4 own review round S-M2: the gate fails CLOSED — it reads the stack PAM itself would read, and a stack
/// it cannot find, a file it cannot read as root's own or an include by a path refuses. A13 refusing on it is
/// <c>ArchiveActionTests.A13_refuses_where_runusers_pam_stack_would_make_a_login_session</c>.
/// </summary>
public sealed class RunuserPamTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("runuser-pam");

    public void Dispose() => _sandbox.Dispose();

    private string Problem(IFileSystem? files = null) => RunuserPam.Problem(_sandbox.Paths, files ?? _sandbox.Files);

    /// <summary>The S4 code round, finding 3: an include of an include is followed (each file once, so a loop ends), and a file the stack
    /// pulls in that cannot be found refuses — it could name pam_systemd for all root knows.</summary>
    [Theory]
    [InlineData("nested", "/etc/pam.d/common-inner")]
    [InlineData("missing", "/etc/pam.d/common-missing could not be checked")]
    [InlineData("loop", "")]
    public void Every_file_the_stack_pulls_in_is_followed(string shape, string reason)
    {
        _sandbox.Write("/etc/pam.d/runuser", shape == "missing" ? "@include common-missing\n" : "@include common-outer\n");
        _sandbox.Write("/etc/pam.d/common-outer", "session required pam_unix.so\n@include common-inner\n");
        _sandbox.Write("/etc/pam.d/common-inner", shape == "loop" ? "session include common-outer\n" : "session optional pam_systemd.so\n");

        var problem = Problem();

        if (reason.Length == 0)
        {
            problem.Should().BeEmpty("a loop of includes naming no pam_systemd ends and refuses nothing");
        }
        else
        {
            problem.Should().Contain(reason);
        }
    }

    [Fact]
    public void A_stack_without_pam_systemd_passes()
    {
        _sandbox.Write("/etc/pam.d/runuser", ArchiveActionTests.SafeRunuserStack);

        Problem().Should().BeEmpty();
    }

    /// <summary>S-M2: Linux-PAM reads a service's stack from /etc/pam.d, else the vendor folder /usr/lib/pam.d (openSUSE ships it
    /// there), else the stack of the service <c>other</c> (Ubuntu's: <c>@include common-session</c>, which names pam_systemd). Each
    /// of these is judged — a stack root cannot find, or an include it would not follow, REFUSES.</summary>
    public static TheoryData<string, string[], string> ClosedStacks => new()
    {
        { "the vendor copy names it", ["/usr/lib/pam.d/runuser=session optional pam_systemd.so"], "/usr/lib/pam.d/runuser names pam_systemd" },
        { "no runuser stack: other's is PAM's", ["/etc/pam.d/other=@include common-session", "/etc/pam.d/common-session=session optional pam_systemd.so"], "/etc/pam.d/common-session names pam_systemd" },
        { "no stack at all", [], "no PAM stack" },
        { "an include found only in the vendor folder", ["/etc/pam.d/runuser=@include common-session", "/usr/lib/pam.d/common-session=session optional pam_systemd.so"], "/usr/lib/pam.d/common-session names pam_systemd" },
        { "an include by a path", ["/etc/pam.d/runuser=@include /etc/pam.d/common-session"], "by a path" },
        { "an include of a dot name", ["/etc/pam.d/runuser=session include ../common-session"], "by a path" },
        { "a bracketed control", ["/etc/pam.d/runuser=session [success=ok default=bad] include common-x", "/etc/pam.d/common-x=session optional pam_systemd.so"], "/etc/pam.d/common-x names pam_systemd" },
    };

    [Theory]
    [MemberData(nameof(ClosedStacks))]
    public void The_gate_judges_every_stack_PAM_would_read_and_fails_closed(string why, string[] files, string reason)
    {
        foreach (var file in files)
        {
            var at = file.IndexOf('=', StringComparison.Ordinal);
            _sandbox.Write(file[..at], file[(at + 1)..] + "\n");
        }

        Problem().Should().Contain(reason, why);
    }

    /// <summary>S-M2: a runuser stack that exists but cannot be read as root's own — a link (NixOS-WSL links it into the store), not
    /// root's, past its cap — refuses; it is no longer "not judged".</summary>
    [Fact]
    public void A_stack_that_cannot_be_read_as_roots_own_refuses()
    {
        _sandbox.Write("/etc/pam.d/runuser", ArchiveActionTests.SafeRunuserStack);

        var problem = Problem(new UnreadableAt(_sandbox.Files, _sandbox.Paths.DistroPath("/etc/pam.d/runuser")));

        problem.Should().Contain("/etc/pam.d/runuser").And.Contain("could not be checked");
    }

    [Fact]
    public void A_vendor_stack_without_pam_systemd_passes()
    {
        _sandbox.Write("/usr/lib/pam.d/runuser", "@include common-session\n");
        _sandbox.Write("/usr/lib/pam.d/common-session", "session required pam_unix.so\n");

        Problem().Should().BeEmpty();
    }

    /// <summary>The sandbox's disk, except that one file reads as a link root never follows.</summary>
    private sealed class UnreadableAt(IFileSystem inner, string path) : DelegatingFileSystem(inner)
    {
        public override FileReadResult ReadStateFile(string file, int maxBytes) =>
            string.Equals(Path.GetFullPath(file), Path.GetFullPath(path), StringComparison.Ordinal) ? new FileReadResult.Unreadable("a symbolic link, never followed") : base.ReadStateFile(file, maxBytes);
    }
}
