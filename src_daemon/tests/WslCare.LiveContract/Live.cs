using System.Text;
using System.Text.Json;

using WslCare.Core.Docker;
using WslCare.Core.Processes;

namespace WslCare.LiveContract;

/// <summary>
/// How the live contract reaches the real tools, and what it does when one is not there.
/// </summary>
/// <remarks>
/// <para><b>Through the product's own launcher.</b> Every command is a product <see cref="ToolCommand"/>
/// started by <see cref="ProcessCommandRunner"/> — argv list, the 30 s ceiling of plan §15b #2, the whole
/// process tree killed on timeout — so what is checked is the product's path to the tool, not a second one.</para>
/// <para><b>Absent is a skip, with its reason; at release a failure.</b> No <c>docker</c> on PATH, no daemon,
/// no <c>systemctl</c>, a host not booted with systemd: <see cref="Unavailable"/> skips the test naming why.
/// <see cref="RequireVariable"/>=1 — the release checklist — makes the same call a failure, so a release
/// cannot be cut on a run that checked nothing. Under CI (<c>CI=true</c>) the check skips unless required:
/// a runner's Docker and systemd are not the machine the contract is about.</para>
/// <para><b>Capture.</b> With <see cref="CaptureVariable"/> naming a directory, each command's stdout is
/// written there as <c>{name}.out</c>, with <c>commands.jsonl</c> (argv, exit, file) and the capture instant
/// — the raw material of the fixtures under <c>src_daemon/tests/fixtures/docker/</c>.</para>
/// </remarks>
internal static class Live
{
    public const string RequireVariable = "WSL_CARE_REQUIRE_LIVE";
    public const string CaptureVariable = "WSL_CARE_LIVE_CAPTURE";

    /// <summary>Plan §15b #2: every real command under a 30 s ceiling.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private static readonly ICommandRunner Runner = new ProcessCommandRunner(new AllowAllCommandPolicy());
    private static readonly object CaptureGate = new();
    private static readonly HashSet<string> CapturedArgv = new(StringComparer.Ordinal);
    private static readonly List<string> CapturedNames = [];

    public static bool Required => Environment.GetEnvironmentVariable(RequireVariable) is "1" or "true";

    /// <summary>Runs <paramref name="command"/> for real (30 s ceiling), skipping first under CI.</summary>
    public static async Task<CommandOutcome> RunAsync(ToolCommand command)
    {
        if (!Required && Environment.GetEnvironmentVariable("CI") is "true")
        {
            Unavailable("CI does not run the live contract: it is about the owner's Docker and systemd, and runs on that machine before every release (plan §15b #2)");
        }

        var outcome = await Runner.RunAsync((command with { Ceiling = Ceiling }).ToRequest(), TestContext.Current.CancellationToken);
        Capture(command, outcome);
        return outcome;
    }

    /// <summary>A docker command whose answer the contract needs: a daemon that is not there skips (or fails
    /// when required); any other failure — a timeout, a cut answer, an error Docker printed three times running —
    /// FAILS, because the product would meet it too.</summary>
    /// <remarks>An error Docker printed is retried twice, 5 s apart: measured 2026-10-02, while another session
    /// was creating containers, <c>system df</c> and <c>ps -a</c> failed for seconds with "snapshotter.Usage failed
    /// … lstat …: no such file or directory" — Docker's own transient, which <c>preview</c> reports as
    /// <c>commandFailed</c> and the next look does not meet. The contract is about the parsers, not that race.</remarks>
    public static async Task<string> DockerAsync(ToolCommand command)
    {
        var answer = DockerCli.Classify(command, await RunAsync(command));
        for (var retry = 0; retry < 2 && answer is DockerAnswer.Failed { Problem.Kind: DockerFailure.CommandFailed }; retry++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            answer = DockerCli.Classify(command, await RunAsync(command));
        }

        if (answer is DockerAnswer.Failed { Problem: var problem })
        {
            if (problem.Kind is DockerFailure.NotInstalled or DockerFailure.DaemonStopped or DockerFailure.SocketRefused)
            {
                Unavailable($"Docker is not reachable here ({problem.Kind}): {problem.Reason}");
            }

            Assert.Fail($"{command.Shown}: {problem.Kind}: {problem.Reason}");
        }

        return ((DockerAnswer.Answered)answer).Stdout;
    }

