using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>
/// The enumeration behind the 2026-10-08 fix, left as a control (<see cref="ShellRefusalScan"/>): no shipped shell script calls
/// a refusing function inside <c>$( … )</c>, where its message would be lost. A prohibition scan passes forever once its pattern
/// stops matching, so it has two companions: the scan still recognises the refusing functions of the real scripts, and it
/// finds the exact shape that shipped. Text only — runs on every OS.
/// </summary>
public sealed class ShellRefusalScanTests
{
    /// <summary>install.sh and every script the workflows run — the set ci-workflows.yml lints with shellcheck.</summary>
    private static IEnumerable<string> ShippedScripts() =>
    [
        Path.Combine(ReleaseFiles.Root, "install.sh"),
        .. Directory.GetFiles(Path.Combine(ReleaseFiles.Root, ".github", "scripts"), "*.sh"),
        .. Directory.GetFiles(Path.Combine(ReleaseFiles.Root, ".github", "scripts", "lib"), "*.sh"),
    ];

    private static string[] Lines(string path) => File.ReadAllLines(path);

    [Fact]
    public void No_shipped_shell_script_calls_a_refusing_function_inside_a_command_substitution()
    {
        var findings = ShippedScripts()
            .SelectMany(path => ShellRefusalScan.Findings(Lines(path)).Select(f => $"{Path.GetRelativePath(ReleaseFiles.Root, path)}:{f.Line} {f.Function}"))
            .ToList();

        findings.Should().BeEmpty($"a refusing function inside $( … ) loses its message and leaves only the exit status — call it as a plain command and assign through a named variable (release-extension-guard.sh's manifest_field); every site: {string.Join(", ", findings)}");
    }

    [Fact]
    public void The_scan_still_recognises_the_refusing_functions_of_the_real_scripts()
    {
        var guard = ShellRefusalScan.RefusingFunctions(Lines(ReleaseFiles.Script("release-extension-guard.sh")));
        var install = ShellRefusalScan.RefusingFunctions(Lines(Path.Combine(ReleaseFiles.Root, "install.sh")));
        var installFunctions = ShellRefusalScan.Functions(Lines(Path.Combine(ReleaseFiles.Root, "install.sh")));

        guard.Should().Contain(["refuse", "manifest_field", "require_published"], "direct and one-call-removed refusals are both found");
        install.Should().Contain("fail");
        installFunctions.Should().ContainKey("printable", "a one-line function is read as a function");
        install.Should().NotContain("wsl_conf_default", "an awk program's bare exit is not a refusal");
        ShippedScripts().Should().HaveCountGreaterThan(5, "the script folders were found");
    }

    /// <summary>The shape that shipped until 2026-10-08, and the shape that replaced it.</summary>
    [Fact]
    public void The_scan_finds_a_refusal_inside_a_command_substitution_and_not_the_plain_command_form()
    {
        string[] shipped =
        [
            "refuse() {",
            "  echo \"::error::guard: $*\"",
            "  exit 1",
            "}",
            "manifest_field() {",
            "  [ -n \"$found\" ] || refuse \"no single top-level $1\"",
            "  printf '%s\\n' \"$found\"",
            "}",
            "# recorded=\"$(manifest_field version)\" in a comment is not a call",
            "recorded=\"$(manifest_field version)\"",
            "publisher=\"$( manifest_field publisher)\"",
        ];
        string[] fixedForm = [.. shipped.Take(8), "manifest_field version recorded", "manifest_field publisher publisher"];

        ShellRefusalScan.Findings(shipped).Should().Equal(new ShellRefusalScan.Finding(10, "manifest_field"), new ShellRefusalScan.Finding(11, "manifest_field"));
        ShellRefusalScan.Findings(fixedForm).Should().BeEmpty();
    }
}
