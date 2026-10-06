using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

using static WslCare.Scenarios.ReleaseFiles;
using static WslCare.Scenarios.WorkflowShape;

namespace WslCare.Scenarios;

/// <summary>
/// The CI gate on the units a release ships (<c>.github/scripts/verify-systemd-units.sh</c>, daemon 0.1.1): systemd's own
/// parser over EVERY file of the units folder, each template through an instance name, each unit with the drop-in the
/// built binary renders. Until 0.1.1 the workflow step named three units by hand, so the detached-run template — added
/// after the list was written — was never read, and its <c>CollectMode=</c> shipped under <c>[Service]</c>, where systemd
/// 255 ignores it with a warning and exit 0 (POST_DEPLOY item 7). The flows run the real script against the real
/// <c>systemd-analyze</c> on a Linux leg; the binary that renders drop-ins is a stand-in, because the subject here is the
/// script and the parser, not the CLI (its drop-ins are <c>ShippedFilesTests</c>' and the CI step's).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SystemdUnitVerifyFlows
{
    private const string Script = "verify-systemd-units.sh";
    private const string CiStep = "Verify the systemd units (linux)";

    private static void LinuxWithSystemd()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "systemd-analyze is Linux's: covered on the Linux legs (and by hand in WSL)");
        Assert.SkipUnless(ExecutableResolver.Resolve("systemd-analyze") is ResolvedExecutable.Found, "no systemd-analyze on PATH here");
    }

    /// <summary>A unit folder of <paramref name="files"/> (name → text).</summary>
    private static string Units(TempRoot root, params (string Name, string Text)[] files)
    {
        var dir = root.Dir("units");
        foreach (var (name, text) in files)
        {
            root.File($"units/{name}", text);
        }

        return dir;
    }

    /// <summary>A stand-in for <c>wsl-care</c> whose <c>units dropin &lt;unit&gt;</c> prints <paramref name="dropIn"/>, or
    /// exits <paramref name="exit"/> without a word.</summary>
    private static string Binary(TempRoot root, string dropIn, int exit = 0)
    {
        var path = root.File("bin/wsl-care", $"#!/bin/sh\n[ \"$1 $2\" = \"units dropin\" ] || exit 2\n{(exit == 0 ? $"printf '%s\\n' '{dropIn}'" : $"exit {exit}")}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private const string Plain = "[Unit]\nDescription=fixture\n\n[Service]\nType=oneshot\nExecStart=/bin/true\n";
    private const string Template = "[Unit]\nDescription=fixture %i\nCollectMode=inactive-or-failed\n\n[Service]\nType=oneshot\nExecStart=/bin/true %i\n";

    /// <summary>The 0.1.0 template, shipped text and all, with its <c>CollectMode=</c> where 0.1.0 had it — the end of
    /// <c>[Service]</c>. Read from the shipped file, so the defect is planted into what ships rather than retyped.</summary>
    private static string TemplateOf010()
    {
        var shipped = File.ReadAllText(Path.Combine(ShippedFiles.SystemdDirectory, "wsl-care-act@.service"));
        return shipped.Replace("CollectMode=inactive-or-failed\n", string.Empty, StringComparison.Ordinal).TrimEnd('\n') + "\nCollectMode=inactive-or-failed\n";
    }

    [Fact]
    public async Task A_template_unit_in_the_folder_is_verified_and_a_key_in_the_wrong_section_fails_the_gate()
    {
        LinuxWithSystemd();
        using var root = new TempRoot("verify-units-template");
        var units = Units(root, ("wsl-care.service", Plain), ("wsl-care-act@.service", TemplateOf010()));

        var result = await ReleaseScripts.RunAsync(Script, [units], root.Path);

        result.Exit.Should().Be(1, $"systemd ignores the key with exit 0; the gate must not:\n{result.Stdout}{result.Stderr}");
        result.Stdout.Should().Contain($"{units}/wsl-care-act@.service:").And.Contain("Unknown key name 'CollectMode' in section 'Service'",
            "the template is read through an instance, and the line names the shipped file");
    }

    [Fact]
    public async Task A_drop_in_the_binary_renders_is_verified_with_its_unit()
    {
        LinuxWithSystemd();
        using var root = new TempRoot("verify-units-dropin");
        var units = Units(root, ("fixture-act@.service", Template));

        var result = await ReleaseScripts.RunAsync(Script, [units, Binary(root, "[Service]\nBogusKey=1")], root.Path);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain($"{units}/fixture-act@.service.d/50-wsl-care-config.conf:").And.Contain("Unknown key name 'BogusKey' in section 'Service'");
    }

    [Fact]
    public async Task A_binary_that_cannot_render_a_drop_in_fails_the_gate_naming_the_unit()
    {
        LinuxWithSystemd();
        using var root = new TempRoot("verify-units-dropin-fails");
        var units = Units(root, ("fixture.service", Plain));

        var result = await ReleaseScripts.RunAsync(Script, [units, Binary(root, string.Empty, exit: 78)], root.Path);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain("units dropin fixture.service failed");
    }

    [Fact]
    public async Task An_empty_folder_is_refused_rather_than_passed()
    {
        LinuxWithSystemd();
        using var root = new TempRoot("verify-units-empty");

        var result = await ReleaseScripts.RunAsync(Script, [Units(root)], root.Path);

        result.Exit.Should().Be(1, result.Stdout);
        result.Stdout.Should().Contain("no unit files in");
    }

    /// <summary>The positive beside the refusals: valid units, a template among them, and valid drop-ins pass and are
    /// named. systemd-analyze reads the whole unit search path, so a host whose own units a normal user cannot read
    /// (a root-only <c>/run/systemd/system/netplan-ovs-cleanup.service</c> in WSL Ubuntu) prints about THEM for any unit;
    /// on such a host this flow is skipped with what systemd said, and the CI step — the same script over the shipped
    /// folder, on a runner where systemd has nothing to say — is the positive.</summary>
    [Fact]
    public async Task Valid_units_with_valid_drop_ins_pass_and_are_named()
    {
        LinuxWithSystemd();
        using var root = new TempRoot("verify-units-ok");
        var calibration = await ChildProcess.RunAsync("systemd-analyze", ["verify", root.File("calibration/calibration.service", Plain)], new Dictionary<string, string?>());
        Assert.SkipUnless(calibration.Exit == 0 && (calibration.Stdout + calibration.Stderr).Trim().Length == 0,
            $"this host's systemd reports on a trivial unit already: {(calibration.Stdout + calibration.Stderr).Trim()}");
        var units = Units(root, ("fixture.service", Plain), ("fixture-act@.service", Template));

        var result = await ReleaseScripts.RunAsync(Script, [units, Binary(root, "[Service]\nNice=19\nMemoryMax=1024M")], root.Path);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().ContainSingle().Which.Should().Contain("fixture.service").And.Contain("fixture-act@.service").And.Contain("each with its drop-in");
    }

    /// <summary>The workflow runs the script over the FOLDER with the binary it just built — and names no unit itself, the
    /// list that went stale.</summary>
    [Fact]
    public void Ci_verifies_the_whole_units_folder_with_the_built_binary_s_drop_ins_and_lists_no_unit()
    {
        var job = WorkflowYaml.Load(Workflow("ci-daemon.yml"))["jobs"].Map["build-test-publish"].Map;
        var steps = Steps(job);
        var verify = steps.Single(s => s.Find("name")?.Text == CiStep);
        var run = Run(verify);

        verify["if"].Text.Should().Be("runner.os == 'Linux'");
        run.Should().Contain($"bash .github/scripts/{Script} src_daemon/systemd \"./src_daemon/src/WslCare.Cli/bin/Release/net10.0/wsl-care\"");
        run.Should().NotContain(".service").And.NotContain(".timer", "the script reads the folder; a list here is what missed the template");
        StepIndex(job, $".github/scripts/{Script}").Should().BeGreaterThan(StepIndex(job, "dotnet build wsl_care.slnx -c Release"), "the binary it renders drop-ins with is the one this job built");
    }
}
