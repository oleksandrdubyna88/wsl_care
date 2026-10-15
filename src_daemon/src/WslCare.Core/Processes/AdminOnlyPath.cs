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

/// <summary>The access rights the rule looks at, with the values of Windows' <c>FileSystemRights</c> (the enum is Windows-flagged,
/// the judge is pure and runs on every platform).</summary>
[Flags]
public enum AclRight
{
    None = 0,
    WriteData = 0x2,
    AppendData = 0x4,
    DeleteSubdirectoriesAndFiles = 0x40,
    Delete = 0x10000,
    ChangePermissions = 0x40000,
    TakeOwnership = 0x80000,
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

    private static AclRight Forbidden(AdminOnlyRole role) => role switch
    {
        AdminOnlyRole.ProgramFile => AclRight.WriteData | AclRight.AppendData | AclRight.Delete | AclRight.ChangePermissions | AclRight.TakeOwnership,
        AdminOnlyRole.ProgramFolder => AclRight.WriteData | AncestorRights,
        _ => AncestorRights,
    };

    /// <summary>Why <paramref name="facts"/> lets a non-administrator do what <paramref name="role"/> forbids; empty when it does not.</summary>
    public static string Problem(AclFacts facts, AdminOnlyRole role) =>
        !IsAdministrative(facts.OwnerSid)
            ? $"is owned by {facts.OwnerSid}, who can change its permissions"
            : facts.Entries.Where(e => Grants(e, Forbidden(role))).Select(e => Describe(e, Forbidden(role))).FirstOrDefault() ?? string.Empty;

    private static bool IsAdministrative(string sid) => sid is Administrators or System or TrustedInstaller;

    private static bool Grants(AclEntry entry, AclRight forbidden) =>
        entry.Allow && !entry.InheritOnly && !IsAdministrative(entry.Sid) && (entry.Rights & forbidden) != 0;

    private static string Describe(AclEntry entry, AclRight forbidden) =>
        string.Create(CultureInfo.InvariantCulture, $"lets {entry.Sid} ({entry.Rights & forbidden}) change it");
}
