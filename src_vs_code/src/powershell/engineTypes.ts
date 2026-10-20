/**
 * The vocabulary of "which PowerShell runs this", WITHOUT the resolver (moved out of `elevatedEngine.ts` by the coai code round of WR-E3, finding 5):
 * the {@link Engine} a flow names, the one that stands for "none can be named", the lines and sentences the unelevated flows say it in, and the
 * sentence for an engine that changed under a modal. The resolver (`elevatedEngine.ts`) and the engine read (`engineHost.ts`) produce an
 * {@link Engine}; the flows only consume it — so the Windows tasks need none of the elevated modules.
 */

/** The engine that runs ELEVATED — the program, the words the modal and the outcome name it by, and which of the three it is. */
export interface Engine {
  readonly file: string;
  /** "PowerShell 7 (C:\…\pwsh.exe)" / "Windows PowerShell 5.1 (C:\…\powershell.exe)". */
  readonly label: string;
  /** The MSI pwsh, the Store package, or Windows PowerShell 5.1 — the Store package is the one another account may not have (own review 2, F5). */
  readonly kind: 'msi' | 'store' | 'windows';
}

/** No engine can be named: no Windows PowerShell on this machine, or a run refused before any read. */
export const NO_ENGINE: Engine = { file: '', label: '', kind: 'windows' };

/** The modal's line naming the program that runs UNELEVATED — the Windows tasks' install and remove (WR-E3), beside the elevated wording. */
export function programLine(engine: Engine): string {
  return `The program: ${engine.label}`;
}

/** The end of every outcome sentence of an unelevated run: " Program: <label>." — nothing when no engine can be named. */
export function byProgram(engine: Engine): string {
  return engine.label === '' ? '' : ` Program: ${engine.label}.`;
}

/**
 * Why the Store package may not start when the extension starts it by its package path (UNMEASURED, plan §10; ENOENT from `lstat` was
 * seen on the owner's machine, 2026-10-11): PowerShell 7 (MSI) is the program the resolver prefers anyway, and a second press tries once more.
 */
const UNELEVATED_STORE_NOTE = ' (the Store package could not be started by its package path from this extension; install PowerShell 7 (MSI), or install again to try once more)';

/** A refused start's sentence, with the Store note when the program was the Store package — the unelevated variant of {@link withStoreNote}. */
export function withUnelevatedStoreNote(sentence: string, engine: Engine): string {
  return sentence + (engine.kind === 'store' ? UNELEVATED_STORE_NOTE : '');
}

/** What a caller says when the engine its modal named is not the one the read after the modal chose: nothing ran. */
export function engineChangedSentence(before: Engine, now: Engine): string {
  return `Nothing ran: the elevated program changed while the question was open (${before.label} is now ${now.label}). Press the button again.`;
}
