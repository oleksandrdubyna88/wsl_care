import { BODY, EXIT, SET_AUTOMATIC } from './windowsTimeFix';

/**
 * The Windows Time guard's scheduled task (PLAN_windows_time_task.md D1, D2, D8): ONE task `\wsl-care\windows-time-guard`
 * that runs as SYSTEM and (re)starts the Windows Time service and resyncs — at startup, at logon, every N hours and when the
 * service logs that it is stopping (Time-Service event 258; the SCM's 7036 is not logged on this build at all —
 * research/2026-10-08_windows_time_guard_trigger.md §1–§2), and when its start type is changed (the SCM's 7040 for
 * `W32Time`, research §6 — the *disabled* of 2026-10-08 undone within `delaySeconds`).
 *
 * <p>Everything here is a PURE function of the settings, so the text a person is shown, the text registered and the text
 * the status query compares against are one computation:</p>
 * <ul>
 *   <li>`guardScript` — the task's action, as ONE line: story 1's start-type line and its start + resync lines, reused
 *       verbatim, with a rate limit on STARTS before them;</li>
 *   <li>`guardTaskXml` — the definition Task Scheduler registers; its action is `powershell.exe -Command <that line>` in
 *       plain text, never a script file a user could edit and never `-EncodedCommand` (an obfuscation indicator in a
 *       SYSTEM task, own review o6);</li>
 *   <li>`guardSummary` — the canonical lines `SUMMARY_FUNCTION` prints for a registered definition; equal → the task is
 *       what the current settings would install (D8 g1/c5/o2).</li>
 * </ul>
 */

export interface GuardOptions {
  /** `wslCare.windowsTime.setAutomaticStart` — the action also sets StartType Automatic (story 1's switch, owner default ON). */
  readonly setAutomaticStart: boolean;
  readonly everyHours: number;
  readonly minMinutesBetweenStarts: number;
  readonly delaySeconds: number;
  readonly timeLimitMinutes: number;
}

export const GUARD_FOLDER = 'wsl-care';
export const GUARD_NAME = 'windows-time-guard';

/** Where the rate limit's stamp lives: HKLM\SOFTWARE, which only administrators and SYSTEM may write (D2). */
export const STAMP_PARENT = 'HKLM:\\SOFTWARE\\wsl-care';
export const STAMP_KEY = `${STAMP_PARENT}\\${GUARD_NAME}`;
export const STAMP_VALUE = 'lastStartUtcTicks';

/**
 * The task's security descriptor: SYSTEM and administrators full, Authenticated Users READ. Measured (research T21):
 * under the root folder's default descriptor an elevated registration's SYSTEM task is not even LISTED to the unelevated
 * reader, so without the read ACE the panel would say "not installed". Requested in the XML; whether Task Scheduler
 * applies it is the receiver's decision, observed by the panel's own read-back after install (`POST_DEPLOY.md`).
 */
export const GUARD_SDDL = 'D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;AU)';

/** The action's program, as Windows' own tasks write theirs: expanded by Task Scheduler in the SYSTEM context. */
export const TASK_POWERSHELL = '%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';

/** The guard's exit codes — story 1's shared ones plus the guard's own (D2, D8 o9). */
export const GUARD_EXIT = {
  done: EXIT.done,
  setAutomaticFailed: EXIT.setAutomaticFailed,
  startFailed: EXIT.startFailed,
  resyncFailed: EXIT.resyncFailed,
  rateLimited: 20,
  stampUnwritable: 21,
  serviceMissing: 22,
} as const;

export const TIME_SERVICE_CHANNEL = 'Microsoft-Windows-Time-Service/Operational';
export const TIME_SERVICE_PROVIDER = 'Microsoft-Windows-Time-Service';
/** *"W32time service is stopping"* — measured to match by provider and number alone (research T8). */
export const STOP_EVENT = 258;

/** The event trigger's subscription, exactly the `QueryList` measured against the recorded stops (research T8). */
export const STOP_QUERY = `<QueryList><Query Id="0" Path="${TIME_SERVICE_CHANNEL}"><Select Path="${TIME_SERVICE_CHANNEL}">*[System[Provider[@Name='${TIME_SERVICE_PROVIDER}'] and EventID=${STOP_EVENT}]]</Select></Query></QueryList>`;

export const SYSTEM_CHANNEL = 'System';
export const SCM_PROVIDER = 'Service Control Manager';
/** *"The start type of the … service was changed from … to …"* — its `param1`..`param3` are localised text. */
export const START_TYPE_EVENT = 7040;
/** `param4` of 7040: the service's KEY name, not localised; the event log compares it case-insensitively (research §6). */
export const TIME_SERVICE_KEY = 'W32Time';

