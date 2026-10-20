/**
 * The first line of EVERY PowerShell script this extension runs that resolves a program (§11 R1, the guard's own k2 rule):
 * the module path pinned to `$PSHOME\Modules` by an assignment — no command to autoload — so `Get-AppxPackage` and
 * `Get-AuthenticodeSignature` come from the administrator-only module folder, never from a user's Documents.
 */
export const MODULES = "$env:PSModulePath = $PSHOME + '\\Modules'";
