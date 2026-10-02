using FluentAssertions;

using WslCare.Core.Docker;
using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Docker;

/// <summary>
/// Every way Docker can fail to answer becomes a NAMED failure with a reason (plan §15b #7). The stderr texts
/// are Docker's own, measured 2026-10-02 against Docker 29.6.1 on Linux and Windows.
/// </summary>
public sealed class DockerCliTests
{
    private static readonly ToolCommand Df = DockerCommands.SystemDf;

    public static TheoryData<string, DockerFailure> RealStderr => new()
    {
        { "failed to connect to the docker API at unix:///tmp/nope.sock; check if the path is correct and if the daemon is running: dial unix /tmp/nope.sock: connect: no such file or directory", DockerFailure.DaemonStopped },
        { "failed to connect to the docker API at npipe:////./pipe/nope; check if the path is correct and if the daemon is running: open //./pipe/nope: The system cannot find the file specified.", DockerFailure.DaemonStopped },
        { "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", DockerFailure.DaemonStopped },
        { "permission denied while trying to connect to the docker API at unix:///tmp/wslcare-nosock", DockerFailure.SocketRefused },
        { "Get \"http://127.0.0.1:1/v1.55/version\": dial tcp 127.0.0.1:1: connect: connection refused", DockerFailure.SocketRefused },
        { "Get \"http://127.0.0.1:1/v1.55/version\": dial tcp 127.0.0.1:1: i/o timeout", DockerFailure.TimedOut },
        { "Error response from daemon: something new", DockerFailure.CommandFailed },
    };

    [Theory]
    [MemberData(nameof(RealStderr))]
    public void A_non_zero_exit_is_classified_by_what_docker_printed_and_the_reason_quotes_it(string stderr, DockerFailure kind)
    {
        var answer = DockerCli.Classify(Df, new CommandOutcome.Exited(1, CapturedText.Empty, new CapturedText(stderr + "\n", false), TimeSpan.Zero));

        var problem = answer.Should().BeOfType<DockerAnswer.Failed>().Subject.Problem;
        problem.Kind.Should().Be(kind);
        problem.Reason.Should().Contain(stderr[..40]);
    }

    [Fact]
    public void A_missing_executable_is_not_installed()
    {
        var answer = DockerCli.Classify(Df, new CommandOutcome.FailedToStart("docker: The system cannot find the file specified."));

        answer.Should().BeOfType<DockerAnswer.Failed>().Which.Problem.Kind.Should().Be(DockerFailure.NotInstalled);
    }

    [Fact]
    public void A_command_past_its_ceiling_is_timed_out_and_says_its_tree_was_killed()
    {
        var problem = ((DockerAnswer.Failed)DockerCli.Classify(Df, new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, TimeSpan.FromSeconds(120)))).Problem;

        problem.Kind.Should().Be(DockerFailure.TimedOut);
        problem.Reason.Should().Be("docker system df --format {{json .}} did not answer within 120 s; its process tree was killed");
    }

    [Fact]
    public void A_policy_refusal_and_a_cut_answer_are_named_and_never_read()
    {
        DockerCli.Classify(Df, new CommandOutcome.Refused("no")).Should().BeOfType<DockerAnswer.Failed>().Which.Problem.Kind.Should().Be(DockerFailure.Refused);
        DockerCli.Classify(Df, new CommandOutcome.Exited(0, new CapturedText("{\"Type\":", true), CapturedText.Empty, TimeSpan.Zero))
            .Should().BeOfType<DockerAnswer.Failed>().Which.Problem.Kind.Should().Be(DockerFailure.Unparseable);
    }

    [Fact]
    public void An_exit_zero_answer_is_the_stdout()
    {
        DockerCli.Classify(Df, new CommandOutcome.Exited(0, new CapturedText("{}", false), CapturedText.Empty, TimeSpan.Zero))
            .Should().Be(new DockerAnswer.Answered("{}"));
    }

    [Fact]
    public void An_inspect_that_names_only_containers_removed_since_the_listing_is_read_for_the_others()
    {
        // Docker 29.6.1, 2026-10-02: a container removed between system df -v and the inspect.
        const string vanished = "Error response from daemon: No such container: bd82df97b7c66d9e3f6481732882693e651687b8fea9a156cc4283d5e8353cfd\n";
        const string others = "{\"id\":\"ff8c\"}\n";
        var inspect = DockerCommands.ContainerInspect([new string('a', 64), new string('b', 64)]);

        var answer = DockerCli.Classify(inspect, new CommandOutcome.Exited(1, new CapturedText(others, false), new CapturedText(vanished, false), TimeSpan.Zero));
        var elsewhere = DockerCli.Classify(Df, new CommandOutcome.Exited(1, new CapturedText(others, false), new CapturedText(vanished, false), TimeSpan.Zero));
        var mixed = DockerCli.Classify(inspect, new CommandOutcome.Exited(1, new CapturedText(others, false), new CapturedText(vanished + "permission denied\n", false), TimeSpan.Zero));

        answer.Should().Be(new DockerAnswer.Answered(others));
        elsewhere.Should().BeOfType<DockerAnswer.Failed>();
        mixed.Should().BeOfType<DockerAnswer.Failed>().Which.Problem.Kind.Should().Be(DockerFailure.SocketRefused);
    }

    [Fact]
    public void A_version_answer_with_a_null_server_is_a_stopped_daemon_whatever_the_exit_code()
    {
        const string clientOnly = "{\"Client\":{\"Version\":\"29.6.1\"},\"Server\":null}";

        var reachability = DockerReachability.From(new DockerAnswer.Answered(clientOnly));

        reachability.Should().BeOfType<DockerReachability.Unreachable>().Which.Problem.Kind.Should().Be(DockerFailure.DaemonStopped);
    }
}
