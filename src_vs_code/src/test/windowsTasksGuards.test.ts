import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { RenderAnswer } from '../client/windowsTasks';
import { schedulerReadOf, type SchedulerRead } from '../scheduler/schedulerAnswer';
import { registrationProblem } from '../windowsTasks/tasksGuards';
import { exited } from './support/recordingRunner';
import { schedulerStdout, USER_SID } from './support/schedulerAnswers';
import { EXTENSION, renderAnswer, STAGED_EXE } from './support/windowsTasks';

/**
 * What the extension checks of the daemon's render before it shows or asks (WR-E3, PLAN_windows_runner.md §3.4): pure, fail closed, each
 * refusal naming the FIELD. The flow tests (`windowsTasksFlow.test.ts`) prove each refusal registers nothing end to end; these prove the
 * function itself, one defect at a time.
 */

const READ: SchedulerRead = schedulerReadOf(exited(0, schedulerStdout()));

function golden(name: 'watch' | 'archive' = 'watch'): RenderAnswer {
  return { ...(renderAnswer(`${name} from the staged copy`) as unknown as RenderAnswer), task: name };
}

function withXml(edit: (xml: string) => string, name: 'watch' | 'archive' = 'watch'): RenderAnswer {
  const answer = golden(name);

  return { ...answer, xml: edit(answer.xml) };
}

function problem(answer: RenderAnswer, read: SchedulerRead = READ, task: 'watch' | 'archive' = answer.task): string {
  return registrationProblem(answer, task, read, STAGED_EXE, EXTENSION.fsPath);
}

test('the daemon\'s own golden renders pass every check, for both tasks', () => {
  assert.equal(problem(golden('watch')), '');
  assert.equal(problem(golden('archive')), '');
});

test('every defect of the principal is refused with a sentence naming it', () => {
  const cases: readonly (readonly [string, RenderAnswer, RegExp])[] = [
    ['SYSTEM', withXml((x) => x.replaceAll(USER_SID, 'S-1-5-18')), /S-1-5-18 \(SYSTEM\)/],
    ['LOCAL SERVICE', withXml((x) => x.replaceAll(USER_SID, 'S-1-5-19')), /S-1-5-19/],
    ['NETWORK SERVICE', withXml((x) => x.replaceAll(USER_SID, 'S-1-5-20')), /S-1-5-20/],
    ['HighestAvailable', withXml((x) => x.replace('LeastPrivilege', 'HighestAvailable')), /HighestAvailable/],
    ['no least privilege', withXml((x) => x.replace('<RunLevel>LeastPrivilege</RunLevel>', '')), /least privilege/],
    ['S4U', withXml((x) => x.replace('InteractiveToken', 'S4U')), /<LogonType>S4U/],
    ['Password', withXml((x) => x.replace('InteractiveToken', 'Password')), /<LogonType>Password/],
    ['ServiceAccount', withXml((x) => x.replace('InteractiveToken', 'ServiceAccount')), /<LogonType>ServiceAccount/],
    ['no logon type', withXml((x) => x.replace('<LogonType>InteractiveToken</LogonType>', '')), /interactive token/],
    ['a group', withXml((x) => x.replace('<Principal id="Author">', '<Principal id="Author"><GroupId>S-1-5-32-544</GroupId>')), /<GroupId>/],
    ['RunAs', withXml((x) => x.replace('Samples', 'runas Samples')), /RunAs/],
    ['a second account in a trigger', withXml((x) => x.replace('<Delay>', '<UserId>S-1-5-21-9-9-9-500</UserId><Delay>')), /names an account other than/],
  ];
  for (const [name, answer, why] of cases) {
    assert.match(problem(answer), why, name);
  }
});

test('a SID is matched as a whole number: S-1-5-181 is not SYSTEM', () => {
  assert.equal(problem(withXml((x) => x.replace('Samples', 'Samples S-1-5-181 S-1-5-2000'))), '');
});

