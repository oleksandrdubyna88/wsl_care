import type { RowKind } from './fieldMap';
import { minuteOf } from '../text/format';
import { age, percent, safeText } from './format';
import { amount, fromRun, gb, gib, isJson, list, num, reading, sized, text, type Json } from './read';
import type { RowState, ViewLevel } from './view';

/**
 * One renderer per row kind of the field map: the value at the row's path in, what the row shows out. The view model
 * (`viewModel.ts`) has already handled the three cases that are not a figure — a missing field, an
 * `available: false` figure, a failed verb — so a renderer sees a value that is there.
 */

export interface Rendered {
  readonly value: string;
  readonly state?: RowState;
  readonly level?: ViewLevel;
  readonly headers?: readonly string[];
  readonly items?: readonly (readonly string[])[];
}

type Renderer = (value: unknown) => Rendered;

const LEVELS: readonly ViewLevel[] = ['ok', 'warn', 'critical', 'unknown'];

function levelOf(value: unknown): ViewLevel {
  return LEVELS.find((level) => level === value) ?? 'unknown';
}

/** A doctor check's state as a level. */
const CHECK_LEVEL: Readonly<Record<string, ViewLevel>> = { ok: 'ok', problem: 'warn', unknown: 'unknown', notChecked: 'none' };

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

function processLine(p: Json): string[] {
  return [text(p, 'pid'), text(p, 'user'), text(p, 'name'), gib(amount(p, 'heldBytes')), reading(p.ageSeconds, age), text(p, 'family'), text(p, 'commandLine')];
}

function processes(value: unknown): Rendered {
  const all = list(value);
  return { value: plural(all.length, 'process', 'processes'), headers: ['PID', 'User', 'Name', 'Held', 'Age', 'Family', 'Command line'], items: all.map(processLine) };
}

function families(value: unknown): Rendered {
  const all = list(value);
  return { value: plural(all.length, 'family', 'families'), headers: ['Family', 'Processes', 'Held'], items: all.map((f) => [text(f, 'name'), text(f, 'count'), gib(amount(f, 'heldBytes'))]) };
}

function containerStats(value: unknown): Rendered {
  const all = list(value, 'containers');
  return {
    value: `${plural(all.length, 'container', 'containers')} — ${fromRun(value)}`,
    headers: ['Container', 'Memory', 'CPU'],
    items: all.map((c) => [text(c, 'name'), gib(amount(c, 'memoryBytes')), percent(amount(c, 'cpuPercent'))]),
  };
}

function fragmentation(value: unknown): Rendered {
  return { value: `order ≥ 7: ${text(value, 'blocksOrder7Plus')} blocks (${gib(amount(value, 'bytesOrder7Plus'))}), order ≥ 4: ${text(value, 'blocksOrder4Plus')} blocks — zone ${text(value, 'zone')}` };
}

function unattributed(value: unknown): Rendered {
  const bytes = num(value, 'bytes');
  if (text(value, 'state') === 'remainder' && bytes !== undefined) {
    return { value: gib(bytes) };
  }

  return { value: `${text(value, 'state')} — ${text(value, 'reason')}`, state: 'unavailable' };
}

function disk(value: unknown): Rendered {
  return { value: `${percent(amount(value, 'usedPercent'))} used — ${gb(amount(value, 'usedBytes'))} of ${gb(amount(value, 'totalBytes'))}, ${gb(amount(value, 'availableBytes'))} free` };
}

function folders(value: unknown): Rendered {
  const all = list(value, 'folders');
  return {
    value: `${plural(all.length, 'folder', 'folders')} — ${fromRun(value)}`,
    headers: ['Folder', 'Path', 'Size', 'Growth since the previous walk'],
    items: all.map((f) => [text(f, 'id'), text(f, 'path'), sized(f.size, gb), sized(f.growthSincePrevious, gb)]),
  };
}

