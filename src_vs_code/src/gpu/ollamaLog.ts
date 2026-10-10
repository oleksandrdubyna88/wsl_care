import * as fs from 'node:fs';
import * as path from 'node:path';

import { safeText } from '../text/safeText';

/**
 * Ollama's own word on which device it chose (PLAN_gpu_health.md D2): the LAST `msg="inference compute"` line of
 * `%LOCALAPPDATA%\Ollama\server.log`, read from a bounded tail. On 2026-10-10 it read `library=cpu` while the R9700 was
 * disabled, and `library=ROCm … description="AMD Radeon AI PRO R9700"` the day before (research G2). Read-only; nothing
 * from the log reaches a command — it is shown, sanitised.
 */

export interface OllamaChoice {
  /** `cpu`, `ROCm`, `CUDA`, … as Ollama prints it. */
  readonly library: string;
  /** The device's description, or its name — '' for the CPU. */
  readonly device: string;
  /** The line's `time=` as Ollama printed it (local time with its offset), or ''. */
  readonly time: string;
}

const MARKER = 'msg="inference compute"';

/** `key=value` or `key="value with spaces"` from one logfmt line. */
function field(line: string, key: string): string {
  const match = new RegExp(`(?:^|\\s)${key}=(?:"([^"]*)"|(\\S*))`).exec(line);

  return match === null ? '' : (match[1] ?? match[2] ?? '');
}

const FIELD_MAX = 120;

/** The last inference-compute choice in `text` (a tail: its first line may be cut and is skipped), or `undefined`. */
export function lastChoice(text: string): OllamaChoice | undefined {
  const line = text.split(/\r?\n/).filter((l) => l.includes(MARKER)).pop();
  const library = line === undefined ? '' : field(line, 'library');

  return line === undefined || library === '' ? undefined : choiceOf(line, library);
}

function deviceOf(line: string, library: string): string {
  return library.toLowerCase() === 'cpu' ? '' : field(line, 'description') || field(line, 'name');
}

function choiceOf(line: string, library: string): OllamaChoice {
  return { library: safeText(library, FIELD_MAX), device: safeText(deviceOf(line, library), FIELD_MAX), time: safeText(field(line, 'time'), FIELD_MAX) };
}

/** Where Ollama writes its server log on Windows, or `undefined` without `LOCALAPPDATA`. */
export function ollamaLogPath(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const local = env.LOCALAPPDATA;

  return local === undefined || local === '' ? undefined : path.win32.join(local, 'Ollama', 'server.log');
}

/** The last `bytes` of `file`, or `undefined` when it cannot be read (absent: Ollama is not installed — that says nothing). */
export function readTail(file: string, bytes: number): string | undefined {
  try {
    const handle = fs.openSync(file, 'r');
    try {
      const size = fs.fstatSync(handle).size;
      const length = Math.min(size, bytes);
      const buffer = Buffer.alloc(length);
      fs.readSync(handle, buffer, 0, length, size - length);
      return buffer.toString('utf8');
    } finally {
      fs.closeSync(handle);
    }
  } catch {
    return undefined;
  }
}

/** The sentence the alert carries: "Ollama's last start (<time>) chose: CPU" / "… chose: ROCm on <device>". */
export function choiceSentence(choice: OllamaChoice): string {
  const what = choice.library.toLowerCase() === 'cpu' ? 'the CPU' : `${choice.library}${choice.device === '' ? '' : ` on ${choice.device}`}`;
  const when = choice.time === '' ? '' : ` (${choice.time})`;

  return `Ollama's last start${when} chose ${what}.`;
}
