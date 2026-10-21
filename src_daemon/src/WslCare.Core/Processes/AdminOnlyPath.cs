using System.Globalization;

namespace WslCare.Core.Processes;

/// <summary>What a path is to the program that is started from it (PLAN_disk_hygiene_windows_E19.md §3 decision 9).</summary>
public enum AdminOnlyRole
{
    /// <summary>The program file: nobody but an administrator may write, append, delete, re-permission or take ownership of it.</summary>
    ProgramFile,

    /// <summary>A folder the program (or its DLLs) loads from: nobody but an administrator may create a file in it either.</summary>
    ProgramFolder,

    /// <summary>Any folder above them up to the volume root: nobody but an administrator may delete, delete a child of,
    /// re-permission or take ownership of it — creating an UNRELATED folder under <c>C:\</c> cannot replace a path component.</summary>
    Ancestor,
}

/// <summary>The access rights the rule looks at. The values are Windows' own security-descriptor access-mask bits (the same numbers as
/// <c>FileSystemRights</c>, whose enum is flagged Windows-only); the judge over them is "pure" in the sense that it makes no operating-system
/// call, so it runs — and is tested — on every platform.</summary>
[Flags]
public enum AclRight
{
    None = 0,
    WriteData = 0x2,
    AppendData = 0x4,

    /// <summary>FILE_WRITE_EA: extended attributes (and, on a file, alternate data).</summary>
    WriteExtendedAttributes = 0x10,
    WriteAttributes = 0x100,
    DeleteSubdirectoriesAndFiles = 0x40,
    Delete = 0x10000,
    ChangePermissions = 0x40000,
    TakeOwnership = 0x80000,

    /// <summary>MAXIMUM_ALLOWED: whatever the principal can be given — everything.</summary>
    MaximumAllowed = 0x02000000,

    /// <summary>GENERIC_EXECUTE. Grants no write.</summary>
    GenericExecute = 0x20000000,

    /// <summary>GENERIC_WRITE: includes <see cref="WriteData"/> and <see cref="AppendData"/>.</summary>
    GenericWrite = 0x40000000,

    /// <summary>GENERIC_READ. Grants no write.</summary>
    GenericRead = unchecked((int)0x80000000),

    /// <summary>GENERIC_ALL: everything.</summary>
    GenericAll = 0x10000000,
}

/// <summary>One access-control entry as the judge reads it: who, allow or deny, which rights, and whether it only applies to children.</summary>
public sealed record AclEntry(string Sid, bool Allow, AclRight Rights, bool InheritOnly);

/// <summary>An object's owner and entries — what a security descriptor says, with no operating system in it.</summary>
public sealed record AclFacts(string OwnerSid, IReadOnlyList<AclEntry> Entries);

/// <summary>Whether ONLY administrators could replace, or plant beside, a path. A seam: the real answer reads the security descriptor
/// (<see cref="WindowsAdminOnlyPath"/>); a test answers from a script.</summary>
public interface IAdminOnlyPath
{
    /// <summary>Why a non-administrator could change <paramref name="path"/> in the way <paramref name="role"/> forbids; empty when none could.</summary>
    string Problem(string path, AdminOnlyRole role);
}

/// <summary>
/// The rule of decision 9, pure over <see cref="AclFacts"/>. Principals that are NOT held to it: BUILTIN\Administrators, NT AUTHORITY\SYSTEM
/// and NT SERVICE\TrustedInstaller. An entry that only applies to children (inherit-only) says nothing about the object itself, and a deny
/// entry is ignored (the rule refuses what ANY allow entry could do — a conservative reading). The owner of an object may always change
/// its permissions, so an owner who is none of those principals is itself a problem.
/// </summary>
public static class AdminOnlyJudge
{
    private const string Administrators = "S-1-5-32-544";
    private const string System = "S-1-5-18";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly AclRight AncestorRights = AclRight.Delete | AclRight.DeleteSubdirectoriesAndFiles | AclRight.ChangePermissions | AclRight.TakeOwnership;

    /// <summary>Changing a program's attributes moves its timestamps (the identity a reused judgement is checked by) and extended attributes carry alternate
    /// data; neither is forbidden on an ANCESTOR, where <c>C:\</c> grants Authenticated Users append rights by default.</summary>
    private static readonly AclRight Attributes = AclRight.WriteAttributes | AclRight.WriteExtendedAttributes;

    private static AclRight Forbidden(AdminOnlyRole role) => role switch
    {
        AdminOnlyRole.ProgramFile => AclRight.WriteData | AclRight.AppendData | AclRight.Delete | AclRight.ChangePermissions | AclRight.TakeOwnership | Attributes,
        AdminOnlyRole.ProgramFolder => AclRight.WriteData | AncestorRights | Attributes,
        _ => AncestorRights,
    };

    /// <summary>Why <paramref name="facts"/> lets a non-administrator do what <paramref name="role"/> forbids; empty when it does not.</summary>
    public static string Problem(AclFacts facts, AdminOnlyRole role) =>
        !IsAdministrative(facts.OwnerSid)
            ? $"is owned by {facts.OwnerSid}, who can change its permissions"
            : facts.Entries.Where(e => Grants(e, Forbidden(role))).Select(e => Describe(e, Forbidden(role))).FirstOrDefault() ?? string.Empty;

    private const AclRight Looked = AclRight.WriteData | AclRight.AppendData | AclRight.DeleteSubdirectoriesAndFiles | AclRight.Delete | AclRight.ChangePermissions | AclRight.TakeOwnership
        | AclRight.WriteAttributes | AclRight.WriteExtendedAttributes;

    /// <summary>GENERIC_ALL, GENERIC_WRITE and MAXIMUM_ALLOWED each include every right the rule forbids to others.</summary>
    private const AclRight GrantsWrite = AclRight.GenericAll | AclRight.GenericWrite | AclRight.MaximumAllowed;

    /// <summary>The bits of a raw access mask the rule looks at: the specific rights it names, and the generic ones that include them. An entry
    /// may carry GENERIC_ALL or GENERIC_WRITE unmapped; dropping them would read it as "no rights". GENERIC_READ and GENERIC_EXECUTE are kept
    /// but grant no write.</summary>
    public static AclRight KnownRights(int accessMask) =>
        (AclRight)accessMask & (Looked | GrantsWrite | AclRight.GenericRead | AclRight.GenericExecute);

    private static bool IsAdministrative(string sid) => sid is Administrators or System or TrustedInstaller;

    private static bool Grants(AclEntry entry, AclRight forbidden) =>
        entry.Allow && !entry.InheritOnly && !IsAdministrative(entry.Sid) && (entry.Rights & (forbidden | GrantsWrite)) != 0;

    private static string Describe(AclEntry entry, AclRight forbidden) =>
        string.Create(CultureInfo.InvariantCulture, $"lets {entry.Sid} ({entry.Rights & (forbidden | GrantsWrite)}) change it");
}
