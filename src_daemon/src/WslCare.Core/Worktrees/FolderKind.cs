using System.Text.Json.Serialization;

namespace WslCare.Core.Worktrees;

/// <summary>What a direct child folder of a root is (PLAN_disk_hygiene_windows.md §3.3), known from file reads alone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FolderKind>))]
public enum FolderKind
{
    /// <summary>A <c>.git</c> FILE whose <c>gitdir:</c> names an existing admin folder whose own <c>gitdir</c> points back here.</summary>
    [JsonStringEnumMemberName("worktree")]
    Worktree,

    /// <summary>A <c>.git</c> FILE whose Windows pointer names nothing, or an admin folder that points elsewhere.</summary>
    [JsonStringEnumMemberName("orphan")]
    Orphan,

    /// <summary>A <c>.git</c> FILE whose pointer is a POSIX path no automount root maps (a worktree made inside WSL): live, never a candidate.</summary>
    [JsonStringEnumMemberName("foreignPointer")]
    ForeignPointer,

    /// <summary>A <c>.git</c> FOLDER: a main clone — reported, never a candidate.</summary>
    [JsonStringEnumMemberName("mainClone")]
    MainClone,

    /// <summary>No <c>.git</c> at all — reported, never a candidate.</summary>
    [JsonStringEnumMemberName("notGit")]
    NotGit,

    /// <summary>The folder itself is a link (junction, symlink): never entered.</summary>
    [JsonStringEnumMemberName("link")]
    Link,

    /// <summary>The pointer or its admin folder could not be read: not judged an orphan — never a candidate.</summary>
    [JsonStringEnumMemberName("unreadable")]
    Unreadable,
}