/**
 * The fifth trigger's subscription: a start-type change of the Windows Time service — exactly the `QueryList` measured
 * against the System log (research/2026-10-08_windows_time_guard_trigger.md §6: the four W32Time 7040s and nothing else).
 */
export const START_TYPE_QUERY = `<QueryList><Query Id="0" Path="${SYSTEM_CHANNEL}"><Select Path="${SYSTEM_CHANNEL}">*[System[Provider[@Name='${SCM_PROVIDER}'] and EventID=${START_TYPE_EVENT}] and EventData[Data[@Name='param4']='${TIME_SERVICE_KEY}']]</Select></Query></QueryList>`;

const TICKS_PER_MINUTE = 600_000_000;

/**
 * The FIRST statement of every guard script: PowerShell's modules from `$PSHOME` only. It calls NO command — a cmdlet
 * looked up before this line (`Join-Path`, autoloaded on first use) would be searched for in the user's own, writable
 * `Documents\WindowsPowerShell\Modules` first, inside an elevated or SYSTEM PowerShell (own code review k2). No double
 * quote either, so the task's one-line action survives the command line.
 */
export const MODULES = "$env:PSModulePath = $PSHOME + '\\Modules'";

/** The rate limit's decision as a function of its own, so a test can extract it from the script and run it ALONE. */
export const START_ALLOWED_FUNCTION = 'function Test-WslCareStartAllowed([long]$Now, [long]$Last, [long]$Window) { $since = $Now - $Last; -not (($since -ge 0) -and ($since -lt $Window)) }';

function rateLimitBlock(minutes: number): string {
  const window = minutes * TICKS_PER_MINUTE;

  return [
    "if ($service.Status -ne 'Running') {",
    '$now = [DateTime]::UtcNow.Ticks;',
    `$last = [long]0; try { $last = [long](Get-ItemPropertyValue -Path $key -Name ${STAMP_VALUE} -ErrorAction Stop) } catch { $last = [long]0 };`,
    `if (-not (Test-WslCareStartAllowed $now $last ${window})) { exit ${GUARD_EXIT.rateLimited} };`,
    `try { if (-not (Test-Path -Path $key)) { New-Item -Path $key -Force -ErrorAction Stop | Out-Null }; Set-ItemProperty -Path $key -Name ${STAMP_VALUE} -Value ([string]$now) -ErrorAction Stop } catch { exit ${GUARD_EXIT.stampUnwritable} }`,
    '}',
  ].join(' ');
}

/**
 * Story 1's start-type line, run only when the start type is not already Automatic: the guard's own `Set-Service` is then
 * at most ONE 7040 — the run that 7040 triggers finds Automatic and changes nothing — so the fifth trigger cannot re-fire
 * the task every `delaySeconds` (whether the SCM logs a 7040 for an unchanged type is not measured, so nothing relies on it).
 */
function startTypeLine(): string {
  return `if ($service.StartType -ne 'Automatic') { ${SET_AUTOMATIC} }`;
}

/**
 * The task's action as ONE line (D2, D8 o6/o10): modules from `$PSHOME`; the service must exist (22); the start type FIRST,
 * so a Disabled service is set Automatic even when the start is rate-limited; then — only when the service is not running —
 * at most one start per window, the stamp written BEFORE the start (20 / 21); then story 1's start and resync lines.
 */
export function guardScript(options: GuardOptions): string {
  return [
    MODULES,
    `$key = '${STAMP_KEY}'`,
    START_ALLOWED_FUNCTION,
    '$service = Get-Service -Name w32time -ErrorAction SilentlyContinue',
    `if ($null -eq $service) { exit ${GUARD_EXIT.serviceMissing} }`,
    ...(options.setAutomaticStart ? [startTypeLine()] : []),
    rateLimitBlock(options.minMinutesBetweenStarts),
    ...BODY,
  ].join('; ');
}

/** The action's arguments — exactly what `CreateProcess` hands `powershell.exe`. */
export function guardArguments(options: GuardOptions): string {
  return `-NoProfile -NonInteractive -Command ${guardScript(options)}`;
}

/**
 * What `powershell.exe` makes of an argument text with NO double quote: `CommandLineToArgvW` splits it on whitespace (a
 * backslash is literal unless a quote follows it) and PowerShell joins everything after `-Command` with single spaces. A
 * script survives the trip byte for byte only if it has no double quote, no tab, no newline and no run of two spaces —
 * `guardScript` is held to that (and to no `%`, which Task Scheduler would expand) by its tests.
 */
export function commandAfterArgv(argumentText: string): string {
  const words = argumentText.split(/[ \t]+/).filter((w) => w !== '');
  const at = words.indexOf('-Command');

  return at < 0 ? '' : words.slice(at + 1).join(' ');
}

