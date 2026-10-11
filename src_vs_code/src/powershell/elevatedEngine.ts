import * as fs from 'node:fs';
import * as path from 'node:path';

import { powerShellOf, quotablePath } from '../windowsTime/windowsTimeFix';

/**
 * The ONE resolver of the PowerShell that runs ELEVATED (PLAN_pwsh_engine_windows_time.md D1) — built by story G1 for
 * *Enable <GPU>* (PLAN_gpu_health.md §11, the owner's decision of 2026-10-10 and the path of 2026-10-11), used since by the
 * Windows Time launcher's user-context runs too (*Start Windows Time*, the guard's install and remove).
 *
 * <p>The order: PowerShell 7 by the MSI install under `%ProgramFiles%\PowerShell\7` when the read verified its signature
 * there, else the Store build by its RESOLVED package path
 * `%ProgramFiles%\WindowsApps\Microsoft.PowerShell_<version>_<arch>__8wekyb3d8bbwe\pwsh.exe`, else Windows PowerShell 5.1.
 * `WindowsApps` is written by TrustedInstaller and SYSTEM only, and Node cannot even see it (ENOENT, measured 2026-10-11), so an
 * UNELEVATED query resolves the package (`Get-AppxPackage`) and verifies its `pwsh.exe` — a real file, no link, an
 * Authenticode signature Windows reads as Valid from `CN=Microsoft Corporation` — at EVERY read; a caller elevates that path
 * only while the LAST read (the one taken right after its modal, before the launch) said so AND the path has the shape this
 * module expects under this machine's Program Files ({@link storePwsh}). The user's app-execution alias under
 * `%LOCALAPPDATA%` is never started: it is the user's own folder (research G8).</p>
 *
 * <p>A caller embeds {@link TAG_HELPER} and {@link PWSH_PROBE} in its own unelevated query, reads the answer back with
 * {@link pwshFactsFromTags}, chooses with {@link engineOf}, and hands {@link elevatedArg} to the launcher.</p>
 */

/** The probe's four tags — the GPU query spreads them into its own, so their names are the ones G1 shipped. */
export const PWSH_TAGS = { pwshKind: 'pwsh.store', pwshPath: 'pwsh.path', msiKind: 'pwsh.msi', msiPath: 'pwsh.msiPath' } as const;

/**
 * The ONE road every printed field takes (code round 2): a value carrying a line break — a device name, say — would end its
 * own line and let the rest read as another tag (`GPU\nadapters=0` read as "no display adapter reported"); the helper
 * flattens every break to a space, and a null prints as an empty value.
 */
export const TAG_HELPER = "function tag($n, $v) { $n + '=' + (([string]$v) -replace '[\\r\\n]+', ' ') }";

/**
 * ONE verdict for a PowerShell 7 the query looked at (the Store package and the MSI install alike, round 4): `signed` means
 * a real file with bytes, no link type, and `Get-AuthenticodeSignature` says Valid from `CN=Microsoft Corporation`;
 * `unsigned` anything else that is there; `absent` nothing.
 */
export const SIGNED_HELPER = "function verdict($i, $s) { if ($null -eq $i) { 'absent' } elseif ($i.Length -gt 0 -and [string]::IsNullOrEmpty($i.LinkType) -and $null -ne $s -and $s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -like 'CN=Microsoft Corporation,*') { 'signed' } else { 'unsigned' } }";

/**
 * The query's look at the two PowerShell 7 installs, as WINDOWS resolves and verifies them. The Store package:
 * `Get-AppxPackage -Name Microsoft.PowerShell` names its install folder (the version in it moves with every Store update —
 * measured 7.6.4 on 2026-10-10 and 7.6.6 the next day — so it is resolved at every read, never pinned). The MSI install: the
 * path under the Program Files the KNOWN-FOLDER API names (the registry's, not the environment's — round 4: a `ProgramFiles`
 * variable pointed at a user folder must not name the program this extension elevates). Each gets {@link SIGNED_HELPER}'s
 * verdict and its path. Measured cost (research G9): 0.9–1.3 s under Windows PowerShell 5.1, most of it `Get-AppxPackage`.
 */
export const PWSH_PROBE = [
  SIGNED_HELPER,
  '$sp = Get-AppxPackage -Name Microsoft.PowerShell -ErrorAction SilentlyContinue | Select-Object -First 1',
  "$se = if ($null -ne $sp) { Join-Path $sp.InstallLocation 'pwsh.exe' } else { '' }",
  "$si = if ($se -ne '') { Get-Item -LiteralPath $se -ErrorAction SilentlyContinue } else { $null }",
  '$ss = if ($null -ne $si) { Get-AuthenticodeSignature -LiteralPath $se } else { $null }',
  `tag '${PWSH_TAGS.pwshKind}' (verdict $si $ss)`,
  `tag '${PWSH_TAGS.pwshPath}' $se`,
  "$mp = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell\\7\\pwsh.exe'",
  '$mi = Get-Item -LiteralPath $mp -ErrorAction SilentlyContinue',
  '$ms = if ($null -ne $mi) { Get-AuthenticodeSignature -LiteralPath $mp } else { $null }',
  `tag '${PWSH_TAGS.msiKind}' (verdict $mi $ms)`,
  `tag '${PWSH_TAGS.msiPath}' $mp`,
].join('\n');

