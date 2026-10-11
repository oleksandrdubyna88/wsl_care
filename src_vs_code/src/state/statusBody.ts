import type { Snapshot } from './outcomeStore';

/**
 * The newest answered `status` body of a snapshot, and the two things the host's buttons read from it — what it advertises
 * (`capabilities`) and which actions it offers (`actions`). Extracted from the archive host (E10.S1b) when *Stop client-gone
 * relays* (S7b.2b) needed the same reads: one reader, never two.
 */
export function statusBodyOf(snapshot: Snapshot): Readonly<Record<string, unknown>> | undefined {
  const status = snapshot.status;

  return status?.kind === 'answered' && status.answer.verb === 'status' ? status.answer.body : undefined;
}

/** The capabilities the newest answered status advertises — `undefined` until one answered. */
export function advertisedOf(snapshot: Snapshot): readonly string[] | undefined {
  const body = statusBodyOf(snapshot);
  if (body === undefined) {
    return undefined;
  }
  const listed = body.capabilities;

  return Array.isArray(listed) ? listed.filter((c): c is string => typeof c === 'string') : [];
}

/** The newest answered status offers `id` in `status.actions`. */
export function offersAction(snapshot: Snapshot, id: string): boolean {
  const actions = statusBodyOf(snapshot)?.actions;

  return Array.isArray(actions) && actions.includes(id);
}
