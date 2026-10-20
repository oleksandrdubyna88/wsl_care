import type { TaskName } from '../client/verbs';
import type { RenderAnswer } from '../client/windowsTasks';
import { programLine, type Engine } from '../powershell/engineTypes';
import { TASK_FOLDER } from '../scheduler/taskFolder';
import { noticeText } from '../text/safeText';

/**
 * The two modals of the Windows tasks (WR-E3 of PLAN_windows_runner.md §3.4, §3.8): what ONE unelevated PowerShell will do, said before
 * it does it. Every value the daemon supplied passes `noticeText` (control characters, bidi overrides and markdown links become
 * harmless text). The install's modal sits beside a read-only tab holding the exact script — the XML the daemon rendered inside it.
 *
 * Words this module may not spell, because the shipped bundle is scanned for them: the daemon's settings verb as an argv word (the
 * exact command that turns the dry run off is the README's, not a literal of the bundle), and the plain word for the staging folder's
 * parent. A literal that names a verb of the daemon is the verb module's alone.
 */

/** What the person is asked: the question, what runs, and the label of the one button that says yes. */
export interface TasksPrompt {
  readonly message: string;
  readonly detail: string;
  readonly confirm: string;
}

export const INSTALL_CONFIRM = 'Install it (one PowerShell, not elevated)';
export const REMOVE_CONFIRM = 'Remove them (one PowerShell, not elevated)';

/** The numbers the daemon's render reports, by key — what the task's triggers and deadlines are. A missing key is said so, never guessed. */
const NOT_REPORTED = '(not reported)';

function numberText(answer: RenderAnswer, key: string): string {
  const found = answer.numbers.find((n) => n.key === key);

  return found === undefined ? NOT_REPORTED : String(found.value);
}

/** The triggers, in words, from the render's numbers: the task's first run after logon and how it repeats. */
function triggersLine(task: TaskName, answer: RenderAnswer): string {
  return task === 'watch'
    ? `The task starts at logon + ${numberText(answer, 'windowsTasks.watchLogonDelaySeconds')} s, then every ${numberText(answer, 'mcpWatchdog.periodMinutes')} min.`
    : `The task starts at logon + ${numberText(answer, 'windowsTasks.archiveLogonDelayMinutes')} min, then every ${numberText(answer, 'windowsTasks.archiveEveryHours')} h.`;
}

/** What a task does, for the outcome's sentence ("Task Scheduler runs <what> every <cadence>"). */
export function whatOf(task: TaskName): string {
  return task === 'watch' ? 'the Windows MCP watch' : 'the archive of the Windows side\'s aged AI sessions';
}

/** How often a task runs, from the render's numbers: a number the render did not report is said so. */
export function cadenceOf(task: TaskName, answer: RenderAnswer): string {
  return task === 'watch' ? `${numberText(answer, 'mcpWatchdog.periodMinutes')} min` : `${numberText(answer, 'windowsTasks.archiveEveryHours')} h`;
}

function deadlineLine(task: TaskName, answer: RenderAnswer): string {
  const own = task === 'watch' ? numberText(answer, 'mcpWatchdog.runLimitMinutes') : numberText(answer, 'timer.runLimitMinutes');

  return `Each run is ended by its own supervisor after ${own} min (its whole process tree), and by Task Scheduler after ${answer.executionTimeLimitMinutes} min.`;
}

function dryLines(answer: RenderAnswer): string[] {
  const reason = `Dry run: ${noticeText(answer.dry.reason)}`;
  if (!answer.dry.dryRun) {
    return [reason];
  }
  const does = answer.task === 'watch' ? 'samples only' : 'previews only';

  return [reason, `While dry the task ${does} and changes nothing; the dry run ends when the daemon's dryRun setting is turned off on the Windows side — this extension never writes the daemon's settings.`];
}

function bodyLine(task: TaskName): string {
  return task === 'watch'
    ? 'Each run samples the Windows MCP servers into the daemon\'s CPU ledger and acts on nothing (the stop of idle servers arrives in a later version).'
    : 'Each run archives the Windows side\'s aged AI sessions (archive run); while dry it previews what it would move (archive preview).';
}

const LIMITATIONS = [
  'The task runs only while you are logged on (an interactive-token task does not run logged out).',
  'If UAC is off (EnableLUA=0) or this is the built-in Administrator account, every process counts as elevated and the task\'s runs are refused with exit 81 — the panel shows it.',
];

/**
 * The install's modal: the ONE PowerShell that runs as you and registers `\wsl-care\<task>`, what the task is and does, the copy it
 * starts (already staged — the plan's order stages before it asks), the engine, the limits, and how to undo it.
 */
export function installPrompt(task: TaskName, answer: RenderAnswer, engine: Engine): TasksPrompt {
  return {
    message: `Install the Windows ${task} task — a scheduled task that runs as you, not elevated?`,
    detail: [
      `One PowerShell runs AS YOU — not elevated, no UAC prompt — and registers ONE scheduled task, \\${TASK_FOLDER}\\${task}, exactly as the read-only editor tab beside this dialog shows (create or replace, so install is also update).`,
      programLine(engine),
      '',
      noticeText(`It runs as your account (${answer.userSid}), interactive token, least privilege — never elevated, never as SYSTEM.`),
      triggersLine(task, answer),
      deadlineLine(task, answer),
      noticeText(`It starts the verified copy ${answer.command} (SHA-256 ${answer.digest}, release ${answer.version}) — a copy of this extension's bundled daemon, never the extension folder. GitHub's attestation proves the released bytes; the copy adds no integrity against your own account.`),
      'The copy is already staged; *Remove the Windows tasks* deletes it.',
      ...dryLines(answer),
      bodyLine(task),
      '',
      ...LIMITATIONS,
      'Remove both tasks and the copy at any time with *Remove the Windows tasks*. Nothing else on Windows is changed.',
    ].map(noticeText).join('\n'),
    confirm: INSTALL_CONFIRM,
  };
}

/** The removal's modal: the exact script it runs, then the daemon's removal of the staged copies — and the rule for a copy still executing. */
export function removePrompt(script: string, engine: Engine): TasksPrompt {
  return {
    message: 'Remove the Windows tasks and their staged copy?',
    detail: [
      `One PowerShell runs AS YOU — not elevated — and runs exactly this: it deletes the tasks \\${TASK_FOLDER}\\watch and \\${TASK_FOLDER}\\archive when present, and the folder only when nothing else is in it (the Windows Time guard's task keeps it).`,
      `Then the daemon's staged-copy removal deletes every staged copy under your LocalAppData's ${TASK_FOLDER}\\bin — a copy whose exe is still executing (a task run in progress) is kept and named, and the next install or removal retries it.`,
      programLine(engine),
      '',
      script,
      '',
      'A task run in progress is not waited for; Task Scheduler may stop it. Nothing else on Windows is changed.',
    ].map((line) => (line === script ? line : noticeText(line))).join('\n'),
    confirm: REMOVE_CONFIRM,
  };
}
