using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>
/// The two seams of plan §15a C1 are the ONLY files that may delete, move or start a process. A
/// red build, not a review comment.
/// </summary>
/// <remarks>The scan is written across whitespace (<c>File . Delete (</c> over three lines still
/// matches) because a line-by-line pattern once missed three of six sites in this family; and it has
/// a companion asserting it still matches a planted instance and the sanctioned sites, so a scanner
/// that silently stopped matching cannot pass forever.</remarks>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"\b(?:File|Directory)\s*\.\s*(?:Delete|Move)\s*\(|\.\s*(?:Delete|MoveTo)\s*\(|\bProcess\s*\.\s*Start\s*\(|\bnew\s+Process\s*[({]", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenCall();

    /// <summary>Every match in <paramref name="source"/>: the 1-based line and the text matched.</summary>
    internal static IReadOnlyList<(int Line, string Text)> Scan(string source) =>
        [.. ForbiddenCall().Matches(source).Select(m => (LineOf(source, m.Index), m.Value))];

    [Fact]
    public void No_file_outside_the_two_seams_deletes_moves_or_starts_a_process()
    {
        var seams = new[] { Metadata("WslCare.FileSystemSeam"), Metadata("WslCare.ProcessSeam") };
        var offenders = SourceFiles()
            .Where(file => !seams.Contains(file, StringComparer.OrdinalIgnoreCase))
            .SelectMany(file => Scan(File.ReadAllText(file)).Select(hit => $"{file}:{hit.Line}: {hit.Text}"))
            .ToList();

        offenders.Should().BeEmpty("every delete, move and process start goes through IFileSystem or ICommandRunner (plan §15a C1)");
    }

    [Fact]
    public void The_scanner_matches_a_planted_instance_formatted_across_lines()
    {
        const string planted = """
            var x = File
                .Delete (
                    path);
            Directory.Move(a, b);
            info.MoveTo(dest);
            using var p = Process
              .Start(info);
            var q = new Process { StartInfo = info };
            """;

        var hits = Scan(planted);

        hits.Select(h => h.Line).Should().Equal(1, 4, 5, 6, 8);
    }

    [Fact]
    public void The_scanner_still_finds_the_sanctioned_calls_in_each_seam()
    {
        Scan(File.ReadAllText(Metadata("WslCare.FileSystemSeam"))).Should().Contain(h => h.Text.Contains("Delete") || h.Text.Contains("Move"));
        Scan(File.ReadAllText(Metadata("WslCare.ProcessSeam"))).Should().Contain(h => h.Text.Contains("Process"));
    }

    /// <summary>What signals a process in native code: the pidfd calls, <c>kill</c> / <c>tgkill</c> / <c>sigqueue</c>, a
    /// <c>Process.Kill</c> of a pid the product did not start (E3.S2: A11 signals ONLY through <c>IProcessSignals</c>).</summary>
    [GeneratedRegex(@"""(?:pidfd_send_signal|pidfd_open|kill|tgkill|tkill|sigqueue|killpg)""|\bProcess\s*\.\s*GetProcessById\s*\([^)]*\)\s*\.\s*Kill\b", RegexOptions.CultureInvariant)]
    private static partial Regex SignalCall();

    [Fact]
    public void Only_the_signal_seam_signals_a_process()
    {
        var seam = Metadata("WslCare.SignalSeam");
        var offenders = SourceFiles()
            .Where(file => !string.Equals(file, seam, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => SignalCall().Matches(File.ReadAllText(file)).Select(m => $"{file}:{LineOf(File.ReadAllText(file), m.Index)}: {m.Value}"))
            .ToList();

        offenders.Should().BeEmpty("A11 ends a process only by pid AND start time, through IProcessSignals (E3.S2)");
    }

    [Fact]
    public void The_signal_scanner_still_finds_the_seams_own_calls_and_a_planted_one()
    {
        SignalCall().Matches(File.ReadAllText(Metadata("WslCare.SignalSeam"))).Select(m => m.Value).Should().Contain("\"pidfd_send_signal\"");
        SignalCall().IsMatch("[LibraryImport(\"libc\", EntryPoint = \"kill\")]").Should().BeTrue();
    }

    [Fact]
    public void The_scanner_ignores_words_that_merely_contain_the_names()
    {
        Scan("var deleted = DeletionVerdict.Allowed; policy.Decide(request); files.DeleteFile(p, s); process.Kill(true);").Should().BeEmpty();
    }

    [Fact]
    public void The_core_library_references_no_package()
    {
        var csproj = Path.Combine(Metadata("WslCare.SourceRoot"), "WslCare.Core", "WslCare.Core.csproj");

        XDocument.Load(csproj).Descendants("PackageReference").Should().BeEmpty("WslCare.Core holds the domain and reaches outward through nothing (nuget rule: prefer nothing)");
    }

    private static IReadOnlyList<string> SourceFiles()
    {
        var root = Metadata("WslCare.SourceRoot");
        Directory.Exists(root).Should().BeTrue($"the build stamps the product source root; {root} should exist");
        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))];
    }

    private static string Metadata(string key)
    {
        var value = typeof(ArchitectureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;
        value.Should().NotBeNullOrWhiteSpace($"the test project stamps {key} at build time");
        return value!;
    }

    private static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}
