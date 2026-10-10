import { parseAnswer, parseDaemonVersion } from '../../client/handshake';
import type { Failure, VerbOutcome } from '../../client/outcome';
import type { Verb } from '../../client/verbs';
import type { Body } from './body';
import { golden } from './paths';

/**
 * Outcomes as the CLIENT produces them, for the view tests: a golden body (or an edited copy) read through the
 * client's own `parseAnswer`, so a view test sees exactly the typed answer the panel and the bar receive in the
 * product — never a hand-built object that could drift from the handshake.
 */

export type { Body };

/** A deep copy of one golden body, to edit. */
export function headBody(verb: 'status' | 'preview' | 'doctor', set = 'head'): Body {
  return structuredClone(golden(set, verb));
}

/** The client's answered outcome for `verb` over `body`. */
export function answered(verb: 'status' | 'preview' | 'doctor', body: Body, distro = 'Ubuntu'): VerbOutcome {
  const answer = parseAnswer(verb, JSON.stringify(body));
  if ('kind' in answer) {
    throw new Error(`the test body does not parse as ${verb}: ${JSON.stringify(answer)}`);
  }

  return { kind: 'answered', verb, distro, answer, daemonVersion: parseDaemonVersion('0.1.0') };
}

/** A failure outcome of `verb`. */
export function failed(verb: Verb, failure: Failure): VerbOutcome {
  return { ...failure, verb };
}

/** The three answered outcomes of one golden set, as a refreshed panel holds them. */
export function goldenOutcomes(set = 'head'): { status: VerbOutcome; preview: VerbOutcome; doctor: VerbOutcome } {
  return { status: answered('status', headBody('status', set)), preview: answered('preview', headBody('preview', set)), doctor: answered('doctor', headBody('doctor', set)) };
}

function parentOf(body: Body, path: string): { parent: Record<string, unknown>; key: string } {
  const keys = path.split('.');
  const key = keys.pop() ?? '';
  let parent: Record<string, unknown> = body;
  for (const step of keys) {
    parent = parent[step] as Record<string, unknown>;
  }

  return { parent, key };
}

/** Sets `value` at a dotted path of `body` (every step before the last must exist). */
export function setAt(body: Body, path: string, value: unknown): Body {
  const { parent, key } = parentOf(body, path);
  parent[key] = value;

  return body;
}

/** Removes the field at a dotted path of `body` — an older daemon that predates it. */
export function removeAt(body: Body, path: string): Body {
  const { parent, key } = parentOf(body, path);
  delete parent[key];

  return body;
}
