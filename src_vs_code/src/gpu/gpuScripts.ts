import type { ProcessRequest } from '../process/runner';
import { elevatedArg, engineOf, MODULES, PWSH_PROBE, PWSH_TAGS, TAG_HELPER, type Engine, type FileInfoOf, type PwshFacts } from '../powershell/elevatedEngine';
import { elevatedRequest, powerShellOf } from '../powershell/elevatedLauncher';

/**
 * The two PowerShell texts of the GPU story (PLAN_gpu_health.md D1, D4): the UNELEVATED read of Windows' display adapters,
 * and the ELEVATED enable of ONE disabled adapter, run once through the Windows Time launcher (`elevatedRequest`:
 * `Process.Start` with `runas`, the inner script as `-EncodedCommand`; 1223 = declined, 13 = launch failed).
 *
 * <p>The query is a module constant. The enable is a pure function of ONE instance id — the id the extension's OWN query
 * read from Windows, selected on the host by an index (the page never sends an id) and refused unless it matches
 * {@link INSTANCE_ID}: no quote, no `$`, no backtick, no whitespace, so inside its single-quoted literal it is only text.
 * Nothing from WSL, the page or a setting's text reaches either script.</p>
 *
 * <p><b>Which PowerShell runs elevated</b> is chosen by the shared resolver, `src/powershell/elevatedEngine.ts` (G1 built it
 * here; PLAN_pwsh_engine_windows_time.md D1 moved it): the query embeds its probe, the enable takes its engine.</p>
 */

/** The query's tags, per adapter `i` (`name.<i>=` …), the count printed LAST so a cut-off answer is visible, and the Store package. */
export const TAGS = { adapter: 'adapter', name: 'name', id: 'id', code: 'code', driver: 'driver', count: 'adapters', ...PWSH_TAGS } as const;

/**
 * The UNELEVATED query (D1): `Win32_VideoController`, which lists a disabled adapter too (research G1), one tagged block per
 * adapter through {@link TAG_HELPER}, its NAME printed last (own review M6), the two PowerShell 7 installs as Windows resolves and verifies them, and the
 * count LAST. `$ProgressPreference` keeps Windows PowerShell's "Preparing modules for first use" off stderr (research G5).
 * It changes nothing — no word of the tripwire's mutating list is in it (asserted).
 */
export const QUERY_SCRIPT = [
  MODULES,
  "$ProgressPreference = 'SilentlyContinue'",
  TAG_HELPER,
  '$i = 0',
  `foreach ($a in @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)) { '${TAGS.adapter}=' + $i; tag ('${TAGS.id}.' + $i) $a.PNPDeviceID; tag ('${TAGS.code}.' + $i) $a.ConfigManagerErrorCode; tag ('${TAGS.driver}.' + $i) $a.DriverVersion; tag ('${TAGS.name}.' + $i) $a.Name; $i++ }`,
  PWSH_PROBE,
  `'${TAGS.count}=' + $i`,
  'exit 0',
].join('\n');

/** The read-only query, UNELEVATED: the absolute Windows PowerShell with the fixed script — no `runas`, no payload. */
export function queryRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  const powerShell = powerShellOf(env);

  return typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', QUERY_SCRIPT], timeoutMs } : powerShell.reason;
}

/** A PnP instance id as Windows spells one (`PCI\VEN_1002&DEV_7551&…\6&2A3DA1BE&0&00000019`) — and nothing a script reads. */
export const INSTANCE_ID = /^[A-Za-z0-9\\&_.#{}-]{1,200}$/;

/** The enable script's own exits, beside the launcher's 13 and 1223 (D4). */
export const ENABLE_EXIT = { done: 0, importFailed: 40, absent: 41, notDisplay: 42, notDisabled: 43, enableFailed: 44 } as const;

/** The only problem code the enable acts on: "disabled" (research G1). */
export const DISABLED_CODE = 22;

/**
 * The ELEVATED enable (D4) of the device `id`, which must already have passed {@link INSTANCE_ID}. It re-checks, as
 * administrator, that the device exists, is a display adapter and is still disabled — so an index that went stale between
 * the panel's render and the click acts on nothing it should not — and only then runs `Enable-PnpDevice`. `PnpDevice` comes
 * by absolute path because the module path is cut to `$PSHOME\Modules` (a user's Documents folder supplies nothing), where
 * `pwsh` 7 does not hold it (research G6); System32 is `[Environment]::SystemDirectory` — GetSystemDirectory, not an
 * environment variable the elevated process inherits from the person's registry (own review M1). It waits `settleSeconds` (`wslCare.gpu.enableSettleSeconds`) after the enable, so the
 * re-read that follows sees the driver started rather than starting — a NUMBER from a setting, clamped, never text.
 */
export function enableScript(id: string, settleSeconds: number): string {
  return [
    "$env:PSModulePath = \"$PSHOME\\Modules\"; $ProgressPreference = 'SilentlyContinue'",
    `try { Import-Module -Name (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\\v1.0\\Modules\\PnpDevice') -ErrorAction Stop } catch { exit ${ENABLE_EXIT.importFailed} }`,
    `$id = '${id}'`,
    `try { $d = Get-PnpDevice -InstanceId $id -ErrorAction Stop } catch { exit ${ENABLE_EXIT.absent} }`,
    `if ($d.Class -ne 'Display') { exit ${ENABLE_EXIT.notDisplay} }`,
    `if ([int]$d.ConfigManagerErrorCode -ne ${DISABLED_CODE}) { exit ${ENABLE_EXIT.notDisabled} }`,
    `try { Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } catch { exit ${ENABLE_EXIT.enableFailed} }`,
    `Start-Sleep -Seconds ${wholeSeconds(settleSeconds)}`,
    `exit ${ENABLE_EXIT.done}`,
  ].join('\n');
}

/** A whole, non-negative number of seconds; NaN reads as 0 (own review). */
export function wholeSeconds(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.trunc(value)) : 0;
}

/**
 * The one elevated request the enable ever builds — for the ENGINE the modal names and the settle value it shows, both looked
 * at once (own review 2, F1) — or why none can be (a refused id, no Windows PowerShell).
 */
export function enableRequestFor(env: Readonly<Record<string, string | undefined>>, id: string, timeoutMs: number, engine: Engine, settleSeconds: number): ProcessRequest | string {
  if (!INSTANCE_ID.test(id)) {
    return 'the device id Windows reported is not one this extension passes to an elevated command';
  }

  return elevatedRequest(env, enableScript(id, settleSeconds), timeoutMs, elevatedArg(engine));
}

/** {@link enableRequestFor} with the engine derived here — the one road, for a caller that holds no engine yet. */
export function enableRequest(env: Readonly<Record<string, string | undefined>>, id: string, timeoutMs: number, fileInfo: FileInfoOf, facts: PwshFacts | undefined, settleSeconds: number): ProcessRequest | string {
  return enableRequestFor(env, id, timeoutMs, engineOf(env, fileInfo, facts), settleSeconds);
}
