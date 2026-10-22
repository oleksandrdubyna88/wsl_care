import assert from 'node:assert/strict';

import { listedNameOf, pickedTarget, type DistroName, type DistroTarget } from '../../wsl/distroTarget';

/** The `DistroName` WSL's own list would mint for `name` — the only road to one; a name not among `among` is a test defect. */
export function listed(name: string, among: readonly string[] = ['Ubuntu', 'Debian']): DistroName {
  const minted = listedNameOf(name, among);
  assert.ok(minted !== undefined, `${name} is not minted from ${JSON.stringify(among)}`);

  return minted;
}

/** The picked target for `name`, minted from the same list. */
export function pick(name: string, among: readonly string[] = ['Ubuntu', 'Debian']): DistroTarget {
  return pickedTarget(listed(name, among));
}
