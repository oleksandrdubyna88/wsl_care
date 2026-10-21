import { TASK_NAMES, type TaskName } from '../client/verbs';
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
export const RUN_CONFIRM = 'Start it now (one PowerShell, not elevated)';

/** The numbers the daemon's render reports, by key — what the task's triggers and deadlines are. A missing key is said so, never guessed. */
const NOT_REPORTED = '(not reported)';

function numberText(answer: RenderAnswer, key: string): string {
  const found = answer.numbers.find((n) => n.key === key);

  return found === undefined ? NOT_REPORTED : String(found.value);
}

/** The sentences of one task — a table keyed by `TaskName`, so a task added to the closed list does not compile until it has its words (WR-T3). */
interface TaskWords {
  /** What the task does, for the outcome's sentence ("Task Scheduler runs <what> every <cadence>"). */
  readonly what: string;
  /** The task's first run after logon and how it repeats, from the render's numbers. */
  readonly triggers: (answer: RenderAnswer) => string;
  /** How often it runs, from the render's numbers. */
  readonly cadence: (answer: RenderAnswer) => string;
  /** The key of the render's numbers that holds the task's own run limit. */
  readonly runLimitKey: string;
  /** What each run does. */
  readonly body: string;
  /** What "dry" means for this task. */
  readonly dryDoes: string;
  /** The question that opens the install modal. */
  readonly installQuestion: string;
  /** What a start now changes. */
  readonly runChanges: string;
}

const NO_SETTINGS_CHANGED = 'No Windows settings or other tasks are changed';

const WORDS: { readonly [K in TaskName]: TaskWords } = {
  watch: {
    what: 'the Windows MCP watch',
    triggers: (answer) => `The task starts at logon + ${numberText(answer, 'windowsTasks.watchLogonDelaySeconds')} s, then every ${numberText(answer, 'mcpWatchdog.periodMinutes')} min.`,
    cadence: (answer) => `${numberText(answer, 'mcpWatchdog.periodMinutes')} min`,
    runLimitKey: 'mcpWatchdog.runLimitMinutes',
    body: 'Each run samples the Windows MCP servers into the daemon\'s CPU ledger and acts on nothing (the stop of idle servers arrives in a later version).',
    dryDoes: 'samples only and changes nothing',
    installQuestion: 'Install the Windows watch task — a scheduled task that runs as you, not elevated?',
    runChanges: `${NO_SETTINGS_CHANGED}.`,
  },
  archive: {
    what: 'the archive of the Windows side\'s aged AI sessions',
    triggers: (answer) => `The task starts at logon + ${numberText(answer, 'windowsTasks.archiveLogonDelayMinutes')} min, then every ${numberText(answer, 'windowsTasks.archiveEveryHours')} h.`,
    cadence: (answer) => `${numberText(answer, 'windowsTasks.archiveEveryHours')} h`,
    runLimitKey: 'timer.runLimitMinutes',
    body: 'Each run archives the Windows side\'s aged AI sessions (archive run); while dry it previews what it would move (archive preview).',
    dryDoes: 'previews only and changes nothing',
    installQuestion: 'Install the Windows archive task — a scheduled task that runs as you, not elevated?',
    runChanges: `${NO_SETTINGS_CHANGED}; the archive task may move session files when the dry line says it acts.`,
  },
  cleanup: {
    what: 'the cleanup of your own old scratch entries in the Windows TEMP folder (A23)',
    triggers: (answer) => `The task starts at logon + ${numberText(answer, 'windowsTasks.cleanupLogonDelayMinutes')} min, then every ${numberText(answer, 'windowsTasks.cleanupEveryHours')} h.`,
    cadence: (answer) => `${numberText(answer, 'windowsTasks.cleanupEveryHours')} h`,
    runLimitKey: 'timer.runLimitMinutes',
    body: 'Each run deletes your OWN old scratch entries in the Windows TEMP folder: only top-level names the allow-list (temp.allowGlobs) covers, older than temp.olderThanDays (by their newest file), owned by you; never %TEMP%\\claude, never a git working folder, never a reparse point, never more than temp.maxItemsPerRun per run. Press *Preview the Windows TEMP cleanup* to see the values in force and what would go.',
    dryDoes: 'previews only and deletes nothing',
    installQuestion: 'Install the Windows cleanup task — a scheduled task that runs as you, not elevated, and DELETES old scratch entries in your TEMP folder?',
    runChanges: `${NO_SETTINGS_CHANGED}; the cleanup task deletes your old scratch entries in the Windows TEMP folder when the dry line says it acts (never %TEMP%\\claude).`,
  },
};

/** The triggers, in words, from the render's numbers. */
function triggersLine(task: TaskName, answer: RenderAnswer): string {
  return WORDS[task].triggers(answer);
}