const XML_ESCAPES: Readonly<Record<string, string>> = { '&': '&amp;', '<': '&lt;', '>': '&gt;' };

/** Text content for an XML element. */
export function xmlText(text: string): string {
  return text.replace(/[&<>]/g, (c) => XML_ESCAPES[c] ?? c);
}

function delayElement(seconds: number): string {
  return seconds > 0 ? `<Delay>PT${seconds}S</Delay>` : '';
}

/** The definition Task Scheduler registers (D1, D8): SYSTEM, five triggers, one at a time, the fixed action. */
export function guardTaskXml(options: GuardOptions): string {
  const delay = delayElement(options.delaySeconds);

  return [
    '<?xml version="1.0" encoding="UTF-16"?>',
    '<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">',
    '  <RegistrationInfo>',
    '    <Author>AI OS Care</Author>',
    '    <Description>Starts the Windows Time service (w32time) and resyncs the Windows clock: at startup, at logon, every few hours, when the service logs that it is stopping and when its start type is changed. Installed and removed by the AI OS Care extension (Install / Remove the Windows Time guard).</Description>',
    `    <SecurityDescriptor>${GUARD_SDDL}</SecurityDescriptor>`,
    '  </RegistrationInfo>',
    '  <Principals>',
    '    <Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>',
    '  </Principals>',
    '  <Triggers>',
    `    <BootTrigger><Enabled>true</Enabled>${delay}</BootTrigger>`,
    `    <LogonTrigger><Enabled>true</Enabled>${delay}</LogonTrigger>`,
    `    <TimeTrigger><Repetition><Interval>PT${options.everyHours}H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>2026-01-01T00:00:00Z</StartBoundary><Enabled>true</Enabled></TimeTrigger>`,
    `    <EventTrigger><Enabled>true</Enabled><Subscription>${xmlText(STOP_QUERY)}</Subscription>${delay}</EventTrigger>`,
    `    <EventTrigger><Enabled>true</Enabled><Subscription>${xmlText(START_TYPE_QUERY)}</Subscription>${delay}</EventTrigger>`,
    '  </Triggers>',
    '  <Settings>',
    '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>',
    '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>',
    '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>',
    '    <StartWhenAvailable>true</StartWhenAvailable>',
    `    <ExecutionTimeLimit>PT${options.timeLimitMinutes}M</ExecutionTimeLimit>`,
    '    <Enabled>true</Enabled>',
    '    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>',
    '  </Settings>',
    '  <Actions Context="System">',
    `    <Exec><Command>${xmlText(TASK_POWERSHELL)}</Command><Arguments>${xmlText(guardArguments(options))}</Arguments></Exec>`,
    '  </Actions>',
    '</Task>',
  ].join('\n');
}

/**
 * The PowerShell that prints a definition's canonical summary — ONE text, used by the status query over the REGISTERED task
 * and by the Windows-leg test over Task Scheduler's in-memory parse of `guardTaskXml`, so `guardSummary` cannot drift from
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
  "9 { 'trigger=logon enabled=' + $t.Enabled + ' delay=' + $t.Delay }",
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
 * summary's duration fields are compared by this VALUE, never by their spelling (`guardState.ts`, `summariesMatch`).
 */
export function durationSeconds(text: string): number | undefined {
  const match = DURATION.exec(text);
  if (match === null || text === 'P' || text.endsWith('T')) {
    return undefined;
  }
  const [days, hours, minutes, seconds] = match.slice(1).map((part) => Number(part ?? 0));

  return days * SECONDS_PER.day + hours * SECONDS_PER.hour + minutes * SECONDS_PER.minute + seconds;
}

/** The summary `SUMMARY_FUNCTION` prints for `guardTaskXml(options)` — what "this version, these settings" reads as. */
export function guardSummary(options: GuardOptions): readonly string[] {
  const delay = options.delaySeconds > 0 ? `PT${options.delaySeconds}S` : '';

  return [
    'principal=S-1-5-18 logon=5 level=1',
    `trigger=boot enabled=True delay=${delay}`,
    `trigger=logon enabled=True delay=${delay}`,
    `trigger=time enabled=True interval=PT${options.everyHours}H`,
    `trigger=event enabled=True delay=${delay} subscription=${STOP_QUERY}`,
    `trigger=event enabled=True delay=${delay} subscription=${START_TYPE_QUERY}`,
    `settings limit=PT${options.timeLimitMinutes}M instances=2 whenAvailable=True battery=False enabled=True`,
    `action type=0 path=${TASK_POWERSHELL} args=${guardArguments(options)}`,
  ];
}
