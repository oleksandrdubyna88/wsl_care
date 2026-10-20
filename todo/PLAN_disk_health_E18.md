# PLAN — E18 stories: wsl_care consumes the kit, the full view, the slim side bar, Help, accessibility (#87)

> Status: **in progress (2026-10-20)** — the stories document of epic E18 of [PLAN_disk_health.md](PLAN_disk_health.md)
> (§3.4–§3.6 and its E18 table). E18.K1 is done in `dew_flow_vscode_kit` (kit #9 → `655df4de`; **0.2.0 released to
> GitHub Packages**, release PR #16, run 39481672054 green). This document holds **W0** now; W1a, W1b, W2, W4, W3 and W5
> are added here, each before its own plan round. Nothing of W0 is built yet.
>
> Related: [module_vs_code.md](../research/module_vs_code.md), [architecture.md](../research/architecture.md), the kit's
> `research/PLAN_kit_components_storybook.md` (kit `main` @ `1d2681cc`) §4.3 (the DOM port), §4.6 / §4.6a (the DataTable, its reveal contract and the
> fit rule).

## W0 — wsl_care consumes the kit

### 1. Goal

The full view (W1b) is built from the kit's components, compiled into the extension's own webview bundle. W0 lays the
road and nothing visible: the pinned dependency and the registry it comes from, a second esbuild entry for the WEBVIEW,
the strict page harness widened by exactly what the kit's components touch, and one test that RUNS the kit's three
components in that harness. No panel opens it yet, so the extension's behaviour does not change.

What must be true when W0 is done:

1. `@oleksandrdubyna88/vscode-webview-kit` is a devDependency pinned to EXACTLY `0.2.0` (bundled by esbuild, so nothing
   of it ships beside the bundle), resolved from GitHub Packages through a committed `src_vs_code/.npmrc` that holds no
   secret: `@oleksandrdubyna88:registry=https://npm.pkg.github.com` and
   `//npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}`.
2. Every CI job that installs the extension's dependencies (`ci-extension.yml`'s matrix job; `release-extension.yml`'s
   build job and the job that runs `npm ci --ignore-scripts`) has `permissions: packages: read` and passes
   `NODE_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}` to the install step ONLY.
3. `npm run bundle` builds a second entry `src/webview/fullView.ts` → `dist/webview/full.js` (IIFE, `platform: 'browser'`,
   the Chromium of VS Code 1.85, no source map, not minified) and `dist/webview/full.css` (the kit's `components.css`,
   imported by the entry, emitted by esbuild). Both are in `.vscodeignore`'s allowlist and `vsix-files.txt`; the
   `.vsix` check (`scripts/check-vsix.mjs`) holds them like every other file. `.npmrc` never ships.
4. The strict harness (`src/test/support/pageHarness.ts`) models exactly the kit's DOM port and nothing more:
   `setAttribute` gains `id` and `tabindex`; element events gain `keydown` and `input`; elements gain `focus()`
   (recorded as the document's active element); a dispatched event carries `key` and `preventDefault()` (recorded).
   Every widening is a RED test first.
5. One page test bundles a TEST entry that builds the kit's `Tabs`, `DataTable` (search, sort, the reveal, `stack`) and
   `RecommendationCard` with wsl_care-shaped data, runs it in the strict harness, and drives it: arrow keys move the
   tab, a search hides rows, a header click sorts, a reveal click hands the host an `(index, request)` and the host's
   `reveal(index, text, request)` answers `true` once and `false` for a stale or repeated request, `setRows([])` then
   `dispose()` leave no revealed text; an `onReveal` that throws leaves the row clickable again (the kit clears the
   request and rethrows). The imports are `…/components` and `import '…/components.css'`. It proves the harness accepts the port, and that it refuses one attribute more.

### 2. Where things are today (checked on `main` @ `5d7d193d`, 2026-10-20)

- One esbuild entry: `src_vs_code/scripts/bundle.mjs:56-69` (`src/extension.ts` → `dist/extension.js`, cjs, node18,
  `vscode` external); `src/test/manifest.test.ts:138-157` reads those options back with the TypeScript parser.
- The webview pages are hand-written static scripts (`media/panel.js`, `logs.js`, `archive.js`), shipped by
  `.vscodeignore`'s `!media/**`; `vsix-files.txt` lists 12 files and is held equal to `vsce ls --no-dependencies`.
- The shell's CSP: `default-src 'none'; script-src 'nonce-…'; style-src 'nonce-…'` (`src/panel/panelHtml.ts:60`);
  resources only from `media/` (`panelOptions`, `panelHtml.ts:74-76`). W0 changes neither — W1b's panel adds the
  `dist/webview/` root for its own page only.
- The harness: tags `pageHarness.ts:30` (all fourteen of the kit's tags are already there), attributes `:33`
  (`role`, `scope`, `title`, `type`, `colspan`, `min`, `max`; `aria-*` and `data-*` by prefix), element events `:36`
  (`click` only); no `focus()`.
- `tsconfig.json` is `lib: ["ES2022"]` with no DOM, `include: ["src"]` — the host never touches a document.
- `src_vs_code/package.json` has no `dependencies` (the bundle carries everything) and no `.npmrc` exists.
- Locally this machine's `gh` token has no `read:packages` scope (`npm.pkg.github.com` answers 403 "does not match
  expected scopes"); the kit's release run kept the attested tarball as the artifact `release-tarball` (run
  39481672054), whose `npm notice integrity` is the published one.

### 3. Design

- **The lock entry.** The dependency is added the ordinary way where a `read:packages` token exists. Without one, the
  lock entry is written from the release artifact: the tarball is downloaded with the repo-scoped token, checked with
  `gh attestation verify <tgz> -R oleksandrdubyna88/dew_flow_vscode_kit` and its sha512 compared with the published
  integrity, and the lock records exactly that integrity; CI's `npm ci` then fetches from GitHub Packages and npm
  refuses any byte that differs. The README's development section says a contributor needs a classic PAT with
  `read:packages` in `NODE_AUTH_TOKEN`.
- **The webview entry.** `src/webview/fullView.ts` imports `…/components` and `…/components.css`, acquires the VS Code
  API once, and renders an empty root; W1b fills it. `src/webview/` gets its own `tsconfig.webview.json`
  (`lib: ["ES2022", "DOM"]`), type-checked by `npm run typecheck`; the main `tsconfig.json` excludes `src/webview` so the
  host keeps "no DOM". An architecture test holds the boundary both ways: nothing under `src/webview/` imports
  `vscode`, a `node:` module or host code, and no host module imports `src/webview/`.
- **The bundle.** A second `buildSync` in `bundle.mjs` with the options of goal 3; `manifest.test.ts` reads both option
  objects back. The bundle scan that already reads `dist/extension.js` reads `dist/webview/full.js` too (no source
  map, no `sourceMappingURL`, no machine path).
- **The harness widening** is the list of goal 4 and only that list, with the reason in the module's comment (the kit's
  `research/PLAN_kit_components_storybook.md` §4.3, kit `main` @ `1d2681cc`). `focus()` records `document.activeElement` (the harness
  document gains that read). `input` events read the element's `value`, which the harness already models.
