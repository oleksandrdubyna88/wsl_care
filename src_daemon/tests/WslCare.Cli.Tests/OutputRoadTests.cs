using System.Reflection;
using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Cli.Tests;

/// <summary>
/// <see cref="Output"/> is the one road from a verb to a stream — every answer, refusal, note and
/// internal error — because that is where <see cref="CommandLine.Printable"/> is applied; the console
/// sink is the one road for log lines, and sanitises the values it renders. A write anywhere else in
/// the CLI could put a raw control character on stderr, so it is a red build.
/// </summary>
/// <remarks>The scan matches across whitespace (<c>stderr . WriteLine (</c> over lines still counts),
/// and has the two companions the family testing rule asks of a scan: a planted instance it must
/// find, and the sanctioned writes it must still find in the two allowed files, so a pattern that
/// silently stopped matching cannot pass forever. It does not prove a write reaches stderr through a
/// call it cannot see (a <c>TextWriter</c> handed to a library); none exists in the CLI today.</remarks>
public sealed partial class OutputRoadTests
{
    /// <summary>The two files allowed to write to a stream.</summary>
    private static readonly string[] Sanctioned = ["Output.cs", "AnsiConsoleSink.cs"];

    [GeneratedRegex(@"\.\s*Write(?:Line)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex StreamWrite();

    /// <summary>The 1-based line of every stream write in <paramref name="source"/>.</summary>
    internal static IReadOnlyList<int> Scan(string source) =>
        [.. StreamWrite().Matches(source).Select(m => source.AsSpan(0, m.Index).Count('\n') + 1)];

    [Fact]
    public void Every_stream_write_of_the_cli_goes_through_Output_or_the_console_sink()
    {
        var offenders = SourceFiles()
            .Where(file => !Sanctioned.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .SelectMany(file => Scan(File.ReadAllText(file)).Select(line => $"{file}:{line}"))
            .ToList();

        offenders.Should().BeEmpty("Output applies CommandLine.Printable on the way to stderr; a direct write skips it");
    }

    [Fact]
    public void The_scanner_matches_a_planted_instance_formatted_across_lines()
    {
        const string planted = """
            stderr
                .WriteLine(
                    message);
            Console.Error.Write(text);
            console . WriteLine ( x );
            """;

        // A match is reported where it starts — at the dot, so the first one is on line 2.
        Scan(planted).Should().Equal(2, 4, 5);
    }

    [Fact]
    public void The_scanner_still_finds_the_sanctioned_writes_in_both_allowed_files()
    {
        foreach (var allowed in Sanctioned)
        {
            var file = SourceFiles().Single(f => Path.GetFileName(f) == allowed);
            Scan(File.ReadAllText(file)).Should().NotBeEmpty($"{allowed} is where the CLI's writes are; a scan that finds none there finds nothing anywhere");
        }
    }

    [Fact]
    public void The_scanner_ignores_calls_that_merely_contain_the_word()
    {
        Scan("text.AppendLine(x); value.WriteTo(writer); writer.WriteStartObject(); _formatter.Format(e, w);").Should().BeEmpty();
    }

    private static IReadOnlyList<string> SourceFiles()
    {
        var root = typeof(OutputRoadTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.CliSourceRoot").Value;
        Directory.Exists(root).Should().BeTrue($"the build stamps the CLI source root; {root} should exist");
        var separator = Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))];
    }
}
