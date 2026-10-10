import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import type { DurableStore } from '../cleanup/journal';
import { enableChoices, noChoiceMessage, NONE_DISABLED } from '../gpu/gpuCommand';
import { enableExitOf, enableGpu, enablePrompt, enableSentence, type EnableDeps } from '../gpu/gpuEnable';
import { GpuHost, type GpuHostDeps } from '../gpu/gpuHost';
import { GPU_PENDING_KEY, readGpuPending, sweepGpuPending } from '../gpu/gpuPending';
import { DEVICE_PROBLEMS, FIX_WORDS, NEEDS_RESTART } from '../gpu/gpuProblems';
import { ALIAS_PROBE, aliasPwsh, DISABLED_CODE, elevatedPowerShellFor, ENABLE_EXIT, enableRequest, enableScript, engineOf, INSTANCE_ID, QUERY_SCRIPT, queryRequest, TAG_HELPER, type FileInfo, type PwshAlias } from '../gpu/gpuScripts';
import { fixOf, gpuBarMark, gpuStateOf, gpuView, MAX_ADAPTERS, NOT_ASKED, parseGpuAnswer, type Adapter, type GpuState } from '../gpu/gpuState';
import { newGpuRecorder } from '../gpu/gpuRecorder';
import { choiceSentence, lastChoice, ollamaLogPath, readError, readTail } from '../gpu/ollamaLog';
import { parsePageMessage } from '../panel/messages';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { withGpuMark } from '../statusBar/statusBarModel';
import { EXIT } from '../windowsTime/windowsTimeFix';
import { isElevatedPowerShell, isMachineChange, MUTATING, revealed } from './support/noRealWsl';

/**
 * Story G1 (PLAN_gpu_health.md): the adapters Windows reports, judged; the alert, the bar mark; *Enable <GPU>* — its id
 * check, its script, its order, its outcomes worded from what Windows says AFTER. The fixtures are the owner's machine as
 * measured on 2026-10-10 (research/2026-10-10_gpu_disabled.md G1, G2, G5): the integrated 890M at 0, the R9700 at 22.
 */

const R9700_ID = 'PCI\\VEN_1002&DEV_7551&SUBSYS_B0201F4C&REV_C0\\6&2A3DA1BE&0&00000019';
const IGPU_ID = 'PCI\\VEN_1002&DEV_150E&SUBSYS_B0201F4C&REV_C1\\4&27F515C0&0&0041';

/** The D1 query's answer exactly as Windows PowerShell 5.1 printed it on the owner's machine (research G5). */
const MEASURED = [
  'adapter=0', 'name.0=AMD Radeon(TM) 890M Graphics', `id.0=${IGPU_ID}`, 'code.0=0', 'driver.0=32.0.31041.1004',
  'adapter=1', 'name.1=AMD Radeon AI PRO R9700', `id.1=${R9700_ID}`, 'code.1=22', 'driver.1=32.0.31041.1004',
  'adapters=2', '',
].join('\r\n');

const ENV = { SystemRoot: 'C:\\Windows', ProgramFiles: 'C:\\Program Files', LOCALAPPDATA: 'C:\\Users\\user\\AppData\\Local' };
const AT = Date.UTC(2026, 9, 10, 12, 0, 0);

const PS51 = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';
const PS51_LABEL = `Windows PowerShell 5.1 (${PS51})`;
const MSI_PWSH = 'C:\\Program Files\\PowerShell\\7\\pwsh.exe';
const ALIAS_PWSH = 'C:\\Users\\user\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh.exe';
const ALIAS: PwshAlias = { kind: 'alias', path: ALIAS_PWSH };

/** What Node's lstat would say per path — nothing for the rest (absent, or EACCES as on the real alias). */
function files(table: Readonly<Record<string, FileInfo>>): (file: string) => FileInfo | undefined {
  return (file) => table[file];
}

function exited(code: number, stdout = ''): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.alloc(0) };
}

function measured(code = 22): GpuState {
  return parseGpuAnswer(MEASURED.replace('code.1=22', `code.1=${code}`), AT);
}

function r9700(state: GpuState = measured()): Adapter {
  assert.equal(state.kind, 'read');
  return (state as Extract<GpuState, { kind: 'read' }>).adapters[1] as Adapter;
}

class MemoryStore implements DurableStore {
  readonly values = new Map<string, unknown>();
  get<T>(key: string): T | undefined { return this.values.get(key) as T | undefined; }
  update(key: string, value: unknown): Promise<void> { if (value === undefined) { this.values.delete(key); } else { this.values.set(key, structuredClone(value)); } return Promise.resolve(); }
}

// ---- D1: the query and its answer ----

test('the measured answer is two adapters, the R9700 at code 22 with its id as Windows spelled it', () => {
  const state = measured();
  assert.equal(state.kind, 'read');
  const adapters = (state as Extract<GpuState, { kind: 'read' }>).adapters;
  assert.deepEqual(adapters.map((a) => [a.name, a.code]), [['AMD Radeon(TM) 890M Graphics', 0], ['AMD Radeon AI PRO R9700', 22]]);
  assert.equal(r9700(state).id, R9700_ID);
  assert.ok(INSTANCE_ID.test(r9700(state).id), 'the measured id passes the id check (the fixture is accepted by the real validator)');
});

