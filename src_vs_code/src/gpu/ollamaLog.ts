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

/** What reading the log gave: its tail, nothing (absent — Ollama is not installed, which says nothing), or why not. */
export type TailRead = { readonly kind: 'text'; readonly text: string } | { readonly kind: 'absent' } | { readonly kind: 'unreadable'; readonly reason: string };

/** Ollama's word as the alert carries it: its last choice, nothing, or why its log could not be read. */
export type OllamaRead = OllamaChoice | { readonly kind: 'absent' } | { readonly kind: 'unreadable'; readonly reason: string };

/** A failed read: ABSENT only when there is no such file (Ollama is not installed); anything else says why (code round). */
export function readError(e: unknown): TailRead {
  const code = errorCode(e);

  return ABSENT_CODES.has(code) ? { kind: 'absent' } : { kind: 'unreadable', reason: safeText(code, FIELD_MAX) };
}

/** No such file — Ollama is not installed, which the alert does not mention. */
const ABSENT_CODES: ReadonlySet<string> = new Set(['ENOENT', 'ENOTDIR']);

function errorCode(e: unknown): string {
  const code: unknown = (e as { readonly code?: unknown } | null | undefined)?.code;

  return typeof code === 'string' && code !== '' ? code : 'unknown error';
}

/** What the alert carries from a read: the last choice in the tail, why the log could not be read, or nothing. */
export function ollamaReadOf(read: TailRead): OllamaRead | undefined {
  if (read.kind === 'text') {
    return lastChoice(read.text);
  }

  return read.kind === 'unreadable' ? read : undefined;
}

/** The last `bytes` of `file`. */
export function readTail(file: string, bytes: number): TailRead {
  try {
    const handle = fs.openSync(file, 'r');
    try {
      const stat = fs.fstatSync(handle);
      if (!stat.isFile()) {
        return { kind: 'unreadable', reason: 'not a file' };
      }
      const size = stat.size;
      const length = Math.min(size, bytes);
      const buffer = Buffer.alloc(length);
      fs.readSync(handle, buffer, 0, length, size - length);
      return { kind: 'text', text: buffer.toString('utf8') };
    } finally {
      fs.closeSync(handle);
    }
  } catch (e: unknown) {
    return readError(e);
  }
}

/** The sentence the alert carries: "Ollama's last start (<time>) chose: CPU" / "… chose: ROCm on <device>". */
export function choiceSentence(choice: OllamaChoice): string {
  const what = choice.library.toLowerCase() === 'cpu' ? 'the CPU' : `${choice.library}${choice.device === '' ? '' : ` on ${choice.device}`}`;
  const when = choice.time === '' ? '' : ` (${choice.time})`;

  return `Ollama's last start${when} chose ${what}.`;
}