/** What a task does, for the outcome's sentence ("Task Scheduler runs <what> every <cadence>"). */
export function whatOf(task: TaskName): string {
  return WORDS[task].what;
}

/** How often a task runs, from the render's numbers: a number the render did not report is said so. */
export function cadenceOf(task: TaskName, answer: RenderAnswer): string {
  return WORDS[task].cadence(answer);
}

function deadlineLine(task: TaskName, answer: RenderAnswer): string {
  const own = numberText(answer, WORDS[task].runLimitKey);

  return `Each run is ended by its own supervisor after ${own} min (its whole process tree), and by Task Scheduler after ${answer.executionTimeLimitMinutes} min.`;
}

function dryLines(answer: RenderAnswer): string[] {
  const reason = `Dry run: ${noticeText(answer.dry.reason)}`;
  if (!answer.dry.dryRun) {
    return [reason];
  }
  const own = answer.task === 'cleanup' ? ['This task has its OWN dry week (A23\'s, separate from the watch\'s and the archive\'s).'] : [];

  return [reason, ...own, `While dry the task ${WORDS[answer.task].dryDoes}; the dry run ends when the daemon's dryRun setting is turned off on the Windows side — this extension never writes the daemon's settings.`];
}

function bodyLine(task: TaskName): string {
  return WORDS[task].body;
}

const LIMITATIONS = [
  'The task runs only while you are logged on (an interactive-token task does not run logged out).',
  'If UAC is off (EnableLUA=0) or this is the built-in Administrator account, every process counts as elevated: the task\'s runs are refused with exit 81, the daemon will not render this task as registrable, and the preview answers 81 too — run it from an account that is not an administrator, or turn UAC on.',
  'A last result of 75 means busy — a watch was sampling under the daemon\'s run lock; the next scheduled run retries.',
];

/**
 * The install's modal: the ONE PowerShell that runs as you and registers `\wsl-care\<task>`, what the task is and does, the copy it
 * starts (already staged — the plan's order stages before it asks), the engine, the limits, and how to undo it.
 */
export function installPrompt(task: TaskName, answer: RenderAnswer, engine: Engine): TasksPrompt {
  return {
    message: WORDS[task].installQuestion,
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
      'Remove every Windows task and the copy at any time with *Remove the Windows tasks*. Nothing else on Windows is changed.',
    ].map(noticeText).join('\n'),
    confirm: INSTALL_CONFIRM,
  };
}

/** Every task's path, as a sentence: the folder's tasks in the closed list's order, joined with "and" — derived from the list, never hand-listed. */
export function taskPaths(): string {
  const paths = TASK_NAMES.map((name) => `\\${TASK_FOLDER}\\${name}`);

  return `${paths.slice(0, -1).join(', ')} and ${paths.at(-1) ?? ''}`;
}

/** The removal's modal: the exact script it runs, then the daemon's removal of the staged copies — and the rule for a copy still executing. */
export function removePrompt(script: string, engine: Engine): TasksPrompt {
  return {
    message: 'Remove the Windows tasks and their staged copy?',
    detail: [
      `One PowerShell runs AS YOU — not elevated — and runs exactly this: it deletes the tasks ${taskPaths()} when present, and the folder only when nothing else is in it (the Windows Time guard's task keeps it).`,
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

/** What a start now changes, said precisely: no settings and no other task; the archive task may MOVE session files and the cleanup task DELETES scratch entries once its dry line says it acts. */
function runChanges(task: TaskName): string {
  return WORDS[task].runChanges;
}

/**
 * The run-now modal (WR-E4): ONE PowerShell as you asks Task Scheduler to start the task the way its next trigger would, and — since the archive task MOVES
 * files once the dry run is off — the exact script is in the detail, verbatim (the removal's shape; it is short, no read-only tab). Every line but the
 * script passes `noticeText`.
 */
export function runPrompt(task: TaskName, script: string, engine: Engine): TasksPrompt {
  return {
    message: `Start the Windows ${task} task now — as you, not elevated?`,
    detail: [
      `One PowerShell runs AS YOU — not elevated, no UAC prompt — and asks Task Scheduler to start \\${TASK_FOLDER}\\${task} exactly as its next trigger would: the registered copy, your account, an interactive token, the least privilege.`,
      `What it does: ${whatOf(task)}.`,
      'If the task is already running nothing new is started, and the panel says so.',
      programLine(engine),
      '',
      script,
      '',
      "The panel's dry line says whether the run previews or acts. " + runChanges(task),
    ].map((line) => (line === script ? line : noticeText(line))).join('\n'),
    confirm: RUN_CONFIRM,
  };
}
