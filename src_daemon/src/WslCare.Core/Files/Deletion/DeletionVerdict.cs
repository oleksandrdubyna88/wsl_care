namespace WslCare.Core.Files.Deletion;

/// <summary>Which rule of the policy refused — a closed set, so a log line and a test can name it.</summary>
public enum DeletionRule
{
    /// <summary>The path is not inside the root the action declared.</summary>
    OutsideDeclaredRoot,

    /// <summary>The path is inside an AI agent's data folder, and no archive-move permit applies.</summary>
    AgentFolder,

    /// <summary>The path is inside the repositories folder (<c>~/git</c>).</summary>
    GitFolder,

    /// <summary>The path is inside the folder Claude Code keeps under the temp directory.</summary>
    ClaudeTemp,

    /// <summary>The path is inside an agent's <c>projects/*/memory/</c> — never moved, even by the archive.</summary>
    AgentMemory,

    /// <summary>A move's destination is itself a protected place.</summary>
    ProtectedDestination,

    /// <summary>The declared root is a filesystem root or the home directory — too broad to mean anything.</summary>
    RootTooBroad,

    /// <summary>A component of the path (or of the destination, or of the root) could not be inspected
    /// for a link — access denied, an unreadable reparse point, a cycle of links — so its real path is
    /// unknown and nothing is done to it. Fail closed: an unknown is never treated as a plain name.</summary>
    Unresolvable,

    /// <summary>Re-resolved just before the final step of an atomic write, the path no longer named
    /// the place that was approved — a link was swapped in after the decision.</summary>
    PathChanged,

    /// <summary>An archive permit was asked for something it does not allow: a rename that is not to or from its quarantine name in
    /// the same folder, a removal of a file that is not quarantined or names no archived copy outside the protected folders, the
    /// agent's own folder (E9.S2a).</summary>
    ArchiveShape,
}

/// <summary>Allowed, or refused by one named rule with a sentence a person can read.</summary>
public abstract record DeletionVerdict
{
    private DeletionVerdict()
    {
    }

    public static readonly DeletionVerdict Allowed = new AllowedVerdict();

    public static DeletionVerdict Refuse(DeletionRule rule, string reason) => new Refused(rule, reason);

    public bool IsAllowed => this is AllowedVerdict;

    public sealed record Refused(DeletionRule Rule, string Reason) : DeletionVerdict;

    private sealed record AllowedVerdict : DeletionVerdict;
}