/** What Node's `lstat` says of a file the enable might elevate — `undefined` when it cannot be read (absent, or EACCES). */
export interface FileInfo {
  readonly bytes: number;
  readonly symlink: boolean;
}

export type FileInfoOf = (file: string) => FileInfo | undefined;

/** A PowerShell 7 as the LAST query verified it ({@link PWSH_PROBE}): the Store package, or the MSI install. */
export interface PwshProbe {
  readonly kind: 'signed' | 'unsigned' | 'absent';
  /** The `pwsh.exe` the query looked at — its stdout, used as a program path ONLY after this module accepted it. */
  readonly path: string;
}

/** The read's two PowerShell 7 facts, as the host hands them to the enable. */
export interface PwshFacts {
  readonly store: PwshProbe | undefined;
  readonly msi: PwshProbe | undefined;
}


/** The engine that runs ELEVATED — the program, the words the modal and the outcome name it by, and which of the three it is. */
export interface Engine {
  readonly file: string;
  /** "PowerShell 7 (C:\…\pwsh.exe)" / "Windows PowerShell 5.1 (C:\…\powershell.exe)". */
  readonly label: string;
  /** The MSI pwsh, the Store package, or Windows PowerShell 5.1 — the Store package is the one another account may not have (own review 2, F5). */
  readonly kind: 'msi' | 'store' | 'windows';
}

/** This machine's Program Files, from the environment (as the Windows Time launcher reads `SystemRoot`), or nothing. */
function programFilesOf(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const programFiles = env.ProgramFiles ?? env.PROGRAMFILES;

  return programFiles === undefined || programFiles === '' ? undefined : programFiles;
}

/** The MSI PowerShell 7 — an administrator-only folder, preferred whenever it is installed; the read verifies it at this path. */
export function msiPwsh(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const programFiles = programFilesOf(env);

  return programFiles === undefined ? undefined : path.win32.join(programFiles, 'PowerShell', '7', 'pwsh.exe');
}

/** Microsoft's Store publisher id — the suffix of every package folder Microsoft publishes through the Store. */
const STORE_PUBLISHER = '8wekyb3d8bbwe';