- **No Dependabot change in W0.** Dependabot reads GitHub Packages only through a Dependabot secret (a separate store
  from Actions secrets); until the owner adds one, Dependabot cannot bump this pin, and the README says so.
  Recorded as an owner item, not built.

### 4. RED list (each seen failing for the real reason first)

1. `manifest.test.ts`: the kit dependency is `devDependencies["@oleksandrdubyna88/vscode-webview-kit"] === "0.2.0"`
   exactly (no `^`, `~`, range or tag), and `package.json` still has no `dependencies`.
2. `.npmrc`: exactly the two lines of goal 1; no literal token; it is not in `vsix-files.txt`.
3. Workflows: every step that runs `npm ci` / `npm install` under `src_vs_code` is in a job with `packages: read` and
   the step carries `NODE_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}`; no other step carries it.
4. `manifest.test.ts`: the second entry's options (`iife`, `browser`, the target, `sourcemap: false`,
   `minify: false`, outfile `dist/webview/full.js`).
5. After `npm run bundle`: `dist/webview/full.js` and `full.css` exist; `full.css` carries the kit's token block
   (`--kit-fg`); the bundle scan passes over `full.js`.
6. `vsix-files.txt` and `.vscodeignore` name both files; the `.vsix` check passes.
7. The harness: `setAttribute('id')` and `('tabindex')` are accepted; `('style')`, `('onclick')`, `('href')` still
   refused; `addEventListener('keydown' | 'input')` accepted, `('change')`, `('focus')` still refused; `focus()` sets the
   active element; `preventDefault()` is recorded on a dispatched event.
8. The kit page test of goal 5, including the stale-request `false`, and that no hidden value is in the DOM before the
   host replies.
9. The architecture test of §3 (both directions).

### 5. Break-its (each restored, SHA-256 compared)

Pin `^0.2.0` → item 1 red. Drop `packages: read` from one job → item 3 red. Remove `tabindex` from the harness
→ item 8 red (the kit's roving tabindex). Bundle the entry for `node` → item 4 red. A host module importing
`src/webview/fullView.ts` → item 9 red. Never a tripwire disabled.

### 6. Build order

1. This section; the plan round.
2. `.npmrc`, the dependency and the lock entry (§3), `npm ci` green.
3. RED items 1–9, then the code: workflows, `bundle.mjs`, `fullView.ts` + its tsconfig, the harness widening, the
   kit page test, the architecture test.
4. Break-its; docs (`research/module_vs_code.md`: the webview bundle, the kit, the harness surface;
   `research/architecture.md`: the build pipeline and the dependency's registry; README: the development token).
5. Full suites: `npm run typecheck`, `lint`, `test`, `test:host`, `check:vsix`, and the daemon tests that read
   `src_vs_code/` (`ArchitectureTests`, `InteropModulePathTests`, `FullCheckLineTests`, `FixturePrivacy`,
   `ExtensionPackageName`, `ReleaseConfig`, `ReleaseExtension*`); the family checks. A private-data scan before the push.
6. The code round, the pull request, CodeRabbit, the merge on every check green.

### 7. What W0 does not prove

That a real webview loads `full.css` under the nonce (W1b's panel, and a real VS Code); that Dependabot can bump the pin
(an owner item); a layout — nothing is shown yet.

### 8. Definition of Done

- [ ] Goals 1–5 hold; RED items 1–9 seen red, then green; the break-its turned the right test red.
- [ ] CI green on both legs with the package fetched from GitHub Packages; the `.vsix` check green.
- [ ] The daemon tests that read `src_vs_code/` green; the family checks clean.
- [ ] Docs updated; the gate's plan and code rounds resolved; merged.