function containersNow(value: unknown): Rendered {
  return {
    value: `${text(value, 'count')} running — ${gib(amount(value, 'memoryCurrentTotal'))} memory.current`,
    headers: ['Container', 'memory.current', 'anon + shmem'],
    items: list(value, 'items').map((c) => [text(c, 'id').slice(0, 12), sized(c.memoryCurrent), sized(c.anonShmem)]),
  };
}

function dockerTotals(value: unknown): Rendered {
  const types = list(value, 'types');
  const reclaimable = types.reduce((sum, t) => sum + (amount(t.reclaimable, 'bytes')), 0);
  return {
    value: `${gb(reclaimable)} reclaimable in total`,
    headers: ['Type', 'Total', 'Active', 'Size', 'Reclaimable'],
    items: types.map((t) => [text(t, 'type'), text(t, 'totalCount'), text(t, 'active'), sized(t.size, gb), sized(t.reclaimable, gb)]),
  };
}

function starts(value: unknown): Rendered {
  const gaps = list(value, 'gaps');
  const complete = isJson(value) && value.complete === true;
  return {
    value: `${text(value, 'starts')} starts, ${text(value, 'testcontainers')} Testcontainers${complete ? '' : ` — partial (${plural(gaps.length, 'gap', 'gaps')})`}`,
    headers: ['Kind', 'What', 'Detail'],
    items: [
      ...list(value, 'topImages').map((i) => ['image', text(i, 'image'), text(i, 'starts')]),
      ...gaps.map((g) => ['gap', `${text(g, 'from')} – ${text(g, 'to')}`, text(g, 'reason')]),
    ],
  };
}

function cleanupLine(row: Json): string[] {
  const head = [text(row, 'id'), text(row, 'what')];
  const tail = [row.auto === true ? 'on' : 'off', text(row, 'basis')];
  if (row.available === false) {
    return [...head, `unavailable — ${text(row, 'reason')}`, '—', ...tail];
  }

  return [...head, text(row, 'count'), gb(amount(row, 'reclaimableBytes')), ...tail];
}

function cleanupRows(value: unknown): Rendered {
  const rows = list(value);
  const total = rows.reduce((sum, r) => sum + (amount(r, 'reclaimableBytes')), 0);
  return {
    value: `${plural(rows.length, 'row', 'rows')}, ${gb(total)} reclaimable — each row cleans after a preview and your confirmation`,
    headers: ['Row', 'What', 'Count', 'Reclaimable', 'Auto', 'Basis'],
    items: rows.map(cleanupLine),
  };
}

function keptVolumes(value: unknown): Rendered {
  return {
    value: `${text(value, 'count')} volumes, ${gb(amount(value, 'bytes'))} — kept; a person decides per volume`,
    headers: ['Volume', 'Size'],
    items: list(value, 'volumes').map((v) => [text(v, 'name'), sized(v.size, gb)]),
  };
}

function unboundedLogs(value: unknown): Rendered {
  return {
    value: plural(amount(value, 'count'), 'container', 'containers'),
    headers: ['Container', 'State', 'Log size'],
    items: list(value, 'containers').map((c) => [text(c, 'container'), text(c, 'state'), sized(c.logSize, gb)]),
  };
}

function buildkit(value: unknown): Rendered {
  const all = list(value, 'leftovers');
  return {
    value: all.length === 0 ? 'none' : plural(all.length, 'leftover', 'leftovers'),
    headers: ['Name', 'Kind', 'State', 'Size'],
    items: all.map((l) => [text(l, 'name'), text(l, 'kind'), text(l, 'state'), sized(l.size, gb)]),
  };
}

function check(value: unknown): Rendered {
  return { value: `${text(value, 'state')} — ${text(value, 'detail')}`, level: CHECK_LEVEL[text(value, 'state')] ?? 'unknown' };
}

function checks(value: unknown): Rendered {
  const all = list(value);
  const count = (state: string): number => all.filter((c) => c.state === state).length;
  return {
    value: `${count('ok')} ok, ${count('problem')} problems, ${count('unknown')} unknown, ${count('notChecked')} not checked`,
    level: count('problem') > 0 ? 'warn' : 'ok',
    headers: ['Check', 'State', 'Detail'],
    items: all.map((c) => [text(c, 'id'), text(c, 'state'), text(c, 'detail')]),
  };
}

