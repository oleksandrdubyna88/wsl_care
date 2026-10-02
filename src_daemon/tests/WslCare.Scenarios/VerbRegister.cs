namespace WslCare.Scenarios;

/// <summary>
/// The derived verb register (plan §16 E1.S3; scenario rule point 4): every verb in
/// <c>CommandLine.Commands</c> must have a row in the flow catalogue of
/// <c>research/module_tests.md</c>, so a verb added without a scenario is a red build instead of a
/// catalogue that quietly stops being complete.
/// </summary>
/// <remarks>
/// A row belongs to a verb when its FIRST cell starts with <c>`wsl-care &lt;usage&gt;`</c> — the
/// usage exactly as the register spells it — and the row is inside the <c>## Flow catalogue</c>
/// section. Prose and other tables do not count: the whole file mentions most verbs somewhere, and a
/// check that accepted any mention could not go red.
/// </remarks>
internal static class VerbRegister
{
    public const string CatalogueHeading = "## Flow catalogue";

    /// <summary>The usages in <paramref name="usages"/> that have no flow-catalogue row, in order.</summary>
    public static IReadOnlyList<string> MissingFrom(string moduleTestsMarkdown, IEnumerable<string> usages)
    {
        var firstCells = CatalogueFirstCells(moduleTestsMarkdown);
        return [.. usages.Where(usage => !firstCells.Any(cell => cell.StartsWith($"`wsl-care {usage}`", StringComparison.Ordinal)))];
    }

    /// <summary>The first cell of every body row of the table(s) under <see cref="CatalogueHeading"/>.</summary>
    internal static IReadOnlyList<string> CatalogueFirstCells(string markdown)
    {
        var lines = markdown.Split(['\r', '\n'], StringSplitOptions.None);
        var inside = false;
        var cells = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = string.Equals(line.TrimEnd(), CatalogueHeading, StringComparison.Ordinal);
                continue;
            }

            if (inside && FirstCell(line) is { } cell)
            {
                cells.Add(cell);
            }
        }

        return cells;
    }

    /// <summary>The first cell of a table body row; <c>null</c> for a non-row or a separator row.</summary>
    private static string? FirstCell(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|'))
        {
            return null;
        }

        var end = trimmed.IndexOf('|', 1);
        var cell = (end < 0 ? trimmed[1..] : trimmed[1..end]).Trim();
        return cell.Length == 0 || cell.All(c => c is '-' or ':') ? null : cell;
    }
}
