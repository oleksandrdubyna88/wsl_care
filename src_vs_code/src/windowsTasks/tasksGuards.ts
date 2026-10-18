import type { TaskName } from '../client/verbs';
import type { RenderAnswer } from '../client/windowsTasks';
import { terminatesHereString } from '../scheduler/registerScript';
import type { SchedulerRead } from '../scheduler/schedulerAnswer';
import { TASK_FOLDER } from '../scheduler/taskFolder';
import { xmlText } from '../scheduler/xmlText';
import { safeText } from '../text/safeText';

/**
 * What the extension checks of the daemon's render BEFORE it shows it or asks (WR-E3 of PLAN_windows_runner.md §3.4, §3.8): the XML is
 * the daemon's — the extension authors none — so each fact a registration must have is re-read from it, as text, and a task the
 * extension will not register is refused with a sentence that names the FIELD, never the XML. Pure; fail closed: the first problem wins
 * and the answer is the sentence, `''` when there is none.
 *
 * The principal is the reader's OWN account (the SID the one Task Scheduler read printed for the person asking), an interactive token at
 * the least privilege; the action is the ABSOLUTE staged copy; nothing of SYSTEM, a service, a group, a stored password or the highest
 * run level is in the definition, and nothing of the extension folder.
 */

/** A domain or local account — S-1-5-21-<three sub-authorities>-<RID>; the built-in SYSTEM / LOCAL SERVICE / NETWORK SERVICE are not. */
const ACCOUNT_SID = /^S-1-5-21-\d+-\d+-\d+-\d+$/;

/** The service accounts' SIDs, as a word: `S-1-5-18` must not match inside a longer number. */
const SERVICE_SIDS: ReadonlyArray<readonly [RegExp, string]> = [
  [/S-1-5-18(?!\d)/, 'S-1-5-18 (SYSTEM)'],
  [/S-1-5-19(?!\d)/, 'S-1-5-19 (LOCAL SERVICE)'],
  [/S-1-5-20(?!\d)/, 'S-1-5-20 (NETWORK SERVICE)'],
];

/** Words that make a task run as someone else, more privileged, or stored: each is a refusal wherever it appears. */
const FORBIDDEN_WORDS: ReadonlyArray<readonly [RegExp, string]> = [
  [/HighestAvailable/, 'HighestAvailable'],
  [/<GroupId>/, '<GroupId>'],
  [/RunAs/i, 'RunAs'],
];

const FORBIDDEN_LOGON: ReadonlyArray<string> = ['S4U', 'Password', 'ServiceAccount'];

/** Every value of the element `name` in `xml` (no attributes, no nesting): what the definition says for it, each time it says it. */
function valuesOf(xml: string, name: string): string[] {
  return Array.from(xml.matchAll(new RegExp(`<${name}>([^<]*)</${name}>`, 'g')), (m) => m[1] ?? '');
}

function shown(text: string): string {
  return JSON.stringify(safeText(text, 80));
}

/** The reader's own SID: the read must have answered, and the render's `userSid` must be an account of that shape and the same one. */
function accountProblem(answer: RenderAnswer, read: SchedulerRead): string {
  if (read.kind !== 'read') {
    return 'Task Scheduler has not answered who you are; refresh the panel and try again';
  }
  if (!ACCOUNT_SID.test(answer.userSid)) {
    return `the render's userSid ${shown(answer.userSid)} is not a domain or local account SID`;
  }

  return answer.userSid === read.user ? '' : `the render is for the account ${shown(answer.userSid)}, but Task Scheduler says you are ${shown(read.user)}`;
}

/** The action is the absolute staged copy: the file the client started, named again in the XML's `<Command>`. */
function commandProblem(answer: RenderAnswer, stagedFile: string): string {
  if (answer.command.toLowerCase() !== stagedFile.toLowerCase()) {
    return `the render's command ${shown(answer.command)} is not the staged copy ${shown(stagedFile)}`;
  }

  return answer.xml.includes(`<Command>${xmlText(answer.command)}</Command>`) ? '' : 'the render\'s XML does not run its command (<Command>) as the staged copy';
}

function extensionProblem(answer: RenderAnswer, extensionFolder: string): string {
  const folder = extensionFolder.toLowerCase();
  const xml = answer.xml.toLowerCase();

  return folder !== '' && (xml.includes(folder) || xml.includes(xmlText(folder))) ? 'the render\'s XML names the extension\'s own folder, which a task may never run' : '';
}

/** The first forbidden word of `table` the XML carries: its name, or none. */
function carried(xml: string, table: ReadonlyArray<readonly [RegExp, string]>): string | undefined {
  return table.find(([pattern]) => pattern.test(xml))?.[1];
}

function logonCarried(xml: string): string | undefined {
  const logon = valuesOf(xml, 'LogonType').find((value) => FORBIDDEN_LOGON.includes(value));

  return logon === undefined ? undefined : `<LogonType>${logon}`;
}

function forbiddenProblem(answer: RenderAnswer): string {
  const found = carried(answer.xml, SERVICE_SIDS) ?? carried(answer.xml, FORBIDDEN_WORDS) ?? logonCarried(answer.xml);

  return found === undefined ? '' : `the render's XML carries ${found}: a task here runs as you, interactive, never elevated`;
}

/** Every `<element>` of the XML says exactly `wanted`, and the XML says it at least once. */
function sayingOnly(xml: string, element: string, wanted: string): boolean {
  const values = valuesOf(xml, element);

  return values.length > 0 && values.every((value) => value === wanted);
}

function principalProblem(answer: RenderAnswer): string {
  if (!sayingOnly(answer.xml, 'LogonType', 'InteractiveToken')) {
    return 'the render\'s XML does not register an interactive token (<LogonType>InteractiveToken)';
  }
  if (!sayingOnly(answer.xml, 'RunLevel', 'LeastPrivilege')) {
    return 'the render\'s XML does not register the least privilege (<RunLevel>LeastPrivilege)';
  }

  return sayingOnly(answer.xml, 'UserId', answer.userSid) ? '' : `the render's XML names an account other than ${shown(answer.userSid)} (<UserId>)`;
}

function embeddingProblem(answer: RenderAnswer): string {
  return terminatesHereString(answer.xml) ? 'the render\'s XML has a line that starts with the here-string terminator, so it cannot be embedded in a script' : '';
}

function pathProblem(answer: RenderAnswer, task: TaskName): string {
  const wanted = `\\${TASK_FOLDER}\\${task}`;

  return answer.taskPath === wanted ? '' : `the render's task path ${shown(answer.taskPath)} is not ${wanted}`;
}

/**
 * Why `answer` — the daemon's render for `task`, already judged registrable by the client — must not be registered, or `''`. `read` is the
 * ONE Task Scheduler read, `stagedFile` the exe the client started, `extensionFolder` the folder the extension runs from.
 */
export function registrationProblem(answer: RenderAnswer, task: TaskName, read: SchedulerRead, stagedFile: string, extensionFolder: string): string {
  return [
    accountProblem(answer, read),
    commandProblem(answer, stagedFile),
    extensionProblem(answer, extensionFolder),
    forbiddenProblem(answer),
    principalProblem(answer),
    embeddingProblem(answer),
    pathProblem(answer, task),
  ].find((problem) => problem !== '') ?? '';
}