    /// <summary>
    /// Two answers taken while Docker held still: <paramref name="probe"/>, then <paramref name="subject"/>, then
    /// <paramref name="probe"/> again — accepted only when the two probes agree, at most three times. Comparing
    /// two commands taken at different moments is otherwise a race against whatever else uses this Docker
    /// (measured 2026-10-02: a parallel session building an image moved the images figure between the two
    /// calls). "Still" is judged on <paramref name="fingerprint"/> — the figures the test compares — because a
    /// running database grows its volume between any two calls and the whole text never holds still.
    /// calls). A Docker that never holds still is unavailable — a skip locally, a failure at release.
    /// </summary>
    public static async Task<(string Probe, string Subject)> QuietAsync(ToolCommand probe, ToolCommand subject, Func<string, string> fingerprint)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await DockerAsync(probe);
            var answer = await DockerAsync(subject);
            if (fingerprint(before) == fingerprint(await DockerAsync(probe)))
            {
                return (before, answer);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Unavailable($"Docker changed during each of three attempts to read {probe.Display} and {subject.Display} together; run the contract while Docker is quiet");
        return (string.Empty, string.Empty);
    }

    /// <summary>A systemd tool's answer; no such tool, or a host not booted with systemd, is unavailable.</summary>
    public static async Task<string> SystemdAsync(ToolCommand command)
    {
        var outcome = await RunAsync(command);
        switch (outcome)
        {
            case CommandOutcome.FailedToStart failed:
                Unavailable($"{command.Executable} is not on PATH here — not a systemd host ({failed.Reason})");
                break;
            case CommandOutcome.Exited { ExitCode: not 0 } exited when exited.Stderr.Text.Contains("not been booted with systemd", StringComparison.Ordinal):
                Unavailable($"this host was not booted with systemd: {exited.Stderr.Text.Trim()}");
                break;
            case CommandOutcome.Exited { ExitCode: 0 } ok:
                return ok.Stdout.Text;
        }

        Assert.Fail($"{command.Display} did not answer: {outcome}");
        return string.Empty;
    }

    /// <summary>A skip with its reason — or, when <see cref="RequireVariable"/> is set, a failure.</summary>
    public static void Unavailable(string reason)
    {
        if (Required)
        {
            Assert.Fail($"{RequireVariable}=1 (release): a skip is a failure. {reason}");
        }

        Assert.Skip(reason);
    }

    private static void Capture(ToolCommand command, CommandOutcome outcome)
    {
        var directory = Environment.GetEnvironmentVariable(CaptureVariable);
        if (string.IsNullOrWhiteSpace(directory) || outcome is not CommandOutcome.Exited exited)
        {
            return;
        }

        lock (CaptureGate)
        {
            // One file per distinct argv, the FIRST answer kept: several tests run `system df -v`, and a
            // fixture is one answer, not five appended. A second argv under one name (another unit, another
            // inspect batch) gets `-2`, `-3`.
            var argv = string.Join('\u0001', command.Argv);
            if (CapturedArgv.Contains(argv))
            {
                return;
            }

            CapturedArgv.Add(argv);
            CapturedNames.Add(command.Name);
            var sameName = CapturedNames.Count(n => n == command.Name);
            var file = sameName == 1 ? $"{command.Name}.out" : $"{command.Name}-{sameName}.out";
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, file), exited.Stdout.Text, new UTF8Encoding(false));
            var instant = Path.Combine(directory, "captured-at.txt");
            if (!File.Exists(instant))
            {
                File.WriteAllText(instant, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\n");
            }

            File.AppendAllText(Path.Combine(directory, "commands.jsonl"), Line(command, exited.ExitCode, file) + "\n");
        }
    }

    private static string Line(ToolCommand command, int exit, string file)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("name", command.Name);
            json.WriteStartArray("argv");
            foreach (var arg in command.Argv)
            {
                json.WriteStringValue(arg);
            }

            json.WriteEndArray();
            json.WriteNumber("exit", exit);
            json.WriteString("file", file);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
