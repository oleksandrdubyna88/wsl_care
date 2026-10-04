/**
 * The version this bundle was BUILT for (plan §15g M8, the family's *Verify the ARTEFACT* rule): `scripts/bundle.mjs`
 * replaces `WSL_CARE_BUILD_STAMP` with `"wsl-care-build <package.json version>"` at bundle time, so the shipped
 * `dist/extension.js` carries the version it was bundled from. The packaged `.vsix` is refused when that stamp is not
 * the `.vsix`'s own `package.json` version (`scripts/check-vsix.mjs`) — the shape of a `.vsix` that once shipped
 * JavaScript three versions old because its bundle step had not run.
 *
 * Unbundled (the compiled `out/` the unit tests load) there is no replacement and the stamp reads `unbundled`.
 */

declare const WSL_CARE_BUILD_STAMP: string | undefined;

/** The marker the stamp starts with — what the `.vsix` check looks for. */
export const BUILD_STAMP_MARKER = 'wsl-care-build';

function stamp(): string {
  return typeof WSL_CARE_BUILD_STAMP === 'string' ? WSL_CARE_BUILD_STAMP : 'unbundled';
}

/** The version the bundle was built for, or `unbundled`. */
export function buildVersion(): string {
  const value = stamp();

  return value.startsWith(`${BUILD_STAMP_MARKER} `) ? value.slice(BUILD_STAMP_MARKER.length + 1) : value;
}
