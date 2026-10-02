using System.Diagnostics;
using System.IO.Enumeration;

namespace WslCare.Core.Files;

/// <summary>
/// The bounded walk behind <see cref="IFileSystem.MeasureTree"/>: one <see cref="FileSystemEnumerable{TResult}"/>
/// pass, a stat per entry and no read, links neither counted nor entered (<see cref="FileAttributes.ReparsePoint"/>
/// is skipped — on Linux .NET reports a symlink with that attribute, on Windows a symlink or a junction), stopped at
/// the entry count or the deadline of <see cref="TreeLimits"/>, whichever comes first.
/// </summary>
/// <remarks>Why links are never followed: a walk of <c>~/.cache</c> or of <c>~/git</c> must not leave the tree it
/// was asked about — through a link into <c>/mnt/c</c> it would become the 9p walk that failed on 2026-10-01
/// (plan §2), and through a link into an AI agent's folder it would measure what is not its business (plan §4.6
/// measures those folders itself).</remarks>
internal static class TreeWalk
{
    private const int CancellationStride = 1024;

    public static TreeMeasure Measure(string root, TreeLimits limits, IReadOnlySet<string> countOnlyUnder, IReadOnlySet<string> neverEnter, CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(root);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TreeMeasure.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TreeMeasure.Unreadable($"{root} could not be inspected: {e.Message}");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new TreeMeasure.Unreadable($"{root} is a link; a walk never follows one");
        }

        return attributes.HasFlag(FileAttributes.Directory)
            ? Walk(root, limits, countOnlyUnder, neverEnter, cancellationToken)
            : new TreeMeasure.Unreadable($"{root} is a file, not a folder");
    }

    private static TreeMeasure Walk(string root, TreeLimits limits, IReadOnlySet<string> countOnlyUnder, IReadOnlySet<string> neverEnter, CancellationToken cancellationToken)
    {
        var state = new WalkState(Stopwatch.StartNew(), limits);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var entries = new FileSystemEnumerable<(long Length, bool Counted)>(
            root,
            (ref FileSystemEntry e) => (e.Length, countOnlyUnder.Count == 0 || IsUnderNamed(root, e.Directory.ToString(), countOnlyUnder)),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => !state.Stopped && !neverEnter.Contains(e.FileName.ToString()),
        };

        long bytes = 0;
        long files = 0;
        long visited = 0;
        foreach (var (length, counted) in entries)
        {
            visited++;
            if (counted)
            {
                bytes += length;
                files++;
            }

            if (visited % CancellationStride == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (state.Exhausted(visited) is { Length: > 0 } why)
            {
                state.Stopped = true;
                return new TreeMeasure.Measured(bytes, files, false, why);
            }
        }

        return new TreeMeasure.Measured(bytes, files, true, string.Empty);
    }

    /// <summary>Whether a file in <paramref name="directory"/> lies below a folder of one of <paramref name="names"/>, counted from <paramref name="root"/>.</summary>
    private static bool IsUnderNamed(string root, string directory, IReadOnlySet<string> names) =>
        directory.Length > root.Length
        && directory[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);

    private sealed class WalkState(Stopwatch watch, TreeLimits limits)
    {
        public bool Stopped { get; set; }

        /// <summary>Why the walk must stop now, or empty.</summary>
        public string Exhausted(long visited) =>
            visited >= limits.MaxEntries
                ? $"stopped after {limits.MaxEntries} entries; the figures are a lower bound"
                : watch.Elapsed > limits.MaxDuration
                    ? $"stopped after {limits.MaxDuration.TotalSeconds:0} s; the figures are a lower bound"
                    : string.Empty;
    }
}
