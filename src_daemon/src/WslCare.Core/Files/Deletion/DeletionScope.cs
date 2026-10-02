namespace WslCare.Core.Files.Deletion;

/// <summary>What a scope may additionally do beyond deleting inside its own root.</summary>
public enum DeletionPermit
{
    None,

    /// <summary>The AI-session archive (plan A13): MOVE a session out of an agent's folder into the
    /// archive root. The only write the product ever makes under an agent's folder; a delete there
    /// stays refused, and <c>projects/*/memory/</c> stays refused even for a move.</summary>
    MoveOutOfAgentFolder,
}

/// <summary>
/// What an action declares before it deletes or moves anything: the root it works under, its name
/// for the log, and any permit it holds.
/// </summary>
/// <param name="Root">The directory every target must be strictly inside (and every move must land inside).</param>
/// <param name="Action">Who is asking — an action id such as <c>A4</c> or <c>log-retention</c>, for the refusal line.</param>
/// <param name="Permit">An extra permission, or none.</param>
public sealed record DeletionScope(string Root, string Action, DeletionPermit Permit = DeletionPermit.None);

/// <summary>Delete, or move — the two things the policy judges.</summary>
public enum FileOperation
{
    Delete,
    Move,
}

/// <summary>
/// One decision's input, with every path already REAL (links followed, <c>..</c> applied): the file
/// system resolves before it asks, so the policy itself is pure.
/// </summary>
/// <param name="Operation">Delete or move.</param>
/// <param name="RealPath">The resolved target (the source, for a move).</param>
/// <param name="RealDestination">The resolved destination of a move; empty for a delete.</param>
/// <param name="RealRoot">The resolved declared root.</param>
/// <param name="Action">The scope's action name.</param>
/// <param name="Permit">The scope's permit.</param>
public sealed record DeletionRequest(
    FileOperation Operation,
    string RealPath,
    string RealDestination,
    string RealRoot,
    string Action,
    DeletionPermit Permit);
