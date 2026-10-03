import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import { nodeScriptRunner, type Runner } from '../../process/runner';
import type { FakeScenario } from '../fake/fakeWsl';
import { FAKE_SCRIPT, GOLDEN_ROOT } from './paths';

/**
 * One world for the strict fake: a temporary folder holding the scenario file and the call log, and the runner that
 * starts the fake through the product's own `nodeScriptRunner`. Removed by `dispose()`.
 */
export interface FakeWorld {
  readonly runner: Runner;
  readonly folder: string;
  /** Every call the fake received, in order: its argv joined with spaces. */
  calls(): string[];
  /** Every call's requested file — what the client asked the runner to start. */
  files(): (string | null)[];
  dispose(): void;
}

export type ScenarioInput = Omit<FakeScenario, 'answers' | 'log'> & { readonly answers?: string };

/** A machine with `Ubuntu` running and marked default, the daemon installed, answering from the `head` goldens. */
export const UBUNTU_RUNNING: ScenarioInput = {
  distros: [{ name: 'Ubuntu', running: true }, { name: 'docker-desktop', running: true }],
  defaultDistro: 'Ubuntu',
  binary: 'present',
};

interface LoggedCall {
  readonly argv: readonly string[];
  readonly file: string | null;
}

export function fakeWorld(input: ScenarioInput): FakeWorld {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'wsl-care-fake-'));
  const log = path.join(folder, 'calls.jsonl');
  const scenario: FakeScenario = { ...input, answers: input.answers ?? path.join(GOLDEN_ROOT, 'head'), log };
  const file = path.join(folder, 'scenario.json');
  fs.writeFileSync(file, JSON.stringify(scenario));
  const logged = (): LoggedCall[] =>
    fs.existsSync(log) ? fs.readFileSync(log, 'utf8').split('\n').filter((l) => l.length > 0).map((l) => JSON.parse(l) as LoggedCall) : [];

  return {
    runner: nodeScriptRunner(FAKE_SCRIPT, { WSL_CARE_FAKE_SCENARIO: file }),
    folder,
    calls: () => logged().map((c) => c.argv.join(' ')),
    files: () => logged().map((c) => c.file),
    dispose: () => fs.rmSync(folder, { recursive: true, force: true }),
  };
}

/** The environment the client is handed in tests: a Windows root that names System32's wsl.exe — never started. */
export const TEST_ENV: Readonly<Record<string, string>> = { SystemRoot: 'C:\\Windows' };