test('an answer cut before its count, an adapter without a code, an exit, a timeout — each is ONE unknown state with its reason, never "no GPU"', () => {
  const cut = parseGpuAnswer(MEASURED.split('adapters=2')[0] ?? '', AT);
  assert.deepEqual(cut, { kind: 'unknown', reason: 'the display-adapter query printed no complete answer' });
  assert.equal(parseGpuAnswer(MEASURED.replace('code.1=22', 'code.1=x'), AT).kind, 'unknown');
  assert.match((gpuStateOf(exited(1), AT) as { reason: string }).reason, /exited 1/);
  assert.match((gpuStateOf({ kind: 'timedOut', timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, AT) as { reason: string }).reason, /20 s \(wslCare\.timeouts\.gpuQuerySeconds\)/);
  assert.deepEqual(parseGpuAnswer('adapters=0', AT), { kind: 'read', adapters: [], atUtcMs: AT }, 'a machine with no display adapter is a READ, not unknown');
});

function adapters(count: number, codeOf: (i: number) => number): string {
  return Array.from({ length: count }, (_v, i) => `name.${i}=GPU ${i}\ncode.${i}=${codeOf(i)}\nid.${i}=PCI\\X${i}`).join('\n') + `\nadapters=${count}`;
}

test('code round: EVERY reported adapter up to the cap is judged — a broken ninth one is an alert at its own index', () => {
  const state = parseGpuAnswer(adapters(9, (i) => (i === 8 ? 43 : 0)), AT);
  assert.equal(state.kind === 'read' ? state.adapters.length : -1, 9);
  const view = gpuView(state, undefined, undefined, () => '');
  assert.deepEqual(view.alerts.map((a) => a.line), ['GPU GPU 8 is not working (code 43: Windows stopped it because it reported problems)']);
  assert.equal(gpuBarMark(state).text, ' · GPU off');
});

test('code round: more adapters than the cap is an explicit UNKNOWN — never a partial read presented as complete', () => {
  const state = parseGpuAnswer(adapters(MAX_ADAPTERS + 1, () => 0), AT);
  assert.deepEqual(state, { kind: 'unknown', reason: `Windows reported ${MAX_ADAPTERS + 1} display adapters; this extension reads at most ${MAX_ADAPTERS}` });
  assert.equal(parseGpuAnswer(adapters(MAX_ADAPTERS, () => 0), AT).kind, 'read', 'exactly the cap is read');
});

test('the query is unelevated and READ-only: no runas, no payload, no mutating word — and the tripwire lets it through', () => {
  const request = queryRequest(ENV, 20_000) as ProcessRequest;
  assert.equal(request.file, 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe');
  assert.deepEqual(request.args.slice(0, 3), ['-NoProfile', '-NonInteractive', '-Command']);
  assert.equal(request.args[3], QUERY_SCRIPT);
  assert.equal(MUTATING.test(QUERY_SCRIPT), false);
  assert.equal(isElevatedPowerShell(request.file, request.args), false);
  assert.equal(isMachineChange(request.file, request.args), false);
  assert.equal(typeof queryRequest({}, 20_000), 'string', 'no SystemRoot: no request, the reason instead');
});

// ---- D3: the verdict table and the alert ----

test('every listed code reads its own meaning and fix; 22 alone is "enable"; an unlisted code is "unknown" with its number', () => {
  const codes = DEVICE_PROBLEMS.map((p) => p.code);
  assert.equal(new Set(codes).size, codes.length, 'each code once');
  for (const problem of DEVICE_PROBLEMS) {
    assert.equal(fixOf(problem.code), problem.fix, String(problem.code));
    assert.equal(problem.fix === 'enable', problem.code === DISABLED_CODE, `only 22 enables (${problem.code})`);
  }
  assert.equal(fixOf(99), 'unknown');
  assert.equal(fixOf(45), 'reconnect', 'plan round: code 45 is not "restart and reinstall"');
  for (const [fix, words] of Object.entries(FIX_WORDS)) {
    assert.equal(/restart Windows/i.test(words), NEEDS_RESTART[fix as keyof typeof FIX_WORDS], `${fix}: the words say a restart exactly when one is needed`);
  }
});

test('the measured state is ONE critical alert, for the R9700: its code and meaning, the enable advice, the consequence, and an Enable button by its index', () => {
  const view = gpuView(measured(), undefined, undefined, () => '14:31');
  assert.equal(view.line, '');
  assert.equal(view.alerts.length, 1);
  const [alert] = view.alerts;
  assert.equal(alert?.level, 'critical');
  assert.equal(alert?.line, 'GPU AMD Radeon AI PRO R9700 is not working (code 22: it is disabled)');
  assert.match(alert?.fix ?? '', /no restart is needed/);
  assert.match(alert?.consequence ?? '', /local models such as Ollama — run on the CPU or do not start/);
  assert.deepEqual(alert?.button, { index: 1, label: 'Enable AMD Radeon AI PRO R9700', enabled: true, readAt: AT }, 'the button carries the READ it was rendered from, so a press after a newer read is refused (code round 2)');
});

test('code 31 and 43 are critical with restart advice and NO button; code 45 says Windows does not see the card', () => {
  for (const code of [31, 43]) {
    const [alert] = gpuView(measured(code), undefined, undefined, () => '').alerts;
    assert.match(alert?.fix ?? '', /^Restart Windows/, String(code));
    assert.equal(alert?.button, undefined, `no button for ${code}`);
  }
  assert.match(gpuView(measured(45), undefined, undefined, () => '').alerts[0]?.fix ?? '', /Windows does not see the card/);
});

test('all adapters working: no alert, one muted line naming them and when they were checked; Ollama\'s line rides on the consequence', () => {
  const view = gpuView(measured(0), undefined, undefined, () => '14:31');
  assert.deepEqual(view.alerts, []);
  assert.equal(view.line, 'GPU: AMD Radeon(TM) 890M Graphics, AMD Radeon AI PRO R9700 — working (checked 14:31)');
  const cpu = { library: 'cpu', device: '', time: '2026-10-10T11:49:08.333+02:00' };
  assert.match(gpuView(measured(), cpu, undefined, () => '').alerts[0]?.consequence ?? '', /Ollama's last start \(2026-10-10T11:49:08\.333\+02:00\) chose the CPU\./);
});

test('a check that could not run is its OWN alert and a "GPU ?" bar mark — never silence (plan round, finding 2); before the first answer, nothing', () => {
  const unknown: GpuState = { kind: 'unknown', reason: 'the display-adapter query exited 1' };
  assert.deepEqual(gpuView(unknown, undefined, undefined, () => '').alerts.map((a) => [a.level, a.line]), [['unknown', 'GPU state could not be checked: the display-adapter query exited 1']]);
  assert.deepEqual(gpuBarMark(unknown), { text: ' · GPU ?', level: 'warn', lines: ['GPU state could not be checked: the display-adapter query exited 1'] });
  assert.deepEqual(gpuView(NOT_ASKED, undefined, undefined, () => '').alerts, []);
  assert.equal(gpuBarMark(NOT_ASKED).text, '');
});

test('the bar: "GPU off" and the error colour over whatever it shows — a mark never lowers the level, and no mark leaves the bar as it was', () => {
  const plain = { text: 'WSL RAM 15% · swap 0.0G · 12 containers', tooltip: 'AI OS Care — Ubuntu', level: 'warn' as const };
  const marked = withGpuMark(plain, gpuBarMark(measured()));
  assert.equal(marked.text, 'WSL RAM 15% · swap 0.0G · 12 containers · GPU off');
  assert.equal(marked.level, 'critical');
  assert.equal(marked.tooltip.split('\n')[0], 'GPU AMD Radeon AI PRO R9700: not working (code 22: it is disabled)');
  assert.deepEqual(withGpuMark(plain, gpuBarMark(measured(0))), plain);
  assert.equal(withGpuMark({ ...plain, level: 'critical' }, gpuBarMark({ kind: 'unknown', reason: 'x' })).level, 'critical', 'a "?" never lowers a critical bar');
});

// ---- D4: the enable's request and script ----

test('the enable is ONE request through the Windows Time launcher: runas, the script encoded, the device id inside it as a single-quoted literal', () => {
  const request = enableRequest(ENV, R9700_ID, 120_000, files({}), undefined, 3) as ProcessRequest;
  assert.equal(request.file, PS51);
  assert.ok(isElevatedPowerShell(request.file, request.args), 'it asks Windows for elevation — so the tripwire refuses it in any test that tried to run it');
  assert.ok(isMachineChange(request.file, request.args), 'and the decoded payload carries a device verb the tripwire knows');
  const inner = revealed(request.args);
  assert.ok(inner.includes(`$id = '${R9700_ID}'`), 'the id travels inside the ENCODED inner script');
  assert.ok(!request.args.join(' ').includes(R9700_ID), 'and never as plain text on the outer command line');
});

test('the elevated script re-checks the device as administrator before it acts: present, a display adapter, still at 22 — each with its own exit', () => {
  const script = enableScript(R9700_ID, 3);
  const order = ['Import-Module', 'Get-PnpDevice', "$d.Class -ne 'Display'", `[int]$d.ConfigManagerErrorCode -ne ${DISABLED_CODE}`, 'Enable-PnpDevice'].map((s) => script.indexOf(s));
  assert.ok(order.every((at, i) => at >= 0 && (i === 0 || at > (order[i - 1] ?? -1))), `the checks come before the enable, in order: ${order.join(',')}`);
  assert.match(script, /^\$env:PSModulePath = "\$PSHOME\\Modules"/, 'the module path is cut first: a user\'s Documents folder supplies nothing');
  assert.ok(script.includes("Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\\v1.0\\Modules\\PnpDevice'"), 'PnpDevice by absolute path under GetSystemDirectory (research G6, own review M1)');
  for (const code of Object.values(ENABLE_EXIT).filter((c) => c !== 0)) {
    assert.ok(script.includes(`exit ${code}`), `exit ${code} is reachable`);
  }
});

test('an id that could be more than text is refused before any request exists: a quote, $, a backtick, whitespace, a newline, 201 characters', () => {
  for (const id of [`${R9700_ID}'`, `${R9700_ID}$x`, `${R9700_ID}\``, 'PCI\\VEN 1002', 'PCI\\VEN\r\nexit', `PCI\\${'A'.repeat(197)}`, '', `PCI\\VEN';Remove-Item C:\\ -Recurse;'`]) {
    assert.equal(typeof enableRequest(ENV, id, 120_000, files({ [MSI_PWSH]: { bytes: 300_000, symlink: false } }), ALIAS, 3), 'string', JSON.stringify(id));
    assert.equal(INSTANCE_ID.test(id), false, JSON.stringify(id));
  }
  assert.equal(INSTANCE_ID.test(`PCI\\${'A'.repeat(196)}`), true, '200 characters is the largest accepted');
});

test('owner 2026-10-10: the elevated engine is PowerShell 7 — the MSI install when it is a real file, else the Store alias while the LAST read saw a zero-byte app-execution alias at the path this extension computes — and Windows PowerShell 5.1 otherwise', () => {
  assert.equal(elevatedPowerShellFor(ENV, files({ [MSI_PWSH]: { bytes: 300_000, symlink: false } }), ALIAS), MSI_PWSH, 'an administrator-only install is preferred over the alias');
  assert.equal(elevatedPowerShellFor(ENV, files({ [MSI_PWSH]: { bytes: 300_000, symlink: true } }), undefined), undefined, 'a link where the MSI pwsh should be is not elevated');
  assert.equal(elevatedPowerShellFor(ENV, files({ [MSI_PWSH]: { bytes: 0, symlink: false } }), undefined), undefined, 'an empty file there is not elevated');
  assert.equal(elevatedPowerShellFor(ENV, files({}), ALIAS), ALIAS_PWSH, 'the Store alias, as the query saw it');
  assert.equal(elevatedPowerShellFor(ENV, files({}), { kind: 'file', path: ALIAS_PWSH }), undefined, 'a real file (or a link) where the alias should be is not elevated');
  assert.equal(elevatedPowerShellFor(ENV, files({}), { kind: 'absent', path: ALIAS_PWSH }), undefined);
  assert.equal(elevatedPowerShellFor(ENV, files({}), { kind: 'alias', path: 'C:\\Elsewhere\\WindowsApps\\pwsh.exe' }), undefined, 'the query looked at another path than this extension would start');
  assert.equal(elevatedPowerShellFor(ENV, files({}), { kind: 'alias', path: ALIAS_PWSH.toUpperCase() }), ALIAS_PWSH, 'the same path in another case is the same path');
  assert.equal(elevatedPowerShellFor({ ...ENV, LOCALAPPDATA: "C:\\Users\\O'Brien\\AppData\\Local" }, files({}), { kind: 'alias', path: "C:\\Users\\O'Brien\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh.exe" }), undefined, 'a path PowerShell could not quote is skipped — 5.1 runs, nothing is refused');
  assert.equal(elevatedPowerShellFor(ENV, files({}), undefined), undefined, 'before any read: 5.1');
  const withAlias = enableRequest(ENV, R9700_ID, 120_000, files({}), ALIAS, 3) as ProcessRequest;
  assert.match(withAlias.args[3] ?? '', /ProcessStartInfo -ArgumentList 'C:\\Users\\user\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh\.exe'/);
  assert.equal(withAlias.file, PS51, 'the LAUNCHER stays Windows PowerShell; only the elevated program changes');
  const without = enableRequest(ENV, R9700_ID, 120_000, files({}), undefined, 3) as ProcessRequest;
  assert.match(without.args[3] ?? '', /ProcessStartInfo -ArgumentList 'C:\\Windows\\System32\\WindowsPowerShell\\v1\.0\\powershell\.exe'/);
  assert.deepEqual(engineOf(ENV, files({}), ALIAS), { file: ALIAS_PWSH, label: `PowerShell 7 (${ALIAS_PWSH})` });
  assert.deepEqual(engineOf(ENV, files({}), undefined), { file: PS51, label: PS51_LABEL });
  assert.equal(aliasPwsh(ENV), ALIAS_PWSH);
});

test('code round 2 / owner 2026-10-10: the query prints every field through ONE helper that strips line breaks, and reports the Store alias as WINDOWS sees it — RUN by Windows PowerShell on this machine, once, read-only', { skip: process.platform !== 'win32' ? 'Windows PowerShell exists on the Windows leg only' : false }, () => {
  assert.equal((QUERY_SCRIPT.match(/\btag \(/g) ?? []).length, 4, 'id, code, driver and name each go through the helper');
  // ONE PowerShell for both probes: the suite runs its files in parallel, and every extra child loads the timing tests.
  const probe = `${TAG_HELPER}\ntag 'name.0' ("GPU\`r\`nadapters=0")\ntag 'code.0' $null\n${ALIAS_PROBE}\nexit 0`;
  const result = childProcess.spawnSync(PS51, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(probe, 'utf16le').toString('base64')], { encoding: 'utf8', timeout: 60_000 });
  assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
  const lines = result.stdout.split(/\r?\n/).filter((l) => l !== '');
  assert.deepEqual(lines.slice(0, 2), ['name.0=GPU adapters=0', 'code.0='], 'one line per field, the break a space, a null an empty value');
  const state = parseGpuAnswer(`${lines.slice(2).join('\n')}\nadapters=0\n`, AT);
  assert.equal(state.kind, 'read', JSON.stringify(state));
  const pwsh = state.kind === 'read' ? state.pwsh : undefined;
  assert.ok(pwsh !== undefined && ['alias', 'file', 'absent'].includes(pwsh.kind), JSON.stringify(pwsh));
  assert.equal(pwsh?.path.toLowerCase(), aliasPwsh(process.env)?.toLowerCase(), 'the shell folder API and LOCALAPPDATA name the same file');
});

// ---- D4: the outcome, worded from what Windows says AFTER ----

test('every exit and every re-read code is its own sentence: enabled → code 0; still 22; another code → restart; declined; nothing done; failed; timed out', () => {
  const adapter = r9700();
  const ran = enableExitOf(exited(0));
  const seven = `PowerShell 7 (${ALIAS_PWSH})`;
  assert.deepEqual(enableSentence(adapter, ran, measured(0), seven), { sentence: `AMD Radeon AI PRO R9700 is enabled — code 0. Start Ollama again so it finds the GPU. Elevated program: ${seven}.`, failed: false }, 'the owner asked which engine ran');
  assert.deepEqual(enableSentence(adapter, ran, measured(22), PS51_LABEL), { sentence: `AMD Radeon AI PRO R9700 is still disabled (code 22). Elevated program: ${PS51_LABEL}.`, failed: true });
  assert.match(enableSentence(adapter, ran, measured(43), seven).sentence, /enabled, but still not working \(code 43: Windows stopped it because it reported problems\) — restart Windows/);
  assert.deepEqual(enableSentence(adapter, enableExitOf(exited(EXIT.declined)), measured(), seven), { sentence: `Nothing was changed: the UAC prompt was declined. Elevated program: ${seven}.`, failed: false });
  assert.match(enableSentence(adapter, enableExitOf(exited(ENABLE_EXIT.notDisabled)), measured(0), seven).sentence, /^Nothing was done: that device is no longer disabled\. .*code 0/);
  assert.match(enableSentence(adapter, enableExitOf(exited(ENABLE_EXIT.enableFailed)), measured(), seven).sentence, /Enable-PnpDevice failed/);
  assert.match(enableSentence(adapter, enableExitOf(exited(EXIT.launchFailed)), measured(), seven).sentence, /could not be started/);
  assert.match(enableSentence(adapter, enableExitOf({ kind: 'timedOut', timeoutMs: 120_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }), measured(), seven).sentence, /120 s \(wslCare\.timeouts\.gpuEnableSeconds\)/);
  assert.match(enableSentence(adapter, ran, { kind: 'unknown', reason: 'the display-adapter query exited 1' }, seven).sentence, /could not be read/);
  assert.equal(enableSentence(adapter, ran, measured(0), '').sentence.includes('Elevated program'), false, 'no PowerShell at all: no engine named');
});

function enableDeps(over: Partial<EnableDeps> & { readonly answers?: ProcessResult; readonly after?: GpuState } = {}): EnableDeps & { readonly log: string[]; readonly store: MemoryStore } {
  const log: string[] = [];
  const store = new MemoryStore();
  return {
    env: ENV, fileInfo: files({}), pwshAlias: undefined, timeoutMs: () => 120_000, settleSeconds: () => 3,
    confirm: () => { log.push('confirm'); return Promise.resolve(true); },
    run: (request) => { log.push(`run ${request.file}`); log.push(`pending ${store.get(GPU_PENDING_KEY) === undefined ? 'absent' : 'written'}`); return Promise.resolve(over.answers ?? exited(0)); },
    report: (message, failed) => { log.push(`${failed ? 'error' : 'info'}: ${message}`); },
    durable: store, nowUtcMs: () => AT,
    reread: () => { log.push('reread'); return Promise.resolve(over.after ?? measured(0)); },
    log, store, ...over,
  };
}

test('the order that makes it safe: the modal, THEN the pending record, THEN the one elevated run, THEN Windows read again, THEN the sentence', async () => {
  const deps = enableDeps();
  assert.equal(await enableGpu(r9700(), deps), 'ran');
  assert.deepEqual(deps.log, ['confirm', 'run C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe', 'pending written', 'reread', `info: AMD Radeon AI PRO R9700 is enabled — code 0. Start Ollama again so it finds the GPU. Elevated program: ${PS51_LABEL}.`]);
  assert.equal(deps.store.get(GPU_PENDING_KEY), undefined, 'cleared once the launcher answered');
});

test('a decline starts nothing and writes nothing; an adapter not at 22 is not offered; a standing enable blocks a second', async () => {
  const declined = enableDeps({ confirm: () => Promise.resolve(false) });
  assert.equal(await enableGpu(r9700(), declined), 'declined');
  assert.deepEqual(declined.log, []);
  assert.equal(declined.store.values.size, 0);

  const working = enableDeps();
  assert.equal(await enableGpu(r9700(measured(0)), working), 'notOffered');
  assert.ok(!working.log.some((l) => l.startsWith('run') || l === 'confirm'));

  const busy = enableDeps();
  await busy.store.update(GPU_PENDING_KEY, { name: 'x', id: R9700_ID, startedAtUtcMs: AT, deadlineUtcMs: AT + 60_000 });
  assert.equal(await enableGpu(r9700(), busy), 'busy');
  assert.ok(!busy.log.some((l) => l.startsWith('run') || l === 'confirm'));
});

test('after a TIMEOUT the pending record stands until its deadline (the UAC prompt may still be open), then it is swept', async () => {
  const deps = enableDeps({ answers: { kind: 'timedOut', timeoutMs: 120_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, after: measured(22) });
  await enableGpu(r9700(), deps);
  const pending = readGpuPending(deps.store, AT + 1);
  assert.equal(pending?.id, R9700_ID);
  assert.equal(pending?.deadlineUtcMs, AT + 120_000 + 60_000);
  await sweepGpuPending(deps.store, AT + 120_000 + 60_001);
  assert.equal(deps.store.get(GPU_PENDING_KEY), undefined, 'never stuck on "waiting"');
});

test('the modal shows the exact elevated script and names the device and its id', () => {
  const prompt = enablePrompt(r9700(), 3, 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe');
  assert.equal(prompt.message, 'Enable AMD Radeon AI PRO R9700?');
  assert.ok(prompt.detail.includes(enableScript(R9700_ID, 3)));
  assert.ok(prompt.detail.includes(R9700_ID));
  assert.equal(prompt.confirm, 'Run it (one UAC prompt follows)');
});

// ---- D6: the page's message ----

test('the page may ask for an enable only by an INDEX below the adapter cap and the READ it saw (a time stamp) — never an id, a name or a script', () => {
  assert.deepEqual(parsePageMessage({ type: 'enableGpu', index: 1, readAt: AT }), { type: 'enableGpu', index: 1, readAt: AT });
  for (const raw of [{ type: 'enableGpu' }, { type: 'enableGpu', index: 1 }, { type: 'enableGpu', index: MAX_ADAPTERS, readAt: AT }, { type: 'enableGpu', index: -1, readAt: AT }, { type: 'enableGpu', index: 1.5, readAt: AT }, { type: 'enableGpu', index: '1', readAt: AT }, { type: 'enableGpu', index: 1, readAt: -1 }, { type: 'enableGpu', index: 1, readAt: 1.5 }, { type: 'enableGpu', index: 1, readAt: 'now' }, { type: 'enableGpu', index: 1, readAt: AT, id: R9700_ID }, { type: 'enableGpu', id: R9700_ID }]) {
    assert.equal(parsePageMessage(raw), undefined, JSON.stringify(raw));
  }
});

test('the command offers exactly the adapters at 22 with an id it passes', () => {
  assert.deepEqual(enableChoices(measured()), [{ index: 1, label: 'AMD Radeon AI PRO R9700' }]);
  assert.deepEqual(enableChoices(measured(0)), []);
  assert.deepEqual(enableChoices(NOT_ASKED), []);
});

test('code round: with no choice the palette says WHY — a failed check is its reason, never "no GPU is disabled"', () => {
  assert.deepEqual(noChoiceMessage({ kind: 'unknown', reason: 'the display-adapter query exited 1' }), 'Whether a GPU is disabled could not be checked: the display-adapter query exited 1.');
  assert.equal(noChoiceMessage(measured(0)), NONE_DISABLED);
});

test('code round: Ollama\'s log that cannot be read says so on the consequence; an absent one says nothing', () => {
  const [unreadable] = gpuView(measured(), { kind: 'unreadable', reason: 'EACCES' }, undefined, () => '').alerts;
  assert.match(unreadable?.consequence ?? '', /Ollama's log could not be read \(EACCES\)\.$/);
  const [absent] = gpuView(measured(), { kind: 'absent' }, undefined, () => '').alerts;
  assert.equal(absent?.consequence, 'Programs that use this GPU — local models such as Ollama — run on the CPU or do not start.');
});

test('code round: the wait after Enable-PnpDevice is the setting\'s number, not a literal in the script', () => {
  assert.match(enableScript(R9700_ID, 5), /\nStart-Sleep -Seconds 5\n/);
  assert.match(enableScript(R9700_ID, 0), /\nStart-Sleep -Seconds 0\n/);
});

// ---- D2: Ollama's last word ----

const OLLAMA_CPU = 'time=2026-10-10T11:49:08.333+02:00 level=INFO source=types.go:50 msg="inference compute" id=cpu library=cpu compute="" name=cpu description=cpu libdirs=ollama driver="" pci_id="" type="" total="91.6 GiB" available="44.9 GiB"';
const OLLAMA_ROCM = 'time=2026-10-09T11:17:57.010+02:00 level=INFO source=types.go:32 msg="inference compute" id=0 filter_id=0 library=ROCm compute=gfx1201 name=ROCm0 description="AMD Radeon AI PRO R9700" libdirs=ollama,rocm_v7_1 driver=0.0';

test('Ollama\'s LAST inference-compute line decides: the CPU, or ROCm on the card — read from the measured lines', () => {
  assert.deepEqual(lastChoice(`${OLLAMA_ROCM}\n${OLLAMA_CPU}\n`), { library: 'cpu', device: '', time: '2026-10-10T11:49:08.333+02:00' });
  const rocm = lastChoice(`${OLLAMA_CPU}\nsomething else\n${OLLAMA_ROCM}`);
  assert.deepEqual(rocm, { library: 'ROCm', device: 'AMD Radeon AI PRO R9700', time: '2026-10-09T11:17:57.010+02:00' });
  assert.equal(choiceSentence(rocm as NonNullable<typeof rocm>), 'Ollama\'s last start (2026-10-09T11:17:57.010+02:00) chose ROCm on AMD Radeon AI PRO R9700.');
  assert.equal(lastChoice('level=INFO msg="listening"\n'), undefined);
  assert.equal(lastChoice(OLLAMA_CPU.slice(30))?.library, 'cpu', 'a tail cut before the time still names the library');
});

test('the tail is bounded, a missing log is no line, and the path is Ollama\'s under LOCALAPPDATA', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gpu-ollama-'));
  try {
    const file = path.join(dir, 'server.log');
    fs.writeFileSync(file, `${'x'.repeat(10_000)}\n${OLLAMA_CPU}\n`);
    const tail = readTail(file, 64);
    assert.equal(tail.kind === 'text' ? tail.text.length : -1, 64);
    assert.deepEqual(readTail(path.join(dir, 'absent.log'), 64), { kind: 'absent' });
    const whole = readTail(file, 4096);
    assert.equal(lastChoice(whole.kind === 'text' ? whole.text : '')?.library, 'cpu');
    // Code round: a log that EXISTS but cannot be read is not "Ollama is not installed" — it says why.
    const unreadable = readTail(dir, 64);
    assert.equal(unreadable.kind, 'unreadable');
    assert.match(unreadable.kind === 'unreadable' ? unreadable.reason : '', /not a file|EISDIR/);
    const denied = Object.assign(new Error('denied'), { code: 'EACCES' });
    assert.deepEqual(readError(denied), { kind: 'unreadable', reason: 'EACCES' }, 'a refused read is not "absent"');
    assert.deepEqual(readError(Object.assign(new Error('gone'), { code: 'ENOENT' })), { kind: 'absent' });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  assert.equal(ollamaLogPath(ENV), 'C:\\Users\\user\\AppData\\Local\\Ollama\\server.log');
  assert.equal(ollamaLogPath({}), undefined);
});

// ---- the host ----

function hostDeps(over: Partial<GpuHostDeps> = {}): GpuHostDeps & { readonly recorder: ReturnType<typeof newGpuRecorder>; readonly timers: number[]; readonly deadlines: number[] } {
  const recorder = newGpuRecorder();
  recorder.queryResult = exited(0, MEASURED);
  const timers: number[] = [];
  const deadlines: number[] = [];
  return {
    env: ENV, platform: 'win32', fileInfo: files({}), queryTimeoutMs: () => 20_000, enableTimeoutMs: () => 120_000, enableSettleSeconds: () => 3, ollamaTailBytes: () => 4096, checkMinutes: () => 15,
    ui: { confirm: (p) => { recorder.prompts.push(p); return Promise.resolve(recorder.answer); }, run: (r) => { recorder.requests.push(r); return Promise.resolve(recorder.result); }, query: (r) => { recorder.queries.push(r); return Promise.resolve(recorder.queryResult); }, report: (m, f) => { recorder.reports.push({ message: m, failed: f }); } },
    durable: new MemoryStore(), nowUtcMs: () => AT, every: (ms) => { timers.push(ms); return () => undefined; }, after: (ms) => { deadlines.push(ms); return () => undefined; }, formatTime: () => '14:31', readTail: () => ({ kind: 'text', text: `${OLLAMA_CPU}\n` }),
    recorder, timers, deadlines, ...over,
  };
}

test('the host reads at start, sets its timer from the setting, and its view carries the alert and Ollama\'s line', async () => {
  const deps = hostDeps();
  const host = new GpuHost(deps);
  host.start();
  await host.refresh();
  assert.ok(deps.recorder.queries.length >= 1);
  assert.deepEqual(deps.timers, [15 * 60_000]);
  const [alert] = host.view().alerts;
  assert.match(alert?.consequence ?? '', /chose the CPU/);
  assert.equal(host.barMark().text, ' · GPU off');
});

test('the host\'s enable acts on ITS reading at the index — the recorded request carries the R9700\'s id; a decline records none', async () => {
  const deps = hostDeps();
  const host = new GpuHost(deps);
  await host.refresh();
  deps.recorder.answer = false;
  await host.enable(1, AT);
  assert.equal(deps.recorder.requests.length, 0, 'declined: nothing ran');
  deps.recorder.answer = true;
  deps.recorder.result = exited(0);
  deps.recorder.queryResult = exited(0, MEASURED.replace('code.1=22', 'code.1=0'));
  await host.enable(1, AT);
  assert.equal(deps.recorder.requests.length, 1);
  assert.ok(revealed(deps.recorder.requests[0]?.args ?? []).includes(R9700_ID));
  assert.deepEqual(deps.recorder.reports.at(-1), { message: `AMD Radeon AI PRO R9700 is enabled — code 0. Start Ollama again so it finds the GPU. Elevated program: ${PS51_LABEL}.`, failed: false });
  assert.deepEqual(host.view().alerts, [], 'Windows now says code 0: the alert is gone');
});

test('owner 2026-10-10: the host elevates the Store alias only when ITS OWN read of Windows reported it as an alias at the path it computes — the modal and the outcome name PowerShell 7', async () => {
  const deps = hostDeps();
  deps.recorder.queryResult = exited(0, `${MEASURED}pwsh.kind=alias\r\npwsh.path=${ALIAS_PWSH.toUpperCase()}\r\n`);
  const host = new GpuHost(deps);
  await host.refresh();
  deps.recorder.answer = true;
  deps.recorder.result = exited(0);
  await host.enable(1, AT);
  assert.match(deps.recorder.prompts.at(-1)?.detail ?? '', /The elevated program: PowerShell 7 \(C:\\Users\\user\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh\.exe\)/);
  assert.match(deps.recorder.requests[0]?.args[3] ?? '', /ProcessStartInfo -ArgumentList 'C:\\Users\\user\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh\.exe'/);
  assert.match(deps.recorder.reports.at(-1)?.message ?? '', /Elevated program: PowerShell 7 \(C:\\Users\\user\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh\.exe\)\.$/);

  const file = hostDeps();
  file.recorder.queryResult = exited(0, `${MEASURED}pwsh.kind=file\r\npwsh.path=${ALIAS_PWSH}\r\n`);
  const planted = new GpuHost(file);
  await planted.refresh();
  file.recorder.answer = true;
  await planted.enable(1, AT);
  assert.match(file.recorder.prompts.at(-1)?.detail ?? '', /The elevated program: Windows PowerShell 5\.1/, 'a real file where the alias should be: 5.1 runs');
});

test('off Windows the host is silent: no query, no alert, no mark, no timer', async () => {
  const deps = hostDeps({ platform: 'linux' });
  const host = new GpuHost(deps);
  host.start();
  await host.refresh();
  assert.equal(deps.recorder.queries.length, 0);
  assert.deepEqual(host.view(), { alerts: [], line: '' });
  assert.equal(host.barMark().text, '');
  assert.deepEqual(deps.timers, []);
});

test('a pending enable whose device Windows already shows enabled is cleared on the next read (a reload mid-run)', async () => {
  const deps = hostDeps();
  await deps.durable.update(GPU_PENDING_KEY, { name: 'AMD Radeon AI PRO R9700', id: R9700_ID, startedAtUtcMs: AT, deadlineUtcMs: AT + 180_000 });
  deps.recorder.queryResult = exited(0, MEASURED.replace('code.1=22', 'code.1=0'));
  await new GpuHost(deps).refresh();
  assert.equal(deps.durable.get(GPU_PENDING_KEY), undefined);
});

test('while an enable stands, the alert says it is waiting and the button is disabled', () => {
  const [alert] = gpuView(measured(), undefined, { name: 'AMD Radeon AI PRO R9700' }, () => '').alerts;
  assert.match(alert?.fix ?? '', /^Waiting for the elevated PowerShell that enables AMD Radeon AI PRO R9700/);
  assert.equal(alert?.button?.enabled, false);
});

test('PowerShell\'s own parser reads the query, the enable and its launcher without an error (parsed, never executed: the enable would change a device)', { skip: process.platform !== 'win32' ? 'Windows PowerShell exists on the Windows leg only' : false }, () => {
  const powerShell = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gpu-parse-'));
  try {
    const request = enableRequest(ENV, R9700_ID, 120_000, files({}), undefined, 3) as ProcessRequest;
    for (const [name, text] of [['query.ps1', QUERY_SCRIPT], ['enable.ps1', enableScript(R9700_ID, 3)], ['outer.ps1', request.args[3] ?? '']] as const) {
      fs.writeFileSync(path.join(dir, name), text, 'utf8');
    }
    const check = "$e=$null; $t=$null; foreach ($f in 'query.ps1','enable.ps1','outer.ps1') { [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $args[0] $f), [ref]$t, [ref]$e); if ($e.Count -gt 0) { Write-Output ($f + ': ' + $e[0].Message); exit 1 } }; exit 0";
    const result = childProcess.spawnSync(powerShell, ['-NoProfile', '-NonInteractive', '-Command', `& { ${check} }`, dir], { encoding: 'utf8', timeout: 60_000 });
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('code round: the HOST carries an unreadable Ollama log to the alert (not only the pure view)', async () => {
  const deps = hostDeps({ readTail: () => ({ kind: 'unreadable', reason: 'EACCES' }) });
  const host = new GpuHost(deps);
  await host.refresh();
  assert.match(host.view().alerts[0]?.consequence ?? '', /Ollama's log could not be read \(EACCES\)\.$/);
});

// ---- own review (Fable, 2026-10-10): the elevated path's minor findings ----

test('own review M1: the modal names the PROGRAM it elevates, and the elevated script finds System32 without the environment', () => {
  const pwsh = 'C:\\Program Files\\PowerShell\\7\\pwsh.exe';
  assert.ok(enablePrompt(r9700(), 3, pwsh).detail.includes(pwsh), 'the modal names the elevated program');
  const script = enableScript(R9700_ID, 3);
  assert.ok(script.includes('[Environment]::SystemDirectory'), 'System32 from GetSystemDirectory, not $env:SystemRoot');
  assert.equal(script.includes('$env:SystemRoot'), false);
});

test('own review M6 / code round 2: a device NAME carrying a newline can shadow nothing — a tag answered twice is an explicit UNKNOWN, never a first-wins read and never an all-clear', () => {
  const shadow = ['adapter=0', `id.0=${R9700_ID}`, 'code.0=22', 'driver.0=1', 'name.0=Evil', 'id.0=PCI\\OTHER', 'code.0=0', 'adapters=1'].join('\n');
  assert.deepEqual(parseGpuAnswer(shadow, AT), { kind: 'unknown', reason: 'the display-adapter query answered a tag twice (id.0)' });
  // The reviewer's case: `GPU\nadapters=0` printed before the real count read as "no display adapter reported — working".
  const spoof = ['adapter=0', `id.0=${R9700_ID}`, 'code.0=22', 'driver.0=1', 'name.0=GPU', 'adapters=0', 'adapters=1'].join('\n');
  assert.deepEqual(parseGpuAnswer(spoof, AT), { kind: 'unknown', reason: 'the display-adapter query answered a tag twice (adapters)' });
  assert.equal(gpuView(parseGpuAnswer(spoof, AT), undefined, undefined, () => '').alerts[0]?.level, 'unknown', 'shown as a check that could not run, not as fine');
  const order = ['id.', 'code.', 'driver.', 'name.'].map((t) => QUERY_SCRIPT.indexOf(`tag ('${t}'`));
  assert.ok(order.every((at, i) => at >= 0 && (i === 0 || at > (order[i - 1] ?? -1))), `the query prints the name LAST: ${order.join(',')}`);
});

test('code round 2: a button rendered from an OLDER read acts on nothing — the host refuses the stale index without a modal, reads Windows again, and says so', async () => {
  let now = AT;
  const deps = hostDeps({ nowUtcMs: () => now });
  const host = new GpuHost(deps);
  await host.refresh();
  now = AT + 5_000;
  await host.refresh();
  deps.recorder.answer = true;
  const queries = deps.recorder.queries.length;
  assert.equal(await host.enable(1, AT), 'stale', 'the token of the first read no longer matches');
  assert.equal(deps.recorder.prompts.length, 0, 'no modal');
  assert.equal(deps.recorder.requests.length, 0, 'nothing ran');
  assert.ok(deps.recorder.queries.length > queries, 'Windows was read again');
  assert.match(deps.recorder.reports.at(-1)?.message ?? '', /read again since the panel showed that button/);
  assert.equal(await host.enable(1, AT + 5_000), 'ran', 'the current read\'s token is accepted');
  assert.equal(deps.recorder.prompts.length, 1);
});

test('code round 2: a window that confirms while THIS modal is open wins — the pending record is read AGAIN after the modal, and this window starts nothing', async () => {
  const store = new MemoryStore();
  const other = { name: 'the other window', id: R9700_ID, startedAtUtcMs: AT, deadlineUtcMs: AT + 60_000 };
  const deps = enableDeps({ durable: store, confirm: async () => { await store.update(GPU_PENDING_KEY, other); return true; } });
  assert.equal(await enableGpu(r9700(), deps), 'busy');
  assert.ok(!deps.log.some((l) => l.startsWith('run')), 'no elevated run from this window');
  assert.deepEqual(store.get(GPU_PENDING_KEY), other, 'the other window\'s record stands, not overwritten');
  assert.match(deps.log.at(-1) ?? '', /^info: An enable of the other window is already waiting/);
});

test('own review: a settle value that is not a number becomes 0 in the elevated script, never NaN', () => {
  assert.match(enableScript(R9700_ID, Number.NaN), /\nStart-Sleep -Seconds 0\n/);
});

test('own review M3: the palette says an enable is already waiting, instead of doing nothing', async () => {
  const busy = enableDeps();
  await busy.store.update(GPU_PENDING_KEY, { name: 'AMD Radeon AI PRO R9700', id: R9700_ID, startedAtUtcMs: AT, deadlineUtcMs: AT + 60_000 });
  assert.equal(await enableGpu(r9700(), busy), 'busy');
  assert.match(busy.log.at(-1) ?? '', /^info: An enable of AMD Radeon AI PRO R9700 is already waiting/);
});

test('own review M2: after a timed-out enable the host schedules ONE re-render at the pending deadline, so "Waiting…" ends on screen', async () => {
  const deps = hostDeps();
  const host = new GpuHost(deps);
  await host.refresh();
  deps.recorder.answer = true;
  deps.recorder.result = { kind: 'timedOut', timeoutMs: 120_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
  await host.enable(1, AT);
  assert.deepEqual(deps.deadlines, [120_000 + 60_000 + 1]);
});

test('own review M4: a press on an adapter no longer at 22 reads Windows again, as its sentence says', async () => {
  const deps = hostDeps();
  const host = new GpuHost(deps);
  await host.refresh();
  const queries = deps.recorder.queries.length;
  await host.enable(0, AT);
  assert.ok(deps.recorder.queries.length > queries, 'Windows was read again');
  assert.match(deps.recorder.reports.at(-1)?.message ?? '', /Windows is read again/);
});
