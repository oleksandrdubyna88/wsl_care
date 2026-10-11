/**
 * The archive's capability names the extension reads from `status.capabilities` in more than one module (E10.S2a, own review #9) —
 * ONE definition each, in a module that imports nothing, so the panel's archive section and the Archive page share them without one
 * importing the other's controller. Each is the daemon's `Status/Capabilities.cs` constant of that name.
 */

/** `archive list [--json]` answers this side's archived entries, as the user (`Capabilities.ArchiveList`, E9.S3). */
export const ARCHIVE_LIST_CAPABILITY = 'archive.list';