/** `text` as a regular expression matching itself. */
function literal(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The one shape a Microsoft Store PowerShell 7 package's `pwsh.exe` has under `programFiles` (case-insensitive). */
function storeShape(programFiles: string): RegExp {
  return new RegExp(`^${literal(programFiles)}\\\\WindowsApps\\\\Microsoft\\.PowerShell_[0-9.]+_[A-Za-z0-9]+__${STORE_PUBLISHER}\\\\pwsh\\.exe$`, 'i');
}

/** The resolved path, when the read verified the package's signature. */
function signedPath(store: PwshProbe | undefined): string | undefined {
  return store?.kind === 'signed' ? store.path : undefined;
}

/**
 * The Store package's `pwsh.exe` as the LAST read resolved it — accepted ONLY when the read verified its signature and the
 * path has exactly the shape Windows gives a Microsoft Store package under THIS machine's Program Files:
 * `<ProgramFiles>\WindowsApps\Microsoft.PowerShell_<version>_<arch>__8wekyb3d8bbwe\pwsh.exe` — a folder only
 * TrustedInstaller and SYSTEM may write. The stdout of a query becomes a program path through this check alone.
 */
export function storePwsh(env: Readonly<Record<string, string | undefined>>, store: PwshProbe | undefined): string | undefined {
  const programFiles = programFilesOf(env);
  const file = signedPath(store);

  return programFiles !== undefined && file !== undefined && storeShape(programFiles).test(file) ? file : undefined;
}

/** Whether `info` is a real file (not a link) with bytes in it. */
function realFile(info: FileInfo | undefined): boolean {
  return info !== undefined && !info.symlink && info.bytes > 0;
}

/** Whether the read verified a signed MSI pwsh at `file` — the path the known-folder API named, compared case-insensitively. */
function msiVerified(msi: PwshProbe | undefined, file: string): boolean {
  return msi !== undefined && msi.kind === 'signed' && msi.path.toLowerCase() === file.toLowerCase();
}

/**
 * The MSI pwsh, when the read verified its signature at the very path this module computes from `ProgramFiles` (round 4: an
 * environment pointed at a user folder makes the two differ, and nothing is elevated) and Node sees a real file there.
 */
function verifiedMsi(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, msi: PwshProbe | undefined): string | undefined {
  const file = msiPwsh(env);

  return file !== undefined && msiVerified(msi, file) && realFile(fileInfo(file)) ? file : undefined;
}

/** The MSI pwsh, when the read verified it and PowerShell can quote its path. */
function msiCandidate(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, msi: PwshProbe | undefined): string | undefined {
  const file = verifiedMsi(env, fileInfo, msi);

  return file !== undefined && quotablePath(file) ? file : undefined;
}

/** The Store package's pwsh, when the last read verified it, its path has the Store shape, and PowerShell can quote it. */
function storeCandidate(env: Readonly<Record<string, string | undefined>>, store: PwshProbe | undefined): string | undefined {
  const file = storePwsh(env, store);

  return file !== undefined && quotablePath(file) ? file : undefined;
}

/** No read yet: neither PowerShell 7 fact. */
const NO_FACTS: PwshFacts = { store: undefined, msi: undefined };

/** The PowerShell 7 that runs the enable elevated — the MSI one the read verified, else the verified Store package — and which. */
function sevenOf(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, facts: PwshFacts): { readonly file: string; readonly kind: 'msi' | 'store' } | undefined {
  const msi = msiCandidate(env, fileInfo, facts.msi);
  if (msi !== undefined) {
    return { file: msi, kind: 'msi' };
  }
  const store = storeCandidate(env, facts.store);

  return store === undefined ? undefined : { file: store, kind: 'store' };
}

/** The PowerShell 7 that runs the enable elevated (the MSI one, else the verified Store package), or `undefined` for 5.1. */
export function elevatedPowerShellFor(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, facts: PwshFacts | undefined): string | undefined {
  return sevenOf(env, fileInfo, facts ?? NO_FACTS)?.file;
}

/** The engine the enable elevates, named: PowerShell 7 when one is chosen, else Windows PowerShell 5.1 (or '' with no PowerShell at all). */
export function engineOf(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, facts: PwshFacts | undefined): Engine {
  const seven = sevenOf(env, fileInfo, facts ?? NO_FACTS);
  if (seven !== undefined) {
    return { ...seven, label: `PowerShell 7 (${seven.file})` };
  }
  const windows = powerShellOf(env);

  return typeof windows === 'string' ? { file: windows, label: `Windows PowerShell 5.1 (${windows})`, kind: 'windows' } : { file: '', label: '', kind: 'windows' };
}

function isProbeKind(kind: string | undefined): kind is PwshProbe['kind'] {
  return kind === 'signed' || kind === 'unsigned' || kind === 'absent';
}

/** A PowerShell 7 as the query verified it (`PWSH_PROBE`), when it answered one of the three kinds. */
function probeOf(tags: ReadonlyMap<string, string>, kindTag: string, pathTag: string): PwshProbe | undefined {
  const kind = tags.get(kindTag);

  return isProbeKind(kind) ? { kind, path: tags.get(pathTag) ?? '' } : undefined;
}

/** The probe's two answers in any tagged answer — each `undefined` when its tags are not there or not one of the three kinds. */
export function pwshFactsFromTags(tags: ReadonlyMap<string, string>): PwshFacts {
  return { store: probeOf(tags, PWSH_TAGS.pwshKind, PWSH_TAGS.pwshPath), msi: probeOf(tags, PWSH_TAGS.msiKind, PWSH_TAGS.msiPath) };
}

/** The launcher's last argument for `engine`: nothing for Windows PowerShell 5.1 (the launcher's own default), else its file. */
export function elevatedArg(engine: Engine): string | undefined {
  return engine.kind === 'windows' ? undefined : engine.file;
}

/** Why the Store package may not start under `runas` (own review 2, F5): the elevating account may not have it. */
const STORE_NOTE = ' — the Store package may not be available to the account that elevates; install PowerShell 7 (MSI) or elevate as your own account';

/** A failed exit's sentence, with the Store note when the elevated program itself could not be started and it was the package. */
export function withStoreNote(sentence: string, launchFailed: boolean, engine: Engine): string {
  return sentence + (launchFailed && engine.kind === 'store' ? STORE_NOTE : '');
}

/** What a caller says when the engine its modal named is not the one the read after the modal chose: nothing ran. */
export function engineChangedSentence(before: Engine, now: Engine): string {
  return `Nothing ran: the elevated program changed while the question was open (${before.label} is now ${now.label}). Press the button again.`;
}

/** Node's `lstat` of a program the enable might elevate — `undefined` when it cannot be read (absent; ENOENT under WindowsApps, EACCES on an app-execution alias). */
export function fileInfoOf(file: string): FileInfo | undefined {
  try {
    const info = fs.lstatSync(file);

    return { bytes: info.size, symlink: info.isSymbolicLink() };
  } catch {
    return undefined;
  }
}

