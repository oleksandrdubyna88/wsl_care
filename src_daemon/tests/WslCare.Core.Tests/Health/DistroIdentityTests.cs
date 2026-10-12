using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S2 design §1 (measured 2026-10-11): a systemd unit has no <c>WSL_DISTRO_NAME</c>; a process started through <c>wsl.exe</c> has.
/// So a root run that carries it records it in <c>{state}/distro-name.json</c> — atomically, only when it changed — and the full run
/// takes the environment first, then the file; with neither, the name is not known and the reason says how it becomes known.
/// </summary>
public sealed class DistroIdentityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("distro-identity");

    public void Dispose() => _sandbox.Dispose();

    private Reading<string> Recorded() => DistroIdentity.Read(_sandbox.Paths, _sandbox.Files);

    private string Record(string environment, DateTimeOffset at) => DistroIdentity.Record(_sandbox.Paths, _sandbox.Files, environment, at);

    private static string ValueOf(Reading<string> reading)
    {
        reading.Should().BeOfType<Reading<string>.Available>(reading.ReasonOrEmpty);
        return ((Reading<string>.Available)reading).Value;
    }

    [Fact]
    public void The_environment_names_the_distro_first()
    {
        ValueOf(DistroIdentity.Resolve("Ubuntu", Reading.Of("Ubuntu-26.04"))).Should().Be("Ubuntu");
    }

    [Fact]
    public void Without_the_environment_the_recorded_file_names_it()
    {
        Record("Ubuntu", Now).Should().BeEmpty();

        ValueOf(DistroIdentity.Resolve(string.Empty, Recorded())).Should().Be("Ubuntu");
        File.Exists(DistroIdentity.File(_sandbox.Paths)).Should().BeTrue();
    }

    [Fact]
    public void With_neither_the_name_is_not_known_and_the_reason_says_how_it_becomes_known()
    {
        var name = DistroIdentity.Resolve(string.Empty, Recorded());

        name.IsAvailable.Should().BeFalse();
        name.ReasonOrEmpty.Should().Be(DistroIdentity.NotKnownYet);
        DistroIdentity.NotKnownYet.Should().Contain("wsl.exe").And.Contain("Run full check now");
    }

    [Fact]
    public void The_file_is_written_only_when_the_name_changed()
    {
        Record("Ubuntu", Now).Should().BeEmpty();
        var first = File.ReadAllText(DistroIdentity.File(_sandbox.Paths));

        Record("Ubuntu", Now.AddHours(5)).Should().BeEmpty();
        File.ReadAllText(DistroIdentity.File(_sandbox.Paths)).Should().Be(first, "the same name rewrites nothing, not even its instant");

        Record("Ubuntu-26.04", Now.AddHours(6)).Should().BeEmpty();
        ValueOf(Recorded()).Should().Be("Ubuntu-26.04");
    }

    [Fact]
    public void No_environment_records_nothing()
    {
        Record(string.Empty, Now).Should().BeEmpty();

        File.Exists(DistroIdentity.File(_sandbox.Paths)).Should().BeFalse();
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("Ubuntu; rm")]
    [InlineData("-Ubuntu")]
    [InlineData("Ubuntu\n")]
    public void A_name_outside_wsls_own_rule_is_neither_recorded_nor_believed(string name)
    {
        Record(name, Now).Should().Contain("is not a distribution name");
        File.Exists(DistroIdentity.File(_sandbox.Paths)).Should().BeFalse();
        DistroIdentity.Resolve(name, Reading.Missing<string>(DistroIdentity.NotKnownYet)).IsAvailable.Should().BeFalse();
    }

    /// <summary>E15.S2b plan round: WSL's rule allows a first character and up to 63 more. The longest name — one that
    /// <c>install.sh --distro-name</c> must hand on — is recorded and believed; one character more is no name
    /// (<c>InstallDistroNameFlows</c> runs the same two lengths through install.sh).</summary>
    [Fact]
    public void The_longest_name_wsls_rule_allows_64_characters_is_recorded_and_believed_and_65_is_not()
    {
        var longest = "U" + new string('b', 63);

        Record(longest, Now).Should().BeEmpty("64 characters is a name");
        ValueOf(Recorded()).Should().Be(longest);

        Record(longest + "b", Now).Should().Contain("is not a distribution name", "65 characters is not");
        ValueOf(Recorded()).Should().Be(longest, "a refused name leaves the recorded one");
    }

    [Fact]
    public void A_recorded_file_with_a_bad_name_is_not_believed()
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DistroIdentity.File(_sandbox.Paths), """{"schemaVersion":1,"name":"a b","recordedAt":"2026-10-11T12:00:00+00:00"}""");

        Recorded().IsAvailable.Should().BeFalse();
        Recorded().ReasonOrEmpty.Should().Contain(DistroIdentity.FileName);
    }
}
