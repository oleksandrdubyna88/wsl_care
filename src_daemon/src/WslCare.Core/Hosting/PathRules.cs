namespace WslCare.Core.Hosting;

/// <summary>
/// The path conventions of one operating-system family — separators, case rules, how an absolute
/// path begins — as a pure value.
/// </summary>
/// <remarks>
/// <para>Deliberately NOT <see cref="Path"/>: the BCL answers for the machine the test runs on, and
/// the deletion policy has to be checkable for Windows paths on the Linux CI leg and for Linux paths
/// on the Windows one. Everything here is string arithmetic over an absolute path that has already
/// been resolved; nothing touches a disk.</para>
/// <para><see cref="IsStrictlyUnder"/> is the one comparison every protective rule rests on, so it
/// lives here once: a prefix match alone would call <c>~/gitx</c> a child of <c>~/git</c>.</para>
/// </remarks>
/// <param name="Separator">The separator a path is written with.</param>
/// <param name="AltSeparator">A second separator the family also accepts (<c>/</c> on Windows).</param>
/// <param name="Comparison">How two paths are compared: case-sensitive on Linux, insensitive on Windows.</param>
/// <param name="HasDriveLetters">Whether an absolute path may begin with <c>X:</c>.</param>
public sealed record PathRules(char Separator, char AltSeparator, StringComparison Comparison, bool HasDriveLetters)
{
    public static readonly PathRules Linux = new('/', '/', StringComparison.Ordinal, HasDriveLetters: false);

    public static readonly PathRules Windows = new('\\', '/', StringComparison.OrdinalIgnoreCase, HasDriveLetters: true);

    /// <summary>The rules of the operating system this process runs on.</summary>
    public static PathRules ForThisOs => OperatingSystem.IsWindows() ? Windows : Linux;

    public bool IsSeparator(char c) => c == Separator || c == AltSeparator;

    /// <summary>Appends segments to a path with this family's separator; never touches the disk.</summary>
    public string Join(string root, params ReadOnlySpan<string> segments)
    {
        var text = TrimTrailingSeparators(root);
        foreach (var segment in segments)
        {
            text = text.Length > 0 && !IsSeparator(text[^1]) ? $"{text}{Separator}{segment}" : text + segment;
        }

        return text;
    }

    /// <summary>
    /// The alternative separator replaced, runs of separators collapsed, and trailing separators
    /// removed — except on a bare root (<c>/</c>, <c>C:\</c>), which keeps its one separator.
    /// </summary>
    public string Normalize(string path)
    {
        var text = Collapse(path.Replace(AltSeparator, Separator));
        return TrimTrailingSeparators(text);
    }

    public bool PathEquals(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="root"/> — a proper descendant,
    /// never the root itself.</summary>
    public bool IsStrictlyUnder(string path, string root)
    {
        var child = Normalize(path);
        var parent = Normalize(root);
        if (child.Length <= parent.Length || !child.StartsWith(parent, Comparison))
        {
            return false;
        }

        // A bare root ends in its separator, so anything longer that starts with it is under it.
        return IsSeparator(parent[^1]) || IsSeparator(child[parent.Length]);
    }

    public bool IsSameOrUnder(string path, string root) => PathEquals(path, root) || IsStrictlyUnder(path, root);

    /// <summary>Whether <paramref name="path"/> is a filesystem root: <c>/</c>, <c>C:\</c> or a UNC share.</summary>
    public bool IsRoot(string path)
    {
        var text = Normalize(path);
        return IsAbsolute(text) && Split(text).Segments.Count == 0;
    }

    public bool IsAbsolute(string path) => RootPrefixLength(path.Replace(AltSeparator, Separator)) > 0;

    /// <summary>The root prefix and the segments after it. Throws for a relative path — the callers
    /// here always hold an absolute one, and a relative path at this layer is a bug upstream.</summary>
    public (string Prefix, IReadOnlyList<string> Segments) Split(string absolutePath)
    {
        var text = Normalize(absolutePath);
        var prefixLength = RootPrefixLength(text);
        if (prefixLength == 0)
        {
            throw new ArgumentException($"\"{absolutePath}\" is not an absolute path.", nameof(absolutePath));
        }

        var segments = text[prefixLength..].Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        return (text[..prefixLength], segments);
    }

    /// <summary>Segments only — the drive or root prefix dropped.</summary>
    public IReadOnlyList<string> Segments(string absolutePath) => Split(absolutePath).Segments;

    private string TrimTrailingSeparators(string text)
    {
        var end = text.Length;
        while (end > 0 && IsSeparator(text[end - 1]))
        {
            end--;
        }

        var prefix = RootPrefixLength(text);
        return end < prefix ? text[..prefix] : text[..end];
    }

    private string Collapse(string text)
    {
        // A UNC path begins with two separators that mean something; keep them.
        var keep = HasDriveLetters && text.Length >= 2 && text[0] == Separator && text[1] == Separator ? 2 : 0;
        var builder = new System.Text.StringBuilder(text.Length);
        builder.Append(text, 0, keep);
        for (var i = keep; i < text.Length; i++)
        {
            if (text[i] == Separator && builder.Length > keep && builder[^1] == Separator)
            {
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    /// <summary>How many characters of <paramref name="text"/> are its root prefix; 0 when relative.</summary>
    private int RootPrefixLength(string text)
    {
        if (!HasDriveLetters)
        {
            return text.Length > 0 && text[0] == Separator ? 1 : 0;
        }

        return WindowsRootPrefixLength(text);
    }

    private int WindowsRootPrefixLength(string text)
    {
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
        {
            // "C:" alone is a drive-relative path, not absolute; "C:\" is.
            return text.Length >= 3 && text[2] == Separator ? 3 : 0;
        }

        if (text.Length >= 2 && text[0] == Separator && text[1] == Separator)
        {
            return UncPrefixLength(text);
        }

        return text.Length > 0 && text[0] == Separator ? 1 : 0;
    }

    /// <summary><c>\\server\share\</c> — server and share are part of the root, not segments.</summary>
    private int UncPrefixLength(string text)
    {
        var afterServer = text.IndexOf(Separator, 2);
        if (afterServer < 0)
        {
            return 0;
        }

        var afterShare = text.IndexOf(Separator, afterServer + 1);
        return afterShare < 0 ? text.Length : afterShare + 1;
    }
}
