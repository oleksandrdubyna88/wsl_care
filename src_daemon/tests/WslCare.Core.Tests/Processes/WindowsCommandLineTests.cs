using System.Runtime.InteropServices;

using FluentAssertions;

using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Processes;

/// <summary>WR-D3: the worker's command line is quoted so the C runtime's parser gives back EXACTLY its arguments — held as text on
/// every leg, and read back through Windows' own <c>CommandLineToArgvW</c> on the Windows legs.</summary>
public sealed partial class WindowsCommandLineTests
{
    public static TheoryData<string[], string> Lines => new()
    {
        { ["C:\\a\\wsl-care.exe", "watch", "--timer", "--json"], "C:\\a\\wsl-care.exe watch --timer --json" },
        { ["C:\\Program Files\\x\\w.exe", "a b", ""], "\"C:\\Program Files\\x\\w.exe\" \"a b\" \"\"" },
        { ["w.exe", "say \"hi\"", "C:\\dir with space\\"], "w.exe \"say \\\"hi\\\"\" \"C:\\dir with space\\\\\"" },
        { ["w.exe", "a\\\\\"b", "c\\d"], "w.exe \"a\\\\\\\\\\\"b\" c\\d" },
    };

    [Theory]
    [MemberData(nameof(Lines))]
    public void A_command_line_quotes_spaces_quotes_and_the_backslashes_before_them(string[] argv, string expected)
    {
        WindowsCommandLine.Of(argv[0], argv[1..]).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Lines))]
    public void Windows_reads_the_command_line_back_as_exactly_its_arguments(string[] argv, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CommandLineToArgvW is Windows'");
        var line = WindowsCommandLine.Of(argv[0], argv[1..]);

        ArgvOf(line).Should().Equal(argv, $"the line {expected} must parse back");
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
