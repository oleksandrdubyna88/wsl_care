namespace WslCare.Core.Files.Deletion;

/// <summary>What a scope may additionally do beyond deleting inside its own root.</summary>
public enum DeletionPermit
{
    None,

    /// <summary>The AI-session archive (plan A13): MOVE a session out of an agent's folder into the
    /// archive root. The only write the product ever makes under an agent's folder; a delete there
    /// stays refused, and <c>projects/*/memory/</c> stays refused even for a move.</summary>
    MoveOutOfAgentFolder,

    /// <summary>Plan §15r D2.8, E9.S2a: rename a session file IN ITS OWN FOLDER to its quarantine name
    /// (<c>&lt;name&gt;.wsl-care-q-&lt;runId&gt;</c>) and back — never anything else, never under <c>memory</c>.</summary>
    ArchiveQuarantine,

    /// <summary>Plan §15r D2.9, E9.S2a: remove a QUARANTINED file whose archived copy is named (the seam removes it only when its
    /// bytes hash equal to that copy), or an EMPTY folder strictly inside an agent's folder — never the agent's folder itself.</summary>
    ArchiveRemoval,

    /// <summary>Plan §15r D6, E9.S2a: a restore's CREATE under an agent's folder — create-only, never a replace, a rename or a
    /// removal, never under <c>memory</c>.</summary>
    RestoreIntoAgentFolder,
}

/// <summary>
/// What an action declares before it deletes or moves anything: the root it works under, its name
/// for the log, and any permit it holds.
/// </summary>
/// <param name="Root">The directory every target must be strictly inside (and every move must land inside).</param>
/// <param name="Action">Who is asking — an action id such as <c>A4</c> or <c>log-retention</c>, for the refusal line.</param>
/// <param name="Permit">An extra permission, or none.</param>
public sealed record DeletionScope(string Root, string Action, DeletionPermit Permit = DeletionPermit.None);

/// <summary>Delete, move, or create — the things the policy judges.</summary>
public enum FileOperation
{
    Delete,
    Move,

    /// <summary>A new file or folder (E9.S2a: the archive's exclusive creates, a restore's creates).</summary>
    Create,
}

/// <summary>
/// One decision's input, with every path already REAL (links followed, <c>..</c> applied): the file
/// system resolves before it asks, so the policy itself is pure.
/// </summary>
/// <param name="Operation">Delete, move or create.</param>
/// <param name="RealPath">The resolved target (the source, for a move).</param>
/// <param name="RealDestination">The resolved destination of a move; empty otherwise.</param>
/// <param name="RealRoot">The resolved declared root.</param>
/// <param name="Action">The scope's action name.</param>
/// <param name="Permit">The scope's permit.</param>
public sealed record DeletionRequest(
    FileOperation Operation,
    string RealPath,
    string RealDestination,
    string RealRoot,
    string Action,
    DeletionPermit Permit)
{
    /// <summary>For an <see cref="DeletionPermit.ArchiveRemoval"/> of a file: the archived copy it was verified against (real).</summary>
    public string ArchivedCopy { get; init; } = string.Empty;

    /// <summary>The target is a folder (an empty-folder removal).</summary>
    public bool IsFolder { get; init; }
}
