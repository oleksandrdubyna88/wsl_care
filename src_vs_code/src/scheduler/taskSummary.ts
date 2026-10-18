/**
 * The CANONICAL summary of a scheduled task's definition (PLAN_windows_time_task.md D8, PLAN_windows_runner.md §3.4, §14 WR-D1,
 * WR-E2) — ONE format for every task this extension reads: what PowerShell prints of a REGISTERED definition (`SUMMARY_FUNCTION`),
 * the duration spellings Task Scheduler normalises (`durationSeconds`), and the comparison that is blind to exactly those
 * (`summariesMatch`). The guard's expected summary (`windowsTime/guardTask.ts`) and the daemon's `tasks render` summary are each
 * held equal to it, so neither side owns the format: it lives in the peer both consume.
 */

/**
 * The PowerShell that prints a definition's canonical summary — ONE text, used by the status query over the REGISTERED task
 * and by the Windows-leg tests over Task Scheduler's in-memory parse of a task's XML, so an expected summary cannot drift from
 * what Task Scheduler reads back. Reads only; the principal is translated to its SID (an account NAME is localised).
 */
export const SUMMARY_FUNCTION = [
  'function Get-WslCareSummary($Definition) {',
  '$p = $Definition.Principal; $sid = [string]$p.UserId;',
  'try { $sid = (New-Object System.Security.Principal.NTAccount($p.UserId)).Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $sid = [string]$p.UserId };',
  "'principal=' + $sid + ' logon=' + $p.LogonType + ' level=' + $p.RunLevel;",
  'foreach ($t in $Definition.Triggers) { switch ($t.Type) {',
  "0 { 'trigger=event enabled=' + $t.Enabled + ' delay=' + $t.Delay + ' subscription=' + $t.Subscription }",
  "1 { 'trigger=time enabled=' + $t.Enabled + ' interval=' + $t.Repetition.Interval }",
  "8 { 'trigger=boot enabled=' + $t.Enabled + ' delay=' + $t.Delay }",
  "9 { 'trigger=logon enabled=' + $t.Enabled + ' delay=' + $t.Delay + ' interval=' + $t.Repetition.Interval }",
  "default { 'trigger=' + $t.Type } } };",
  "$s = $Definition.Settings; 'settings limit=' + $s.ExecutionTimeLimit + ' instances=' + $s.MultipleInstances + ' whenAvailable=' + $s.StartWhenAvailable + ' battery=' + $s.DisallowStartIfOnBatteries + ' enabled=' + $s.Enabled;",
  "foreach ($a in $Definition.Actions) { 'action type=' + $a.Type + ' path=' + $a.Path + ' args=' + $a.Arguments }",
  '}',
].join('\n');

/** An ISO 8601 duration as Task Scheduler writes one — days, then hours, minutes and whole seconds: `P1D`, `PT1M`, `PT1M30S`. */
const DURATION = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$/;

const SECONDS_PER = { day: 86_400, hour: 3_600, minute: 60 } as const;

/**
 * A duration's length in seconds, or `undefined` for anything that is no duration of that shape (the empty "no delay" among
 * them). Task Scheduler stores a registered trigger's `PT60S` as `PT1M` (measured on the owner's machine, 2026-10-10), so the
 * summary's duration fields are compared by this VALUE, never by their spelling (`summariesMatch`).
 */
export function durationSeconds(text: string): number | undefined {
  const match = DURATION.exec(text);
  if (match === null || text === 'P' || text.endsWith('T')) {
    return undefined;
  }
  const [days, hours, minutes, seconds] = match.slice(1).map((part) => Number(part ?? 0));

  return days * SECONDS_PER.day + hours * SECONDS_PER.hour + minutes * SECONDS_PER.minute + seconds;
}

/** The summary fields that hold a duration: `trigger=boot enabled=True delay=PT1M`, `trigger=time enabled=True interval=PT4H`,
 * `settings limit=PT5M …`. */
const DURATION_FIELDS = ['delay=', 'interval=', 'limit='] as const;

/** The summary fields whose value is FREE TEXT and runs to the end of its line (`SUMMARY_FUNCTION`): from the first of them on,
 * nothing is read as a duration — a subscription or an action argument is compared as text (coai plan round 990e7d9a), wherever
 * the structured fields before it stand (code round: tied to the line's schema, not to a token count). */
const FREE_TEXT_FIELDS = ['subscription=', 'path=', 'args='] as const;

/** One summary token with a duration field's value as its length in seconds (`delay=60s`); any other token as it is. */
function canonicalToken(token: string): string {
  const field = DURATION_FIELDS.find((f) => token.startsWith(f));
  const seconds = field === undefined ? undefined : durationSeconds(token.slice(field.length));

  return field === undefined || seconds === undefined ? token : `${field}${seconds}s`;
}

function canonicalLine(line: string): string {
  const tokens = line.split(' ');
  const freeText = tokens.findIndex((token) => FREE_TEXT_FIELDS.some((f) => token.startsWith(f)));
  const structured = freeText === -1 ? tokens.length : freeText;

  return tokens.map((token, i) => (i < structured ? canonicalToken(token) : token)).join(' ');
}

/**
 * Whether two summaries describe the same task: line for line, byte for byte — except the duration fields, compared by VALUE,
 * because Task Scheduler stores a registered `PT60S` as `PT1M` (2026-10-10: every real install of 0.3.0 read as "not as the
 * current settings would install it"). A field whose value is no duration is compared as text.
 */
export function summariesMatch(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((line, i) => canonicalLine(line) === canonicalLine(b[i] ?? ''));
}
