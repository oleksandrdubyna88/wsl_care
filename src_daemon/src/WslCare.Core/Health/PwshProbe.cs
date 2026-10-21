namespace WslCare.Core.Health;

/// <summary>
/// What the Windows clock probe said about the MSI PowerShell 7 (E15.S1b, PLAN_disk_health.md): its four tagged lines, each
/// as printed (empty when the probe printed the tag empty). The judgement over them is <see cref="PwshEngine"/>'s, never this
/// record's.
/// </summary>
/// <param name="KnownFolder">Program Files as the KNOWN-FOLDER API names it (<c>[Environment]::GetFolderPath('ProgramFiles')</c>).</param>
/// <param name="EnvProgramFiles">Program Files as the ENVIRONMENT names it (<c>$env:ProgramFiles</c>) — must agree, never decides.</param>
/// <param name="Msi">What is at <c>&lt;known folder&gt;\PowerShell\7\pwsh.exe</c>: <c>absent</c>, <c>link</c>, <c>directory</c>,
/// <c>empty</c>, <c>file</c>, or <c>error:&lt;type&gt;</c> when the look itself faulted.</param>
/// <param name="Signature">For a <c>file</c>: <c>signed</c> (Authenticode Valid from <c>CN=Microsoft Corporation</c>), else
/// <c>unsigned:&lt;status&gt;|&lt;subject&gt;</c>; empty otherwise.</param>
public sealed record PwshProbe(string KnownFolder, string EnvProgramFiles, string Msi, string Signature);