function versions(value: unknown): Rendered {
  const all = list(value);
  return {
    value: plural(all.length, 'component', 'components'),
    headers: ['Component', 'Version'],
    items: all.map((v) => [text(v, 'component'), v.available === false ? `unavailable — ${text(v, 'reason')}` : text(v, 'version')]),
  };
}

function configError(value: unknown): Rendered {
  const all = list(value);
  if (all.length === 0) {
    return { value: 'none', level: 'ok' };
  }

  return { value: `${plural(all.length, 'error', 'errors')} — the daemon runs observe-only`, level: 'critical', headers: ['File', 'Line', 'Message'], items: all.map((e) => [text(e, 'file'), text(e, 'line'), text(e, 'message')]) };
}

function measured(basis: unknown): string {
  const seconds = num(basis, 'ageSeconds');
  return text(basis, 'source') === 'fullRun' ? `the full run, ${seconds === undefined ? 'age unknown' : age(seconds)}` : 'this sample';
}

function verdicts(value: unknown): Rendered {
  const all = list(value);
  const count = (level: string): number => all.filter((v) => v.level === level).length;
  return {
    value: `${plural(all.length, 'verdict', 'verdicts')}: ${count('warn')} warn, ${count('critical')} critical, ${count('unknown')} unknown`,
    level: count('critical') > 0 ? 'critical' : count('warn') > 0 ? 'warn' : 'ok',
    headers: ['Verdict', 'Level', 'Value', 'Limit', 'Measured by'],
    items: all.map((v) => [text(v, 'id'), text(v, 'level'), text(v, 'value'), text(v, 'limit'), measured(v.basis)]),
  };
}

function verdict(value: unknown): Rendered {
  const fields: Json = isJson(value) ? value : {};
  const reason = text(fields, 'reason', '');
  return { value: `${text(fields, 'value')} — measured by ${measured(fields.basis)}${reason === '' ? '' : ` (${reason})`}`, level: levelOf(fields.level) };
}

/** Who started a run, as `status.lastCleanup.trigger` names it. */
const STARTED_BY: Readonly<Record<string, string>> = { manual: 'from the panel', timer: 'by the timer', cli: 'from a terminal' };

/** `status.lastCleanup` (plan §15j M7): what the newest cleanup freed and removed, who ran it, when. */
function lastCleanup(value: unknown): Rendered {
  const by = STARTED_BY[text(value, 'trigger', '')] ?? `by ${text(value, 'trigger')}`;

  return { value: `freed ${gb(amount(value, 'freedBytes'))}, ${text(value, 'count')} objects — ${by}, run ${text(value, 'runId')}, started ${minuteOf(text(value, 'startedAt', ''), '—')}` };
}

export const RENDERERS: { readonly [K in RowKind]: Renderer } = {
  gib: (v) => ({ value: sized(v) }),
  percent: (v) => ({ value: reading(v, percent) }),
  fragmentation,
  unattributed,
  processes,
  families,
  containerStats,
  disk,
  folders,
  containersNow,
  dockerEngine: (v) => ({ value: `Docker ${text(v, 'serverVersion')} — ${text(v, 'platform')}` }),
  dockerTotals,
  starts,
  cleanupRows,
  keptVolumes,
  unboundedLogs,
  builderGc: (v) => ({ value: isJson(v) && v.present === true ? 'present' : 'absent — the build cache grows until it is pruned' }),
  buildkit,
  productVersion: (v) => ({ value: typeof v === 'string' ? safeText(v) : '—' }),
  healthy: (v) => ({ value: v === true ? 'yes' : 'no', level: v === true ? 'ok' : 'warn' }),
  check,
  configError,
  checks,
  versions,
  verdicts,
  verdict,
  lastCleanup,
};
