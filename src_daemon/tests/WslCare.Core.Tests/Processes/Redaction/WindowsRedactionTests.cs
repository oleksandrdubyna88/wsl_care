using System.Runtime.InteropServices;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Redaction;

using static WslCare.Core.Tests.Processes.Redaction.RedactionCases;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>E15.S7 §2 (the Windows entry point): the Windows binary reads a command line as ONE string; <see cref="WindowsCommandLine.Split"/>
/// gives it back as arguments exactly as <c>CommandLineToArgvW</c> does, then the SAME rule judges them — so both binaries hold one
/// rule. A program that parses its raw line differently can carry a value the split does not isolate: the raw-line backstop
/// redacts the whole shown line then.</summary>
public sealed partial class WindowsRedactionTests
{
    /// <summary>Line → the arguments <c>CommandLineToArgvW</c> answers, measured on Windows 11 (2026-10-15) with the P/Invoke below.</summary>
    public static TheoryData<string, string[]> EdgeVectors => new()
    {
        { "p a\\\\\"b c\"", ["p", "a\\b c"] },
        { "p \"a\\\\\"", ["p", "a\\"] },
        { "p \"a\"\"b\"", ["p", "a\"b"] },
        { "p \"a b\"c d", ["p", "a bc", "d"] },
        { "p x \"\"", ["p", "x", ""] },
        { "p \"C:\\dir with space\\\" z", ["p", "C:\\dir with space\" z"] },
        { "p a\\\\\\\"b", ["p", "a\\\"b"] },
        { "p   lead  x\ty", ["p", "lead", "x", "y"] },
        { "p \"p q\" \"x\"\"y\"", ["p", "p q", "x\"y"] },
        { "p C:\\a\\\\b\" c d\"", ["p", "C:\\a\\\\b c d"] },
        { "\"C:\\my dir\\x.exe\" --a \\\"k\\\"", ["C:\\my dir\\x.exe", "--a", "\"k\""] },
        { "\"a\\\\\" z", ["a\\\\", "z"] },
        { "\"C:\\dir with space\\\" z", ["C:\\dir with space\\", "z"] },
        { "prog --auth:'my secret value'", ["prog", "--auth:'my", "secret", "value'"] },
    };

    [Theory]
    [MemberData(nameof(EdgeVectors))]
    public void Split_reads_the_edge_cases_of_the_command_line_parser_as_Windows_does(string line, string[] expected)
    {
        WindowsCommandLine.Split(line).Should().Equal(expected, $"the line {line}");
    }

    [Theory]
    [MemberData(nameof(EdgeVectors))]
    public void Windows_itself_agrees_with_every_edge_vector(string line, string[] expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CommandLineToArgvW is Windows'");

        ArgvOf(line).Should().Equal(expected, $"the vector's expectation is what Windows answers for {line}");
    }

    [Fact]
    public void Split_is_the_inverse_of_Of_over_a_generated_set_of_awkward_arguments()
    {
        var random = new Random(15);
        string[] pieces = ["a", "b", " ", "\t", "\"", "\\", "x"];
        string Argument() => string.Concat(Enumerable.Range(0, random.Next(0, 7)).Select(_ => pieces[random.Next(pieces.Length)]));

        for (var n = 0; n < 500; n++)
        {
            var exe = string.Concat(Enumerable.Range(0, random.Next(1, 4)).Select(_ => new[] { "a", "b", " ", "\\" }[random.Next(4)])).TrimEnd('\\') + "e";
            var arguments = Enumerable.Range(0, random.Next(0, 5)).Select(_ => Argument()).ToArray();
            var line = WindowsCommandLine.Of(exe, arguments);

            WindowsCommandLine.Split(line).Should().Equal([exe, .. arguments], $"the line {line} was built from them");
        }
    }

    [Fact]
    public void A_windows_claude_command_line_is_shown_exactly_as_the_distro_shows_the_same_argv()
    {
        const string exe = "C:\\Users\\user\\.vscode\\extensions\\vendor.extension-a-1.0.0-win32-x64\\resources\\native-binary\\claude.exe";
        var line = WindowsCommandLine.Of(exe, ["--resume=" + SessionGuid, "-p", "any text with spaces", "--max-thinking-tokens", "0"]);

        WindowsCommandLine.Shown(line, Limits).Should().Be(Shown(exe, "--resume=" + SessionGuid, "-p", "any text with spaces", "--max-thinking-tokens", "0"));
        CommandLineRedaction.Redact(WindowsCommandLine.Split(line), Limits).Should().Contain("--resume=" + SessionGuid).And.ContainInOrder("--max-thinking-tokens", "<redacted>");
    }

    [Fact]
    public void A_windows_line_with_a_secret_the_split_cannot_isolate_is_redacted_whole_by_the_raw_line_backstop()
    {
        const string line = "prog.exe --auth:'my secret value'";

        WindowsCommandLine.Shown(line, Limits).Should().Be("<redacted>", "a program that parses its raw line differently keeps its secret outside any one argument");
    }

    [Fact]
    public void A_redacted_value_whose_text_also_occurs_inside_a_shown_guid_does_not_fire_the_backstop()
    {
        // The value of --max-thinking-tokens is "0", and "0" occurs inside the GUID: the backstop compares WHOLE arguments, never a substring.
        var line = WindowsCommandLine.Of("C:\\tools\\claude.exe", ["--resume=" + SessionGuid, "--max-thinking-tokens", "0"]);

        WindowsCommandLine.Shown(line, Limits).Should().Be("C:\\tools\\claude.exe --resume=" + SessionGuid + " --max-thinking-tokens <redacted>");
    }

    [Fact]
    public void A_windows_line_whose_secret_the_split_isolates_is_not_redacted_whole_by_the_backstop()
    {
        WindowsCommandLine.Shown("tool.exe --token abc --port 5", Limits).Should().Be("tool.exe --token <redacted> --port 5");
        WindowsCommandLine.Shown("tool.exe --auth:\"my secret value\" --port 5", Limits).Should().Be("tool.exe <redacted> --port 5");
    }

    private static string[] ArgvOf(string line)
    {
        var pointer = CommandLineToArgvW(line, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
