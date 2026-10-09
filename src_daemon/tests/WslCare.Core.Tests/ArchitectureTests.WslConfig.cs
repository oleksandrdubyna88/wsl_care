using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>E14 S5 (owner question Q5: "shown, never written"): the product READS <c>.wslconfig</c> and ADVISES, and no product file
/// that names it holds a write — the daemon's file seam's writers, <c>File.Write*</c> / <c>File.Create</c> / <c>File.Append*</c>,
/// a stream writer, or the extension's <c>fs</c> writers. A companion proves the scan still finds a write (coai plan round
/// 2026-10-09, finding 5).</summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"\b(?:WriteFileAtomically|WritePrivateFileAtomically|CreateFileExclusively|ReplaceLinkWithFile|RewriteLines|AppendLine|StreamWriter|FileStream)\b|\bFile\s*\.\s*(?:Write\w*|Create\w*|Append\w*|Open)\s*\(|\b(?:writeFile|writeFileSync|appendFile|appendFileSync|createWriteStream)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex FileWrite();

    [GeneratedRegex(@"\.wslconfig|wslconfig", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamesWslConfig();

    /// <summary>Every write a text holds, as <c>line: text</c>.</summary>
    private static IEnumerable<string> Writes(string text) =>
        FileWrite().Matches(text).Select(m => $"{LineOf(text, m.Index)}: {m.Value}");

    [Fact]
    public void No_product_code_writes_wslconfig()
    {
        var root = Metadata("WslCare.SourceRoot");
        var extension = Path.GetFullPath(Path.Combine(root, "..", "..", "src_vs_code", "src"));
        var daemonFiles = SourceFiles();
        var extensionFiles = Directory.Exists(extension)
            ? Directory.EnumerateFiles(extension, "*.ts", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}"))
            : [];

        var naming = daemonFiles.Concat(extensionFiles).Where(f => NamesWslConfig().IsMatch(File.ReadAllText(f))).ToList();
        var offenders = naming.SelectMany(f => Writes(File.ReadAllText(f)).Select(w => $"{f}:{w}")).ToList();

        naming.Should().Contain(f => f.EndsWith("HealthCollector.cs", StringComparison.Ordinal), "the scan reads the files that name .wslconfig — the audit is one");
        offenders.Should().BeEmpty(".wslconfig is shown and advised, never written by the product (E14 S5, Q5)");
    }

    [Fact]
    public void The_wslconfig_write_scan_still_finds_a_write()
    {
        Writes("var path = profile + \"/.wslconfig\";\nfiles.WriteFileAtomically(path, bytes, scope);").Should().ContainSingle();
        Writes("await writeFile(join(home, '.wslconfig'), text);").Should().ContainSingle();
        Writes("File.WriteAllText(wslconfig, text);").Should().ContainSingle();
    }
}
