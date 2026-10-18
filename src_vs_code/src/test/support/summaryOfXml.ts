import { at, child, type XmlElement } from './xmlTree';

/**
 * A reference reader of a task definition's XML into the canonical summary lines `SUMMARY_FUNCTION` prints (`windowsTime/guardTask.ts`)
 * — written independently, in TypeScript, from what the PowerShell function reads (`Principal`, `Triggers`, `Settings`, `Actions` of
 * Task Scheduler's object model). It exists so the plan round 4fc3f328's finding 10 holds on EVERY OS: the daemon's `tasks render`
 * summary (the golden) and the guard's own `guardSummary` are each equal to this reading of their own XML, under `summariesMatch`.
 * The Windows leg then proves the reference itself against Task Scheduler's real parse (`NewTask(0).XmlText`).
 */

const LOGON_TYPE: Readonly<Record<string, number>> = { None: 0, Password: 1, S4U: 2, InteractiveToken: 3, Group: 4, ServiceAccount: 5, InteractiveTokenOrPassword: 6 };
const RUN_LEVEL: Readonly<Record<string, number>> = { LeastPrivilege: 0, HighestAvailable: 1 };
const INSTANCES: Readonly<Record<string, number>> = { Parallel: 0, Queue: 1, IgnoreNew: 2, StopExisting: 3 };

function optionalText(element: XmlElement, ...names: readonly string[]): string {
  const [head, ...rest] = names;
  const next = element.children.find((c) => c.name === head);

  return next === undefined ? '' : rest.length === 0 ? next.text : optionalText(next, ...rest);
}

/** Task Scheduler prints a boolean the way .NET does; an element that is absent holds the schema's default (true). */
function boolean(element: XmlElement, name: string, fallback: boolean): string {
  const text = optionalText(element, name);

  return (text === '' ? fallback : text === 'true') ? 'True' : 'False';
}

function principalLine(root: XmlElement): string {
  const principal = at(root, 'Principals', 'Principal');
  const userId = optionalText(principal, 'UserId');
  // Task Scheduler reads a principal that names SYSTEM with no logon type as a service account.
  const logon = optionalText(principal, 'LogonType') || (userId === 'S-1-5-18' ? 'ServiceAccount' : '');

  return `principal=${userId} logon=${LOGON_TYPE[logon] ?? 0} level=${RUN_LEVEL[optionalText(principal, 'RunLevel')] ?? 0}`;
}

function triggerLine(trigger: XmlElement): string {
  const enabled = boolean(trigger, 'Enabled', true);
  const delay = optionalText(trigger, 'Delay');
  const interval = optionalText(trigger, 'Repetition', 'Interval');
  switch (trigger.name) {
    case 'EventTrigger':
      return `trigger=event enabled=${enabled} delay=${delay} subscription=${optionalText(trigger, 'Subscription')}`;
    case 'TimeTrigger':
      return `trigger=time enabled=${enabled} interval=${interval}`;
    case 'BootTrigger':
      return `trigger=boot enabled=${enabled} delay=${delay}`;
    case 'LogonTrigger':
      return `trigger=logon enabled=${enabled} delay=${delay} interval=${interval}`;
    default:
      throw new Error(`summaryOfXml: no reading for <${trigger.name}>`);
  }
}

function settingsLine(root: XmlElement): string {
  const settings = child(root, 'Settings');

  return `settings limit=${optionalText(settings, 'ExecutionTimeLimit')} instances=${INSTANCES[optionalText(settings, 'MultipleInstancesPolicy')] ?? 2} whenAvailable=${boolean(settings, 'StartWhenAvailable', false)} battery=${boolean(settings, 'DisallowStartIfOnBatteries', true)} enabled=${boolean(settings, 'Enabled', true)}`;
}

/** The summary `SUMMARY_FUNCTION` would print for a definition with this XML. */
export function summaryOfXml(root: XmlElement): readonly string[] {
  const triggers = child(root, 'Triggers').children.map(triggerLine);
  const actions = child(root, 'Actions').children.map((exec) => `action type=0 path=${optionalText(exec, 'Command')} args=${optionalText(exec, 'Arguments')}`);

  return [principalLine(root), ...triggers, settingsLine(root), ...actions];
}
