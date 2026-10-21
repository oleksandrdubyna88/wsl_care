# PLAN — E18 stories: wsl_care consumes the kit, the full view, the slim side bar, Help, accessibility (#87)

> Status: **in progress (2026-10-20)** — the stories document of epic E18 of [PLAN_disk_health.md](PLAN_disk_health.md)
> (§3.4–§3.6 and its E18 table). E18.K1 is done in `dew_flow_vscode_kit` (kit #9 → `655df4de`; **0.2.0 released to
> GitHub Packages**, release PR #16, run 39481672054 green). This document holds **W0** now; W1a, W1b, W2, W4, W3 and W5
> are added here, each before its own plan round. **W0 is built (2026-10-21); its code round is done (gate session `e9ba8ab4`: proceed) — awaiting the pull request and merge.** § 6b is the code round, § 9 records what shipped differently.
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
   `NODE_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}` to the install step ONLY — and that step runs `npm ci --ignore-scripts`, so no
   dependency's lifecycle script ever runs while the token is in its environment; a following step without the token
   runs `npm rebuild` for the install scripts the build needs. A prerequisite, verified by W0's first CI run: the kit's
   package grants this repository read access (public, or wsl_care added under the package's *Manage Actions access*); a
   401, 403 or 404 there (GitHub Packages answers any of them for a package the token may not read) is an owner setting, not a code fix.
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

- **The lock entry** (plan round, 2026-10-20: the mechanism made exact). Where a `read:packages` token exists, the
  ordinary `npm install --save-dev --save-exact`. Without one (this machine): (a) download the release run's
  `release-tarball` artifact with the repo-scoped token; (b) `gh attestation verify <tgz> -R oleksandrdubyna88/dew_flow_vscode_kit`
  and its sha512 equal to the published `npm notice integrity`; (c) `npm cache add <tgz>` into a cache folder outside
  the repository and the home folder; (d) the lock entry written with that `version` and `integrity` and no `resolved`,
  so npm takes the tarball from the scope's registry (`.npmrc`) and refuses any byte that differs; (e) `npm ci --offline`
  green locally from that cache, and `npm ci` green in CI from GitHub Packages. If (e) fails in CI, the lock is written
  by an install with a `read:packages` token instead — an owner item, said in the PR. The README's development section says a contributor needs a classic PAT with
  `read:packages` in `NODE_AUTH_TOKEN`.
- **The webview entry.** `src/webview/fullView.ts` imports `…/components` and `…/components.css`, acquires the VS Code
  API once, and renders an empty root; W1b fills it. `src/webview/` gets its own `tsconfig.webview.json`
  (`lib: ["ES2022", "DOM"]`), type-checked by `npm run typecheck` (the script runs `tsc` over both configs); the main `tsconfig.json` excludes `src/webview` so the
  host keeps "no DOM". An architecture test holds the boundary both ways: nothing under `src/webview/` imports
  `vscode`, a `node:` module or host code, and no host module imports `src/webview/`.
- **The bundle.** A second `buildSync` in `bundle.mjs` with the options of goal 3; `manifest.test.ts` reads both option
  objects back. The bundle scan that already reads `dist/extension.js` reads `dist/webview/full.js` too (no source
  map, no `sourceMappingURL`, no machine path).
- **The harness widening** is the list of goal 4 and only that list, with the reason in the module's comment (the kit's
  `research/PLAN_kit_components_storybook.md` §4.3, kit `main` @ `1d2681cc`). `focus()` records `document.activeElement` (the harness
  document gains that read). `input` events read the element's `value`, which the harness already models.
- **No Dependabot change in W0** (corrected in the code round, 2026-10-21). Dependabot authenticates to GitHub Packages through the package's *Manage Actions access* grant to this repository (GitHub changelog 2026-09-08): no `dependabot.yml` change, no secret, no `ignore` entry, no `registries:` block. Without that grant the grouped npm update job for `src_vs_code` may fail entirely, security updates included. It is the same grant CI's install needs (goal 2); the README says so.
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
   (`--kit-fg`) and NO `url(`, `@import` or `@font-face` (the CSP is `default-src 'none'`); the bundle scan passes
   over `full.js`.
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

### 6a. The gate's plan round (session `e9ba8ab4`, 2026-10-20)

Two of three reviewers answered (kimi returned nothing parseable); `proceed` at 4 of 6. All five findings accepted and
folded in above: the package-access prerequisite, the token never beside a lifecycle script (`--ignore-scripts` +
`npm rebuild`), the exact lock-entry mechanism, no `url(` / `@import` / `@font-face` in `full.css`, `typecheck` over
both configs. The gate's "split into 2–3 epics" order does not fit: W0 is ONE story of E18's existing split
(PLAN_disk_health.md's E18 table), so it is not split again.

### 6b. The gate's code round (session e9ba8ab4, 2026-10-21)

12 reviewers answered; `proceed` (9/5, as the gate printed it). Accepted (as numbered by the gate): 0, 1, 2, 3, 4, 7, 8, 9, 10, 13, 14 — folded in:

- **A** the token step's `run`, trimmed, must EQUAL `npm ci --ignore-scripts` (a substring let `&&`, `;`, `||`, a second line, a comment or an `echo` of the flag pass); four chained forms and three more are fixtures that must be REJECTED.
- **B** the README tells a contributor to run `npm ci --ignore-scripts` with the token, unset it, then `npm rebuild` — never a plain `npm ci` with the token.
- **C** Dependabot authenticates through the package's *Manage Actions access* grant (GitHub changelog 2026-09-08): no secret, no `dependabot.yml` change, no `ignore` entry, no `registries:` block; without the grant the grouped npm update job may fail entirely, security updates included. README, `module_vs_code.md`, `module_tests.md` and this plan said "a Dependabot secret" — corrected.
- **D** the kit page test: a `dispose()`-ALONE case, an exact reveal cell and row before the host replies (no vacuous `includes(SECRET) === false`), and keydown on the FOCUSED tab.
- **E** `fullView.test.ts` RUNS the shipped `dist/webview/full.js` with and without a `view` mount: no throw, nothing posted, the API acquired once.
- **F** a host module importing the kit (`…/components`, `…/components.css`, the root) is refused, with planted companions.
- **G** the new C# test method is split into helpers (complexity ≤ 4).
- **H** this status line and the `todo/README.md` row.
- **I** the harness's `focus()` takes effect only on an ATTACHED button, input or element with a `tabindex`.
- **J** one list of bundles, `scripts/bundle-entries.json`, looped over by `bundle.mjs`; `manifest.test.ts` asserts every webview entry has the browser options; the kit page test bundles its entry with the webview entry's own options.
- **K** a missing package grant answers 401, 403 or 404, not only 403.
- **L** the `release-extension.yml` comments say `npm ci --ignore-scripts` + `npm rebuild` without the token.
- **M** `kitPageEntry.ts` replaces its `mode` object instead of mutating it.

Rejected, with the reason: **5** — the kit has no `/dom` export (its exports are `.`, `./components`, `./components.css`), so `KIT_ENTRIES` stays the two browser entries; **6** — `manifest.test.ts` compares the option source text by design (since J it compares the parsed list instead); **11** — the exact pin makes an export-map change a deliberate re-check of the boundary; **12** — one registry, and the workflow test already covers every install step whatever the registry.

The cadence consultation (c3c3a4c6) was solved by the Dependabot grant (C); the own Opus review's findings are folded into the list above.
### 7. What W0 does not prove

That a real webview loads `full.css` under the nonce (W1b's panel, and a real VS Code); that Dependabot can bump the pin
(it needs the same *Manage Actions access* grant, an owner setting); a layout — nothing is shown yet.

### 8. Definition of Done

- [ ] Goals 1–5 hold; RED items 1–9 seen red, then green; the break-its turned the right test red.
- [ ] CI green on both legs with the package fetched from GitHub Packages; the `.vsix` check green.
- [ ] The daemon tests that read `src_vs_code/` green; the family checks clean.
- [ ] Docs updated; the gate's plan and code rounds resolved; merged.

### 9. As built (2026-10-21) — what shipped differently

- **`npm ci --offline` does not work for the lock entry of § 3, step (e).** An entry with `version` and `integrity` and no `resolved` makes
  npm ask the registry for the packument BEFORE it looks at the cache, so after `npm cache add <tarball>` the answer is still
  `ENOTCACHED` (`request to https://npm.pkg.github.com/@oleksandrdubyna88%2fvscode-webview-kit failed: cache mode is 'only-if-cached'`).
  What was proved instead, against a throwaway registry on `127.0.0.1` serving the attested tarball: `npm ci --ignore-scripts` with the
  committed entry installs the kit (packument, then tarball, both requested), and the same entry against a registry serving one extra
  byte stops with `EINTEGRITY` (`wanted sha512-ydzXTgD0EuhVW… but got …`). The same entry WITH a `resolved` URL installs offline from
  the cache. The entry stays as § 3 specifies; whether to add `resolved` (then `npm ci --offline` works) is the owner's and the gate's
  call — CI's first run proves the registry road with a real token.
- **The workflow rule lives in the daemon's workflow tests, not in the extension's.** Item 3 is `ReleaseExtensionWorkflowTests` (two new
  tests; the job-permission table gained `packages: read` for `build` and `publish-marketplace`) because the repository's one YAML reader
  is there; the extension's tests have none.
- **The Marketplace job also installs the kit.** `publish-marketplace` runs `npm ci --ignore-scripts` for `vsce`, which installs every
  devDependency, so it too has `packages: read` and the token on that one step (and still no `npm rebuild`).
- **The page's mount point is `view`, not `root`** — `structure.test.ts` lets the word `root` appear in a shipped source only in the
  root-call module, and was not weakened.
- **`src/webview/stylesheets.d.ts`** declares the kit's stylesheet specifier: TypeScript 6 refuses a side-effect import it cannot resolve.
- **`src/test/webview/`** (the kit page test's entry) is excluded from the host's `tsconfig.json` and checked by `tsconfig.webview.json`
  with the page's own files; ESLint's type-aware rules read that program for both folders (`eslint.config.mjs`).
- **`resolveRelative`** was extracted from `structure.test.ts` into `support/sourceScan.ts` (one resolver for the two structural tests).
- **Not in W0:** the package's *Manage Actions access* grant to this repository (an owner setting; it serves CI and Dependabot alike); the first CI run's fetch from GitHub Packages (a 401, 403 or 404 there is that grant missing).
- **Code round (§ 6b), as built:** `dispose()` does NOT clear a revealed text (the kit's documented contract — a consumer clears it with `setRows([])` first), so the `dispose()`-alone test holds what the kit promises: the pending request is dropped, the late answer is `false`, the table stops reacting, and the revealed text stays. The list of bundles moved to `scripts/bundle-entries.json`; the harness's `focus()` is a no-op on a detached or non-focusable element.