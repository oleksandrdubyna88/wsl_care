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
        var scripts = ShippedScripts().Select(path => (Path: path, Lines: Lines(path))).ToList();
        var refusing = ShellRefusalScan.RefusingFunctions(scripts.Select(s => (IReadOnlyList<string>)s.Lines));
        var findings = scripts
            .SelectMany(s => ShellRefusalScan.Findings(s.Lines, refusing).Select(f => $"{Path.GetRelativePath(ReleaseFiles.Root, s.Path)}:{f.Line} {f.Function}"))
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

    /// <summary>The same call reformatted over lines — the name on the line after <c>$(</c>, or after a continuation — is the
    /// same defect, reported at the line of its <c>$(</c>.</summary>
    [Fact]
    public void A_command_substitution_split_across_lines_is_still_found_at_its_opening_line()
    {
        string[] script =
        [
            "manifest_field() {",
            "  refuse \"no single top-level $1\"",
            "}",
            "recorded=\"$(",
            "  manifest_field version",
            ")\"",
            "publisher=\"$( \\",
            "manifest_field publisher)\"",
        ];

        ShellRefusalScan.Findings(script).Should().Equal(new ShellRefusalScan.Finding(4, "manifest_field"), new ShellRefusalScan.Finding(7, "manifest_field"));
    }

    /// <summary>Scripts source each other's libraries: a refusing function defined in one file and called inside <c>$( … )</c>
    /// in another is found once the refusing set is taken over both — and not from the calling file alone.</summary>
    [Fact]
    public void A_refusing_function_from_a_sourced_library_is_found_in_the_script_that_calls_it()
    {
        string[] library = ["lib_value() {", "  [ -n \"$1\" ] || fail \"no value\"", "  printf '%s' \"$1\"", "}"];
        string[] caller = [". \"$here/lib/values.sh\"", "v=\"$(lib_value \"$x\")\""];

        ShellRefusalScan.Findings(caller, ShellRefusalScan.RefusingFunctions([library, caller])).Should().Equal(new ShellRefusalScan.Finding(2, "lib_value"));
        ShellRefusalScan.Findings(caller).Should().BeEmpty("the calling file alone does not define it — which is why the prohibition takes the set over every script");
    }
}
