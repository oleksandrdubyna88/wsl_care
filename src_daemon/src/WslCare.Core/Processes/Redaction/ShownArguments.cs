namespace WslCare.Core.Processes.Redaction;

/// <summary>What a view may show of a command line by default: the program, the safe leading arguments, how many there are.
/// Equal when the three are equal AS VALUES — a list inside a record is compared element by element, not by reference.</summary>
public sealed record ShownCommandLine(string Program, IReadOnlyList<string> Arguments, int Total)
{
    public bool Equals(ShownCommandLine? other) =>
        other is not null && Program == other.Program && Total == other.Total && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Program, Total, Arguments.Count);
}

/// <summary>E15.S7 — STUB (the RED commit).</summary>
public static class ShownArguments
{
    public static ShownCommandLine Of(string name, IReadOnlyList<string> argv, IReadOnlyList<string> redacted, RedactionLimits limits, int shownSafeArgs) =>
        new(string.Empty, [], 0);
}
