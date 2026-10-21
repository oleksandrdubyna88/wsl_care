using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>
/// WR-T2 own review, item 8 (PLAN_windows_runner.md §14): the recursive <c>IFileSystem.DeleteDirectory</c> is the one call that removes a
/// whole tree through the deletion policy. The Windows <c>%TEMP%</c> cleanup (A23) must never reach it - a folder there leaves only through
/// <c>DeleteEmptyDirectory</c>, after its files went one by one - and every OTHER caller of it is a closed, named list, each deleting under
/// the daemon's own state or log directory. A new caller anywhere goes red and has to be justified and recorded.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>A use of the recursive delete as a member (<c>files.DeleteDirectory(</c>, across lines, or as a METHOD GROUP: <c>Remove(files.DeleteDirectory)</c>); not <c>DeleteEmptyDirectory(</c>, not a declaration.</summary>
    [GeneratedRegex(@"\.\s*DeleteDirectory(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex RecursiveDeleteMemberCall();

    /// <summary>Any spelling of the name as a call, a method group or a declaration, but never as the tail of another name (<c>DeleteEmptyDirectory(</c>).</summary>
    [GeneratedRegex(@"(?<!\w)DeleteDirectory(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex RecursiveDeleteAnyCall();

    /// <summary>The src files that call the recursive delete, and the root each deletes under (never <c>%TEMP%</c>): the closed list.</summary>
    internal static readonly Dictionary<string, string> RecursiveDeleters = new(StringComparer.Ordinal)
    {
        ["WslCare.Cli/Logging/LogRetention.cs"] = "the log root (the run-log day folders past logging.retentionDays)",
        ["WslCare.Core/Actions/UserCaches/CacheFolders.cs"] = "a user-cache action's own target root (Linux actions; never on the Windows binary's A23)",
        ["WslCare.Core/Records/RunRetention.cs"] = "the run-detail store under the state directory (aged day folders)",
        ["WslCare.Core/Scheduler/Staging.Sweep.cs"] = "the staging root under the state directory (aged staged copies)",
    };

    [Fact]
    public void No_windows_temp_cleanup_file_calls_the_recursive_delete()
    {
        var folder = Path.Combine(Metadata("WslCare.SourceRoot"), "WslCare.Core", "Actions", "WindowsTemp");
        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).ToList();
        files.Should().NotBeEmpty("the scan must read the A23 files, or it proves nothing");

        var offenders = files
            .SelectMany(file => RecursiveDeleteAnyCall().Matches(CodeOnly(File.ReadAllText(file))).Select(m => $"{file}:{LineOf(CodeOnly(File.ReadAllText(file)), m.Index)}: {m.Value}"))
            .ToList();

        offenders.Should().BeEmpty("A23 removes a folder only through DeleteEmptyDirectory, after its entries went one by one: "
            + "a recursive DeleteDirectory under %TEMP% could take a whole tree on one verdict (WR-T2 own review, item 8)");
    }

    [Fact]
    public void The_recursive_delete_has_exactly_the_closed_list_of_callers()
    {
        var root = Metadata("WslCare.SourceRoot");
        var callers = SourceFiles()
            .Where(file => RecursiveDeleteMemberCall().IsMatch(CodeOnly(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        var added = callers.Except(RecursiveDeleters.Keys, StringComparer.Ordinal).Select(f => $"{f}: a NEW caller of the recursive IFileSystem.DeleteDirectory");
        var gone = RecursiveDeleters.Keys.Except(callers, StringComparer.Ordinal).Select(f => $"{f}: in the list, no longer a caller (remove it)");

        added.Concat(gone).Should().BeEmpty(
            "the recursive DeleteDirectory is a closed list (ArchitectureTests.RecursiveDeleters). A new caller must be JUSTIFIED - what it deletes, "
            + "under which root (the state or log directory, never %TEMP%) and why a per-file delete does not do - and RECORDED: add it to "
            + "RecursiveDeleters with that root and to todo/PLAN_windows_runner.md §14 (WR-T2 own review) and research/module_daemon.md");
    }

    [Fact]
    public void The_recursive_delete_scan_matches_a_planted_call_and_not_the_empty_folder_removal()
    {
        const string planted = "var v = files.DeleteDirectory(path, scope);\nvar w = files\n    .DeleteDirectory (path, scope);\nvar ok = files.DeleteEmptyDirectory(path, scope);\nRemove(files.DeleteDirectory);\n// files.DeleteDirectory(old)\npublic DeletionVerdict DeleteDirectory(string path, DeletionScope scope) => x;";

        RecursiveDeleteMemberCall().Matches(CodeOnly(planted)).Select(m => LineOf(CodeOnly(planted), m.Index)).Should().Equal(1, 3, 5);
        RecursiveDeleteAnyCall().Matches(CodeOnly(planted)).Select(m => LineOf(CodeOnly(planted), m.Index)).Should().Equal(1, 3, 5, 7);
        RecursiveDeleteAnyCall().IsMatch("var ok = files.DeleteEmptyDirectory(path, scope);").Should().BeFalse("the empty-folder removal is the one A23 may use");
        RecursiveDeleteMemberCall().IsMatch("var ok = files.DeleteEmptyDirectory(path, scope);").Should().BeFalse();
        RecursiveDeleters.Keys.Should().OnlyContain(f => File.Exists(Path.Combine(Metadata("WslCare.SourceRoot"), f)), "every listed caller still exists");
        RecursiveDeleteMemberCall().IsMatch(CodeOnly(File.ReadAllText(Path.Combine(Metadata("WslCare.SourceRoot"), "WslCare.Core", "Records", "RunRetention.cs"))))
            .Should().BeTrue("the scanner finds a real caller");
    }

    /// <summary>The source with each whole-line <c>//</c> comment blanked (line numbers kept), so prose that names the call is not a call.</summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));
}
