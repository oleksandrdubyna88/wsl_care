using System.Text;
using System.Text.Json;

using WslCare.Core;
using WslCare.Core.Config;
using WslCare.Core.Doctor;
using WslCare.Core.Json;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>doctor [--json]</c> (plan §6): the installation's health — read-only. The answer is exit 0 whatever it finds:
/// <c>healthy</c> in the JSON is the verdict (the installer reads it, plan §15a #3), and a non-zero code stays the
/// sign of a defect or a refused argument.
/// </summary>
internal static class DoctorCommand
{
    public static int Run(Request.Doctor request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, CancellationToken cancellationToken)
    {
        var version = ProductVersion.Of(typeof(DoctorCommand).Assembly).Text;
        var report = new DoctorRun(host.Paths, host.Files, host.Commands, host.Clock).RunAsync(loaded, version, cancellationToken).GetAwaiter().GetResult();
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.DoctorReport) : Render(report));
    }

    private static string Render(DoctorReport report)
    {
        var text = new StringBuilder().AppendLine($"wsl-care doctor ({report.Side}): {(report.Healthy ? "healthy" : "NOT healthy")}");
        foreach (var check in report.Checks)
        {
            // E7.S0 review S4: a detail can quote a user layer (an unknown key, a refused value): no terminal control reaches the
            // admin's terminal — an OSC 52 sequence would write their clipboard.
            text.AppendLine(CommandLine.Printable($"  {check.State,-10} {check.Id}: {check.Detail}"));
        }

        // Retro gate over PR #17: a version is what a tool ANSWERED (docker's daemon, systemd) — Printable like the checks above.
        text.Append(CommandLine.Printable("versions: " + string.Join(", ", report.Versions.Select(v => v.Available ? $"{v.Component} {v.Version}" : $"{v.Component} unknown"))));
        return text.ToString();
    }
}