test('the account is the reader\'s own: the render\'s SID has the account shape and equals the read\'s; an unanswered read refuses', () => {
  const other = schedulerReadOf(exited(0, schedulerStdout({ user: 'S-1-5-21-1-2-3-1002' })));
  assert.match(problem(golden(), other), /render is for the account .*1001.*you are .*1002/);
  assert.match(problem({ ...golden(), userSid: 'S-1-5-18' }), /not a domain or local account SID/);
  assert.match(problem({ ...golden(), userSid: 'S-1-5-21-1-2-3' }), /not a domain or local account SID/);
  assert.match(problem(golden(), { kind: 'unknown', cause: 'failed', reason: 'x' }), /has not answered who you are/);
  assert.match(problem(golden(), { kind: 'unknown', cause: 'checking', reason: 'checking…' }), /has not answered who you are/);
});

test('the action is the staged copy, named again in the XML, and the extension folder is nowhere in it', () => {
  assert.match(problem({ ...golden(), command: 'C:\\elsewhere\\wsl-care.exe' }), /is not the staged copy/);
  assert.match(problem({ ...golden(), command: STAGED_EXE.toUpperCase() }), /does not run its command/, 'the XML must say it too, exactly as the daemon wrote it');
  assert.match(problem(withXml((x) => x.replace(STAGED_EXE, 'C:\\elsewhere\\wsl-care.exe'))), /does not run its command/);
  assert.match(problem(withXml((x) => x.replace('Samples', `${EXTENSION.fsPath.toUpperCase()} Samples`))), /extension's own folder/);
  assert.equal(registrationProblem(golden(), 'watch', READ, STAGED_EXE, ''), '', 'an empty folder name matches nothing (not everything)');
});

test('a terminator line and a wrong task path are refused', () => {
  assert.match(problem(withXml((x) => `${x}\n'@`)), /here-string terminator/);
  assert.match(problem(withXml((x) => `${x}\r'@x`)), /here-string terminator/);
  assert.equal(problem(withXml((x) => x.replace('Samples', "it's '@ in the middle"))), '', 'only a line that BEGINS with it ends the string');
  assert.match(problem({ ...golden(), taskPath: '\\other\\watch' }), /task path .* is not \\wsl-care\\watch/);
  assert.match(problem(golden('archive'), READ, 'watch'), /is not \\wsl-care\\watch/, 'the task asked, not the task answered');
});

test('Fable minor 3: an element with an attribute is read too, and every opening tag must have a value (an xmlns on a second LogonType hides nothing)', () => {
  const second = '<LogonType>InteractiveToken</LogonType><LogonType xmlns="urn:x">S4U</LogonType>';
  assert.match(problem(withXml((x) => x.replace('<LogonType>InteractiveToken</LogonType>', second))), /S4U|interactive token/);
  assert.match(problem(withXml((x) => x.replace('<RunLevel>LeastPrivilege</RunLevel>', '<RunLevel>LeastPrivilege</RunLevel><RunLevel id="x">HighestAvailable</RunLevel>'))), /HighestAvailable|least privilege/);
  assert.match(problem(withXml((x) => x.replace('<LogonTrigger>', '<LogonTrigger><UserId xmlns="urn:x">S-1-5-21-9-9-9-500</UserId>'))), /names an account other than/);
  assert.match(problem(withXml((x) => x.replace('<Enabled>true</Enabled>', '<Enabled>true</Enabled><RunLevel/>'))), /least privilege/, 'an opening tag with no value is not "only LeastPrivilege"');
  assert.equal(problem(golden()), '', 'the golden still passes');
});

test('Fable minor 4: exactly one Exec, and every Command is the staged copy', () => {
  const evil = '<Exec><Command>C:\\evil\\run.exe</Command></Exec>';
  assert.match(problem(withXml((x) => x.replace('</Actions>', evil + '</Actions>'))), /one <Exec>|does not run its command/);
  assert.match(problem(withXml((x) => x.replace('</Exec>', '</Exec><Exec><Command>' + STAGED_EXE + '</Command></Exec>'))), /one <Exec>|does not run its command/, 'a second Exec, even of the staged copy, is refused');
  assert.match(problem(withXml((x) => x.replace('</Exec>', '<Command id="y">C:\\evil\\run.exe</Command></Exec>'))), /does not run its command/, 'a second Command, with an attribute, is read and compared');
  assert.equal(problem(withXml((x) => x.replace('<Command>', '<Command id="x">'))), '', 'an attribute on the one Command, with the staged value, is harmless');
});
