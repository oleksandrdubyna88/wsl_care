import { powerShellOf } from '../powershell/elevatedLauncher';

/**
 * Ending a child's whole PROCESS TREE under the family kill rule (WR-E2, plan round 4fc3f328 finding 8; the Opus own review B1):
 * only this host's OWN child and what that child started, each process judged by pid AND creation time, never by image name, and
 * never a process whose identity is no longer the one that was judged. This module BUILDS the one fixed request and judges the
 * outcome; it starts nothing itself — `process/runner.ts` (the one module that starts a process) hands it the function that does.
 *
 * <p>The request is ONE fixed PowerShell, started by its absolute path, whose script is a constant but for three integers it
 * validates. It (1) finds the root among the machine's processes and refuses unless the root's PARENT is this host and the root
 * was created AFTER this host started (so a reused pid is never taken for the child); (2) walks the descendants from the CIM
 * process table, keeping a child only when it was created no earlier than its parent (a reused parent pid cannot adopt a stranger);
 * (3) stops each, oldest first — the root before its children can start more — by id, re-reading its creation time right before
 * and stopping it only if it is still the process that was judged; (4) reads the table back and exits 0 only when NONE of them is
 * left. Any other exit is "the tree was not verified ended".</p>
 *
 * <p>It is asked for only while the root is still running: after Node saw the child exit (its pipes may still be open, the ceiling
 * may fire), the pid may already be another process's, and no tree kill is requested at all.</p>
 */

/** The program and argv of the one fixed, verifying process that ends a child's tree. */
export interface TreeKillRequest {
  readonly file: string;
  readonly args: readonly string[];
}

/** What the tree kill needs of the process it ends: its pid and whether it is still ours and running. */
export interface TreeRoot {
  readonly pid?: number | undefined;
  readonly exitCode: number | null;
  readonly signalCode: NodeJS.Signals | null;
}

/** The killer process as the tree kill sees it: it ends, fails to start, or is stopped when the bound is over. */
export interface Killer {
  on(event: 'exit', listener: (code: number | null) => void): unknown;
  on(event: 'error', listener: (error: Error) => void): unknown;
  kill(): unknown;
}

export interface TreeKillDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly hostPid: number;
  /** When this host started, in epoch milliseconds (a bound a little before the real start, not an exact figure). */
  readonly hostStartedAtMs: number;
  readonly spawnKiller: (request: TreeKillRequest) => Killer;
  /** How long the killer may run before it is stopped and the tree counts as not ended. */
  readonly boundMs: number;
}

function positiveInteger(value: number): boolean {
  return Number.isSafeInteger(value) && value > 0;
}

/** Three integers worth building a request from: positive, and the host is never its own root. */
function trusted(rootPid: number, hostPid: number, hostStartedAtMs: number): boolean {
  return [rootPid, hostPid, hostStartedAtMs].every(positiveInteger) && rootPid !== hostPid;
}

/**
 * The verifying script: constant text, three validated integers. No double quote (it travels as one command-line argument), and
 * `exit 0` only after the read-back found nobody left.
 */
function treeKillScript(rootPid: number, hostPid: number, hostStartedAtMs: number): string {
  return [
    "$ErrorActionPreference = 'Stop'",
    `$rootId = ${rootPid}`,
    `$hostId = ${hostPid}`,
    `$notBefore = [DateTimeOffset]::FromUnixTimeMilliseconds(${hostStartedAtMs}).UtcDateTime`,
    '$all = @(Get-CimInstance -ClassName Win32_Process)',
    '$root = @($all | Where-Object { $_.ProcessId -eq $rootId })',
    'if ($root.Count -ne 1) { exit 3 }',
    'if ($root[0].ParentProcessId -ne $hostId) { exit 4 }',
    'if ($root[0].CreationDate.ToUniversalTime() -lt $notBefore) { exit 5 }',
    '$found = @($root[0]); $queue = @($root[0]); $seen = @{}',
    'while ($queue.Count -gt 0) {',
    '$parent = $queue[0]; $queue = @($queue | Select-Object -Skip 1)',
    '$kids = @($all | Where-Object { $_.ParentProcessId -eq $parent.ProcessId -and $_.CreationDate -ge $parent.CreationDate -and -not $seen.ContainsKey([int]$_.ProcessId) })',
    'foreach ($k in $kids) { $seen[[int]$k.ProcessId] = $true }',
    '$found += $kids; $queue += $kids',
    '}',
    "foreach ($p in @($found | Sort-Object -Property CreationDate)) { $live = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $p.ProcessId) -ErrorAction SilentlyContinue; if ($null -ne $live -and $live.CreationDate -eq $p.CreationDate) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } }",
    'Start-Sleep -Milliseconds 300',
    '$now = @(Get-CimInstance -ClassName Win32_Process)',
    '$survivors = @($found | Where-Object { $f = $_; @($now | Where-Object { $_.ProcessId -eq $f.ProcessId -and $_.CreationDate -eq $f.CreationDate }).Count -gt 0 })',
    'if ($survivors.Count -gt 0) { exit 7 }',
    'exit 0',
  ].join('\n');
}

/**
 * The request that ends `rootPid`'s tree, or `undefined` when none can be made safely: a pid, host pid or start time that is not a
 * positive integer, the host named as its own root, or no absolute Windows PowerShell (`SystemRoot`).
 */
export function treeKillRequest(env: Readonly<Record<string, string | undefined>>, rootPid: number, hostPid: number, hostStartedAtMs: number): TreeKillRequest | undefined {
  const powerShell = powerShellOf(env);

  return trusted(rootPid, hostPid, hostStartedAtMs) && typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', treeKillScript(rootPid, hostPid, hostStartedAtMs)] } : undefined;
}

/** Whether the root is still the running process the runner started: no exit seen, no signal seen. */
function stillRunning(root: TreeRoot): root is TreeRoot & { readonly pid: number } {
  return root.pid !== undefined && root.exitCode === null && root.signalCode === null;
}

/** One killer, to its verdict: its exit 0, within the bound — anything else (another exit, no start, no end) is "not ended". */
function runKiller(request: TreeKillRequest, deps: TreeKillDeps): Promise<boolean> {
  return new Promise((resolve) => {
    let killer: Killer;
    try {
      killer = deps.spawnKiller(request);
    } catch {
      resolve(false);
      return;
    }
    const timer = setTimeout(() => { killer.kill(); resolve(false); }, deps.boundMs);
    const settle = (verdict: boolean): void => { clearTimeout(timer); resolve(verdict); };
    killer.on('exit', (code) => { settle(code === 0); });
    killer.on('error', () => { settle(false); });
  });
}

/**
 * Ends `root`'s tree and says whether it VERIFIED that it ended. A root that has already exited (or was signalled, or has no pid)
 * is never tree-killed — no request is made — and the answer is false.
 */
export function endTree(root: TreeRoot, deps: TreeKillDeps): Promise<boolean> {
  const request = stillRunning(root) ? treeKillRequest(deps.env, root.pid, deps.hostPid, deps.hostStartedAtMs) : undefined;

  return request === undefined ? Promise.resolve(false) : runKiller(request, deps);
}
