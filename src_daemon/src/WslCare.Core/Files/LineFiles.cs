namespace WslCare.Core.Files;

/// <summary>
/// Reading a line-appended file (<c>history.jsonl</c>, the container-starts day files) that a writer may be appending to at
/// this moment (gate finding #6). <see cref="IFileSystem.AppendLine"/> writes each line WITH its newline in one write, so a
/// trailing segment without one is a write still in progress — or the torn remains of a writer that died mid-write — and is
/// not a line yet: it is ignored, never parsed and never counted as corruption.
/// </summary>
/// <remarks>The torn remains do not stay invisible: the next append first ends them with a newline (see
/// <see cref="PhysicalFileSystem.AppendLine"/>), so they become one unparseable line of their own instead of swallowing the
/// next record.</remarks>
public static class LineFiles
{
    /// <summary>The complete lines of <paramref name="text"/> — those a newline ends — without the empty ones.</summary>
    public static IReadOnlyList<string> CompleteLines(string text)
    {
        var end = text.LastIndexOf('\n');
        return end < 0 ? [] : [.. text[..end].Split('\n').Where(l => l.Trim().Length > 0)];
    }
}
