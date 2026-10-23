# PLAN — E18 stories: wsl_care consumes the kit, the full view, the slim side bar, Help, accessibility (#87)

> Status: **in progress (2026-10-23)** — the stories document of epic E18 of [PLAN_disk_health.md](PLAN_disk_health.md)
> (§3.4–§3.6 and its E18 table). E18.K1 is done in `dew_flow_vscode_kit` (kit #9 → `655df4de`; **0.2.0 released to
> GitHub Packages**, release PR #16, run 39481672054 green). This document holds **W0**, **W1a** and **W1b**; W2, W4, W3 and W5
> are added here, each before its own plan round. **W0 is MERGED (2026-10-21, PR #171 → `main` @ `4d051378`)** — § 6b is its code round, § 9 what shipped differently. **W1a is MERGED (2026-10-22, PR #177 → `main` @ `2bbd8d30`)** — built as one branch in three commits (W1a § 9a), § 10 / § 10a what shipped differently and its review round. **W1b is specified (2026-10-22, § W1b — the full view) and its coai plan round passed (session `6fc851ec`, proceed 5/6, folded in at `e1ecf478`); its build has not started (2026-10-23). It waits for kit 0.3.0 (E18.K2, kit draft PR #19) only at W1b-3; W1b-1 and W1b-2 can start now. Before W1b merges, a Fable security check of the pick → target map, the closed wire and the reveal binding must say SHIP.**
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
- **No Dependabot change in W0** (corrected in the code round, 2026-10-21). Dependabot authenticates to GitHub Packages through the package's *Manage Actions access* grant to this repository ([GitHub changelog, 2026-09-08](https://github.blog/changelog/2026-09-08-automatic-dependabot-access-to-github-hosted-registries/)): no `dependabot.yml` change, no secret, no `ignore` entry, no `registries:` block. Without that grant the grouped npm update job for `src_vs_code` may fail entirely, security updates included. It is the same grant CI's install needs (goal 2); the README says so. Confirmed only when the first Dependabot npm run on `src_vs_code` succeeds.
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
- **C** Dependabot authenticates through the package's *Manage Actions access* grant ([GitHub changelog, 2026-09-08](https://github.blog/changelog/2026-09-08-automatic-dependabot-access-to-github-hosted-registries/)): no secret, no `dependabot.yml` change, no `ignore` entry, no `registries:` block; without the grant the grouped npm update job may fail entirely, security updates included. README, `module_vs_code.md`, `module_tests.md` and this plan said "a Dependabot secret" — corrected.
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

## W1a — the per-distro data path (cornerstone)

> Specified 2026-10-21 on `main` @ `4d051378` (W0 merged). A cornerstone of E18: W1b (the picker, the full view), W2 (the
> default distro's name in the side bar) and W3 (the Statistics tab's reads) all stand on it. **No UI ships here** — the
> side bar, the status bar, the panel, the Logs and Archive pages keep reading the DEFAULT distro and behave exactly as
> today; what changes is that every road to the daemon can be told WHICH distro, and the store can hold more than one. The
> owner's decisions it implements: [PLAN_disk_health.md](PLAN_disk_health.md) §3.6 item 3 (the VM's figures against the
> chosen distro's own; the picker; the side bar keeps the default; WSL 1 shows no VM figures) and its E18 table row W1a,
> including the gate's accepted findings of the 2026-10-15 plan round (ONE distro-aware entry point; a test enumerating
> every verb and action with the picked `-d`; maps bounded by `listDistros()`; a picked distro that stops mid-session; WSL 1).

### 1. Goal

What must be true when W1a is done:

1. **One entry point.** `WslCareClient` stays the ONLY speller of a `wsl.exe` argv word (`structure.test.ts:79-84`), and every
   public road into it — `run`, `read`, `terminalTarget`, `rootTarget`, and the new `listDistros` — takes the distro as an
   explicit, typed argument (`DistroTarget`, §3.1): the DEFAULT (the `wslCare.distro` setting, resolved inside the client as
   today) or a PICKED `DistroName` that only WSL's own listing can mint. No call site reads the setting to name a distro.
2. **`listDistros()`** answers `{ distros: [{ name, running, version, isDefault, stateText }], default }` from `wsl.exe`'s own
   two unlocalised signals (the `*` row and the running list) plus the version column, and every per-distro map in the
   extension is pruned to that list at every answer — a machine's distro count bounds them; no LRU.
3. **The store** holds the default's snapshot exactly as today (every reader of `snapshot()` keeps its meaning) and, beside it,
   one snapshot per picked distro. A SETTING change clears the default's (today's rule, `distroSwitch.test.ts`); a PICK never
   clears anything; a distro that leaves the list loses its entry at the next `listDistros()`.
4. **The poller** reads the default on its schedule as today, and a picked distro ONLY when asked (`readPicked`: on pick,
   *Refresh*, becoming visible — W1b's calls; no timer ever fires for a picked distro). A read never starts a distro: a picked
   distro that is stopped, or stops mid-session, answers the existing `stopped` outcome with no `-d` call. A WSL 1 distro
   — picked, or the EMPTY setting's default (a set setting naming one keeps today's road, § 7) —
   answers the new `wsl1` outcome ("WSL 1 — AI OS Care needs WSL 2") with no `-d` call, so no VM figure is ever read from it.
5. **Every action** — the seven root ops, the two config writes, the seven run reads, the install terminal, the explicit
   start — is reachable with an explicit distro; the modals and the journal already name theirs and go on doing so; the
   `distroChanged` guard keeps working.
6. **The enumeration test** (RED 20) runs every verb, run read, root op, config op, the terminal and every one of the 19
   contributed commands' flows against a picked distro and proves each argv carries the picked `-d` (or, for a Windows-side
   command, no `-d` at all) — built on the closed registers, so a verb added without a distro road fails there.

### 2. Where things are today (checked on `main` @ `4d051378`, 2026-10-21)

- **The client** (`src_vs_code/src/client/WslCareClient.ts`): `ClientOptions.distroSetting: () => unknown` (`:49`) is read at
  every call — `run` (`:128-140`, the in-flight key is the SETTING text: `${setting}|${verb}[+start]`, `:131`), `read`
  (`:174-189`, key `${setting}|${tail}`, `:180`), `terminalTarget` (`:147-155`, `['-d', distro, '--cd', '~']` at `:154`, no
  running check), `rootTarget` (`:161-165`). Resolution: `target()` (`:243-255`) → `launcher` (`:257-264`) → `listedDistro`
  (`:268-275`: the setting's pattern, then `--list --quiet`) → `chosenAmong` (`:277-281`) → `defaultDistro` (`:283-291`,
  `-l -v` + `parseDefaultDistro`) → `checkListed` (`:449-455`, refuses a leading `-`) → `isRunning` (`:294-298`,
  `--list --running --quiet`; any failure reads as not running) → `targetOf` (`:419-421`: the `stopped` outcome, no `-d`).
  `daemonArgv(distro, tail, asUser)` (`:100-102`) is also the builder `root/rootCall.ts:182` and `config/configCall.ts:59` use.
  The single slots `knownVersion` (`:115`, `:342`, `:347-349`) and `knownContainers` (`:117`, `:219-222`, `:234-236`) remember
  ONE distro each; `versionsInFlight` (`:114`, `:353-362`) is already a map by distro. No version column is parsed anywhere;
  nothing handles WSL 1. There is no `listDistros()`.
- **The registers**: `client/verbs.ts:14` (`VERB_NAMES`: status, preview, doctor, version), `:72` (`RUN_READ_NAMES`: runsShow,
  runs, logs, archiveStatus, archivePreview, archiveCheckBase, archiveList); `root/rootCall.ts:35` (`ROOT_OPS`: preview,
  confirm, stop, fullCheck, rootCheck, restorePreview, restoreConfirm) with `rootRequest(target, op)` pure (`:178-183`);
  `config/configCall.ts:27` (`ConfigOp`: setBaseFolder, clearBaseFolder) with `configRequest(target, op)` pure (`:58-60`).
  Both take a `{ wsl, distro }` target that `rootTarget()` answered — already explicit.
- **The distro parsers** (`src/wsl/distros.ts`): `DISTRO_NAME` (`:14`), `distroSettingText` (`:26-28`), `parseQuietList`
  (`:31-33`), `parseDefaultDistro` (`:40-47`, reads ONLY the `*` marker because the header and the state words are localised —
  measured, `wsl.test.ts:61-67`: `Wird ausgeführt` / `Beendet`). The measured `-l -v` layout (2026-10-03, WSL 2.7.10.0,
  [2026-10-03_wsl_exe_facts.md](../research/2026-10-03_wsl_exe_facts.md) row 3): `*` in column 0, names padded to 18, states to
  16, then the version digit. Only version `2` has been measured on this machine.
- **The store** (`src/state/outcomeStore.ts`): one `Snapshot { status, preview, doctor, windowsStatus?, checking }` (`:11-22`),
  `set` (`:34`), `setWindowsStatus` (`:39`), `setChecking` (`:43`), `clear()` keeps `windowsStatus` (`:51-54`), `onChange` (`:57`).
  Written only by the poller. Readers of `snapshot()`: `extension.ts:97` (`limitsOf`), `:413` and `:491` (the Logs and Archive
  pages' `status`), `:535` (`openRunLogs`); `statusBar/statusBar.ts:54`; `panel/panelProvider.ts:156`;
  `cleanup/cleanupHost.ts:63`, `:79`, `:150`, `:176`, `:185`, `:245`, `:291-293`; `archive/archiveHost.ts:42`, `:47`, `:52`.
  `onChange` subscribers: `panelProvider.ts:85`, `statusBar.ts:39`, `cleanupHost.ts:141` (kicks the follower),
  `extension.ts:499` and `:510` (the two pages).
- **The poller** (`src/poll/poller.ts`): `PollerOptions.target: () => string` (`:64`) is the setting text; `nextTargets`
  (`:107-111`) bumps a generation when it changes; `observeTarget` (`:307-313`) clears the store on a bump; `settle` (`:323-327`)
  writes only for the current generation; `tick` (`:148-155`, focused only), `askStatus` (`:162-164`), `refreshPanel` (`:167-169`,
  `panelRound` `:182-195`), `mirror` (`:206-210`) copies a stop into `preview` / `doctor`; `TARGET_FAILURES` (`:88-90`). The Windows
  read has its own slot and throttle (`:227-294`). Built in `extension.ts` `build()` (`:151-158` the client, `:159` the
  controller, `:160` the store, `:163` / `:212-222` the poller with `target: () => distroSettingText(distroSetting())`);
  `configurationChanged` (`:576-583`) re-reads on a `wslCare.distro` change.
- **The actions.** `root/cleanupController.ts`: `transaction` (`:234-246`: `client.rootTarget()` at `:235`, the per-distro
  `inFlight` set `:236-239`), `gated` (`:265-272`: `client.run('status')` at `:266`; `outcome.distro === target.distro` else
  `distroChanged`), `checkedRoot` (`:275-285`, `rootOk` by distro), `poll(distro)` (`:389-393`, `client.run('status')` at `:390`);
  entry points `rootCheck` (`:174`), `preview` (`:179`), `previewRestore` (`:191`), `confirm` (`:204`), `stop` (`:213`),
  `runFullCheck` (`:226`); their callers in `cleanup/cleanFlow.ts` (`:149`, `:159`, `:170`, `:181`, `:214`, `:282`, `:309`).
  Journal entries carry `distro` (`cleanup/journal.ts:72`, checked `:296`, `:304`); the follower matches answers by it
  (`cleanup/runFollower.ts:99`, `:116`, `:248`, `:445-453`, `:494`, `:502-504`, `:520`); the modals name `preview.distro`
  (`cleanup/modalText.ts:116`, `:149`, `:187`, `:201`). Archive: `archive/archiveFlow.ts:40` (`target: () => Promise<ConfigTarget | Failure>`),
  `:116-118`; `extension.ts:287-293` (`archiveHostOf`: `read`, `target: () => client.rootTarget()`); `archiveHost.ts:123-139`
  (`refresh`, `choose`, `stop`). Logs: `extension.ts:409-415`; `logsPage/logsController.ts:196`, `:232`, `:297`, `:341`, `:361`
  read `status()`. Install: `extension.ts:376-381`, `install/installDaemon.ts:42` (`target`), `:75-83`. Start WSL:
  `extension.ts:552`, `:596`; `panel/panelProvider.ts:127`. The host flows: `cleanup/cleanupHost.ts:154` (`clean`), `:162`
  (`archiveNow`), `:183` (`stopRelays`), `:213` (`restore`), `:233` (`runFullCheck`), `:244` (`stop`).
- **Tests and fakes.** `src/test/distroSwitch.test.ts` (11 tests, `:74-295`: a SETTING change clears the store and never
  mixes two distros — they stay valid, the setting still works that way; the PICK is new). `test/support/recordingRunner.ts`
  (exact-argv runner, `argvs()`; `verboseTable` hardcodes version `2`, `:50`). `test/fake/fakeWsl.ts` (the strict fake:
  `FakeDistro { name, running }` `:44-47`; `verboseTable` version `2` `:199-204`; a `-d` to a stopped distro refused unless
  `startable` `:388-403`; every call appended as a JSON line `{ argv, file, stdin? }` `:581-583`). `structure.test.ts`: `:79-84`
  only `WslCareClient.ts` spells `-d --cd --exec --list --quiet --running -l -v` and it spells ALL of them; `:87-89` only
  `verbs.ts` spells `--json --all --version`; `:101-105` the verb union is exactly four; `:246-248` only the cleanup controller
  imports `rootCall.ts`; `:250-255` / `:309-312` no `panel/`, `statusBar/`, `poll/`, `install/`, `state/`, `logsPage/`,
  `archivePage/` module imports `root/` or `config/`. `commandButtons.test.ts`: the 19 contributed commands
  (`package.json` `contributes.commands`) each have a panel button (`panel/commandButtons.ts:24-44`). The extension-host
  scenarios read every argv through `WslCareTestApi.calls()` (`src/testApi.ts:29`, `scenarios/clientFlows.test.ts:30`).
  The branded-id style the typed name follows: `root/rootIds.ts:41-47` (`RunId`, `VolumeName`: `string & { readonly __brand }`,
  one validator the only road in, `:85`).

### 3. Design

#### 3.1 The typed distro and the one entry point (`src/wsl/distroTarget.ts`, new)

```ts
/** A name wsl.exe LISTED — minted only by listDistros() (and, for tests, listedNameOf): never a raw string across a boundary. */
export type DistroName = string & { readonly __brand: 'DistroName' };
/** Which distro a call is about: the wslCare.distro setting (resolved inside the client, as today), or a picked, listed name. */
export type DistroTarget = { readonly kind: 'default' } | { readonly kind: 'picked'; readonly distro: DistroName };
export const DEFAULT_TARGET: DistroTarget = { kind: 'default' };
export function pickedTarget(distro: DistroName): DistroTarget;
/** `text` as a DistroName when it is one of `listed` and does not start with "-" (checkListed's rule); undefined otherwise. */
export function listedNameOf(text: string, listed: readonly string[]): DistroName | undefined;
/** A name persisted by the journal, re-minted at reload by its shape alone (§3.5) — WSL's list is the authority on the next call. */
export function persistedNameOf(text: string): DistroName | undefined;
/** The in-flight key's prefix: `default|<setting text>` or `picked|<name>` — a setting change still makes a new key (#23). */
export function targetKey(target: DistroTarget, settingText: string): string;
```

- It lives under `src/wsl/` so that `state/`, `poll/`, `panel/`, `install/` and the pages may import it — none of them may
  import `root/` or `config/` (`structure.test.ts:250-255`), and `root/rootIds.ts` is the wrong home for a type the poller needs.
- **Every public client method takes a `DistroTarget`, required, never optional**: `run(verb, target, options = {})`,
  `read(request, target)`, `terminalTarget(target)`, `rootTarget(target)`. A required parameter is the point: a call site that
  forgot the distro does not compile, and "the setting at the call site" cannot happen because the call site holds a token, not
  a text. Today's callers pass `DEFAULT_TARGET` (`extension.ts:214`, `:289`, `:380`, `:413`, `:491`; `cleanupController.ts:235`,
  `:266`, `:390` through their own new parameter, §3.5).
- **Resolution per target** (`target()` gains the parameter):
  - `default`: byte for byte today's road — the setting's pattern, `--list --quiet`, `-l -v` when empty, `checkListed`,
    `--list --running --quiet`. `clientFlows.test.ts:96` (`['--list --quiet']` first) stays green; the argv count per call is
    unchanged. One addition at no cost: when the empty setting's `-l -v` row for the `*` distro carries version `1`, the
    answer is `wsl1` (the table was read anyway). A SET setting naming a WSL 1 distro keeps today's behaviour (a `-d`, then
    `notInstalled` / `unsupportedDistro`) — stated in § 7, not covered: making that road read `-l -v` would change its argv
    and the measured churn, and the picker never takes that road.
  - `picked`: no pattern check (the name came from WSL's listing, §15h #4), ONE `-l -v` (`parseVerboseTable`, §3.2) that is both
    the listed check (`checkListed` over the table's names: an unregistered distro answers `distroRefused`) and the version
    read (version `1` → `wsl1`, before any running check), then `--list --running --quiet` → `targetOf` as today. Two `wsl.exe`
    starts per call, the same count as a SET setting's road today.
- **The `wsl1` outcome**: `Failure` gains `{ kind: 'wsl1'; distro: string }` (`client/outcome.ts:51-89`); `failureText.ts`
  words it "WSL 1 — AI OS Care needs WSL 2 (the daemon runs under systemd in the WSL 2 VM)"; the poller's `TARGET_FAILURES`
  (`poller.ts:88-90`) gains it so `preview` / `doctor` are mirrored, never asked; the bar's model treats it as it treats
  `unsupportedDistro`. No `-d` is ever built for it.
- **The in-flight keys** become `${targetKey(target, setting)}|${verb}[+start]` (`:131`) and `${targetKey(...)}|${tail}` (`:180`):
  the default's identity stays the setting TEXT (so `distroSwitch.test.ts:74-89` and `:295` hold unchanged), a picked call's is
  its name. A default call and a picked call for the same distro do not share — by design: sharing would couple the setting
  generation to a picked read.
- **`knownVersion` and `knownContainers` become maps by resolved distro name** (`Map<string, DaemonVersion>`,
  `Map<string, number>`), read and written exactly where the single slots are today (`:219-222`, `:234-236`, `:342`, `:347-349`);
  `versionsInFlight` is already one. All three are pruned by `listDistros()` (§3.2) to the names it reports.

#### 3.2 `listDistros()` and the verbose-table parser

```ts
export interface DistroInfo {
  readonly name: DistroName;
  /** From --list --running --quiet (unlocalised); never from the state word. */
  readonly running: boolean;
  /** The version column's digit; undefined when it is not 1 or 2. */
  readonly version: 1 | 2 | undefined;
  readonly isDefault: boolean;
  /** The state column as wsl.exe printed it (localised — shown, never judged). */
  readonly stateText: string;
}
export interface DistroList { readonly distros: readonly DistroInfo[]; readonly default: DistroName | undefined }
listDistros(): Promise<DistroList | Failure>
```

- **Two reads, not one, and the reason is measured.** `-l -v` gives the names, the `*` and the version; its STATE column is
  localised (`wsl.test.ts:65-66`), so "running" is taken from `--list --running --quiet`, the unlocalised question the client
  already asks (`:294-298`) — a distro not in that list is `running: false`, and a failed running question reads every distro
  as not running, today's rule for one distro (`:293`). The `state: 'other'` the story table once imagined is NOT offered:
  there is no unlocalised signal for "installing" / "converting", and a guess from a localised word would be wrong on every
  non-English machine. `stateText` carries the word for display.
- **`parseVerboseTable(text): VerboseRow[]`** (`src/wsl/distros.ts`, beside `parseDefaultDistro`, which is rewritten over it —
  one parser): per line, `^(\*?)\s+(\S+)\s+(.*?)\s+(\d+)\s*$`; the header has no trailing digit and is skipped; the state text is
  the middle (it may contain a space); a name starting with `-` is dropped (never a `DistroName`); a version digit other than
  `1` / `2` is `undefined`; two `*` rows → `default: undefined` (today's `parseDefaultDistro` rule, `wsl.test.ts:69-73`); an empty
  answer → `[]`. Fixtures: the measured table (`wsl.test.ts:62`), the localised one (`:65`), and a NEW mixed table with a
  version-`1` row and a two-word state — the WSL 1 row shape is Microsoft's documented layout, **not measured on this machine
  (no WSL 1 distro here)**; if a real WSL 1 machine prints otherwise, the fixture is corrected and the deviation recorded.
- **Failures**: a launch failure or a non-zero exit of either question is the `Failure` the client already builds (`wslText`,
  `:301-308`); `listDistros()` answers it as a whole (no partial list). A failed `-l -v` prunes nothing.
- **Pruning**: on every answered list the client drops `knownVersion`, `knownContainers` and `versionsInFlight` entries whose
  name is not listed, and returns the list; the store's `dropUnlisted` (§3.3) is called by the poller's `listDistros()` wrapper,
  not by the client (`state/` and `client/` stay unaware of each other).
- **Reuse**: the resolution road keeps its own three questions (§3.1) rather than being rebuilt over `listDistros()`: that
  would change the measured argv sequence every client test pins and the fake's expectations; a later story may unify them,
  with the churn numbers re-measured. Said here so the duplication is a decision, not an oversight.

#### 3.3 The store: distro → snapshot

- `OutcomeStore` keeps `snapshot()`, `set`, `setWindowsStatus`, `setChecking`, `clear()`, `onChange` with their meaning: they are
  the DEFAULT's. Added: `snapshotOf(distro: DistroName): Snapshot` (`EMPTY` when none — the same `EMPTY` the default starts
  from, so a reader needs no new case), `setPicked(distro, verb: PanelVerb, outcome)`, `setPickedChecking(distro, checking)`,
  `pickedNames(): readonly DistroName[]`, `dropUnlisted(listed: readonly DistroName[])`.
- **What a SETTING change clears**: `clear()` — the default's three outcomes and its `checking`, as today (`:51-54`); it keeps
  `windowsStatus` AND every picked snapshot. **What a PICK clears: nothing.** There is no `clearPicked`; a picked snapshot is
  replaced by the next read's outcomes, verb by verb, and removed only by `dropUnlisted`.
- **The slot is the asked name, never the answer's.** `setPicked(distro, …)` is called with the `DistroName` the read was started
  FOR; a failure carries no name, and an `answered` outcome's `distro` is the same name by construction (the client resolved the
  picked target to itself). Two distros' answers therefore cannot land in one slot (RED 13).
- `onChange` fires on every write, default or picked, with the DEFAULT snapshot as today: the bar and the panel re-render what
  they already show, which is bounded by picked reads (on pick / Refresh / visible only — no timer). The follower's kick
  (`cleanupHost.ts:141`) is harmless: it matches by distro. W1b may subscribe per distro; not here.
- **Growth surface (planning-docs rule)**: the picked map holds at most the machine's distros (bounded by `dropUnlisted` at every
  `listDistros()` — 3 on the owner's machine; a dozen is a large machine), each a `Snapshot` of three bodies (the `head` goldens:
  `status.json` ≈ 20 KB, `preview.json` ≈ 30 KB, `doctor.json` ≈ 5 KB) — projected ≈ 60 KB × N, under 1 MB at a dozen distros;
  in memory only, never persisted; retired by `dropUnlisted` and by the extension's disposal; nothing in flight survives a
  crash, so no sweep.

#### 3.4 The poller: the default on schedule, a picked distro on demand

- Unchanged: `start`, `focusChanged`, `settingsChanged`, `tick`, `askStatus()`, `refreshPanel`, the Windows read, the generation
  rule and `clear()` on a setting change. `PollerOptions.run` becomes `(verb, target, options?)`, and every existing call passes
  `DEFAULT_TARGET`.
- Added: `readPicked(distro: DistroName, options: RunOptions = {}): Promise<void>` — `status` for the picked target; then, unless
  `stopsTheOthers(status)` (now including `wsl1`), `preview` and `doctor`; each outcome into `setPicked(distro, verb, …)`, the
  stop mirrored into `preview` / `doctor` as `mirror` does; `setPickedChecking` around it. **No generation, no timer, no focus
  rule**: a picked read is asked by a user's act on a visible view (W1b's pick / *Refresh* / visible), so the "focused window
  only" rule of the schedule does not apply, and a setting change never obsoletes it (it is not about the setting). A picked
  read started while the same distro's read is in flight joins it (the client's in-flight sharing) — one start, one write.
  `options.startIfStopped` is the ONE way a picked distro is started, and only W1b's *Start <distro> and check* passes it.
- Added: `listDistros(): Promise<DistroList | Failure>` — the client's, then `store.dropUnlisted(names)` on an answer. W1b calls
  it on open / *Refresh* / visible; W1a's callers are the tests.
- `askStatus(target: DistroTarget)`: `default` → today's (`:162-164`); `picked` → `readPicked(distro)`'s `status` leg only. The
  follower (§3.5) asks with the journal entry's target.
- `extension.ts:212-222` (`pollerOf`) passes `run: (verb, target, options) => client.run(verb, target, options)`; `:576-583`
  is unchanged (a setting change still refreshes the default).

#### 3.5 Actions with an explicit distro

- `CleanupController`: `rootCheck(target)`, `preview(ids, target)`, `previewRestore(texts, target)`, `stop(runIdText, target)`,
  `runFullCheck(target)`; `confirm(preview)` keeps its signature — the `HeldPreview` the controller issued (`issued`, `:163`)
  gains the `target` it was previewed with, so a confirm can never be sent to another distro than its preview (the gate's
  "every action names its distro"). `transaction(target, body)` → `client.rootTarget(target)`; `gated(target, …)` →
  `client.run('status', target)`; `poll(expected)` → `client.run('status', expected.target)`. The `distroChanged` guard
  (`:270-272`) is unchanged and still correct: both sides come from the same `DistroTarget` resolution, so for a picked target
  they differ only when the distro was unregistered between the two reads (then `distroRefused` comes first). `inFlight`
  (`:236-239`) and `rootOk` (`:275-285`) are already by distro name.
- `CleanupHost`: `clean(rowIds, selected, target)`, `runFullCheck(target)`, `stop(index, target)`, `archiveNow(target)`,
  `restore(texts, target)`, `stopRelays(target)`; `cleanFlow.ts` passes it through to the controller (`:149`, `:159`, `:170`,
  `:181`, `:282`, `:309`). The journal entry (`journal.ts:68-79`) gains an OPTIONAL `targetKind: 'picked'` beside `distro`
  (absent = default, so every persisted entry from before W1a reads as today, `:296-304` unchanged for them); the follower
  builds the entry's `DistroTarget` from the two fields and asks `askStatus(target)`. A persisted picked name is re-minted
  through `persistedNameOf(text): DistroName | undefined` in `distroTarget.ts` (the `:296` shape check: text, at most 64, not
  starting with `-`; nothing else, because no list exists at reload time) — the brand is a compile-time promise, and the
  runtime authority stays the client's `checkListed` on the next call, which answers `distroRefused` for a name WSL no longer
  lists. An entry whose name fails that check is resolved as `unknown`, as a malformed entry is today. The `distro` check at `:502-504`
  is unchanged.
- `ArchiveHost`: `choose(target)`, `stop(target)`, `archiveNow(target)`; its options `read: (request, target)` and
  `target: (target) => client.rootTarget(target)`; `archiveFlow.ts:40` / `:116-118` pass it to `callConfig`. Its VIEW (`refresh`,
  `:123`; `this.status` / `this.preview`) stays the default's: a per-distro archive card is W1b's, built on `read(request, target)`.
- `installDaemon`: `InstallDeps.target` becomes `(target) => client.terminalTarget(target)`; `installer()` (`extension.ts:376-381`)
  takes a target and the command passes `DEFAULT_TARGET`. The terminal argv (`:154`) carries the resolved name as today.
- Start: `wslCare.startWsl` stays `refreshAll({ startIfStopped: true })` (default); a picked start is
  `poller.readPicked(name, { startIfStopped: true })` — the explicit act, never a read.
- The Logs and Archive pages read `client.read(request, DEFAULT_TARGET)` and the default's `status` (no UI change). The 19
  commands and `commandButtons.ts` are unchanged (W1b adds `where: 'full'`).
- `WslCareTestApi` (`src/testApi.ts`) gains `readPicked(name: string, options?)` (minted through `listedNameOf` against the
  fake's list — a name not listed throws, a test defect) and `listDistros()`, so the host scenarios can drive a pick with no UI.

### 4. RED list (each seen failing for the real reason first; the slice it belongs to in brackets, § 9)

Client and list [W1a-1]:

1. `wsl.test.ts`: `parseVerboseTable` over the measured table gives three rows, `Ubuntu` marked, every version `2`; over the
   localised table the two-word state `Wird ausgeführt` is one row's `stateText`; over the mixed fixture the WSL 1 row reads
   `version: 1`; a `3` reads `undefined`; a `-` name is dropped; the header is skipped; `parseDefaultDistro` still answers as
   `:61-73` require (it now runs over the same parser).
2. `client.test.ts`: `listDistros()` sends exactly `-l -v` then `--list --running --quiet` (the recording runner's `argvs()`), and
   answers `{ name, running, version, isDefault, stateText }` per row with `default` the `*` row; a distro missing from the
   running list is `running: false`; a failed running question makes every distro `running: false` and is not a failure of the
   list; a failed `-l -v` is the `Failure` and nothing else.
3. `client.test.ts`: `run('status', pickedTarget('Debian'))` sends `-l -v`, `--list --running --quiet`, then `-d Debian …` — and
   NEVER `--list --quiet`; `run('status', DEFAULT_TARGET)` sends today's sequence unchanged (`clientFlows.test.ts:96` stays).
4. `client.test.ts`: a picked distro not in the `-l -v` table answers `distroRefused` with no `-d`; a picked name starting with
   `-` cannot be minted (`listedNameOf` → `undefined`).
5. `client.test.ts`: a picked distro that is not running answers `{ kind: 'stopped', distro }` with no `-d`; with
   `{ startIfStopped: true }` the `-d` is sent (today's rule, now for a picked target).
6. `client.test.ts`: a picked WSL 1 distro (version `1`, running) answers `{ kind: 'wsl1', distro }` with NO `-d` — and no
   `--list --running --quiet` either (the version is judged before the running check); the empty-setting default resolving to
   a WSL 1 `*` row answers `wsl1` with no `-d`.
7. `client.test.ts`: two picked distros' `preview` calls in flight at once are two calls with two keys; a default call and a
   picked call for the same name are two calls (never shared); `distroSwitch.test.ts` 11 tests unchanged and green.
8. `client.test.ts`: `knownVersion` / `knownContainers` hold one entry per distro: a `status` of Debian then a `preview` timeout
   of Ubuntu is judged against UBUNTU's container count (`previewTooManyContainers` only when Ubuntu's count exceeds the
   assumption), and Debian's `--version` is not asked again after Ubuntu's; after a `listDistros()` that no longer lists Debian,
   Debian's entries are gone (the next Debian call asks `--version` again).
9. `failures` / `failureText` tests: `wsl1` has a sentence naming WSL 2; the bar's model shows it like `unsupportedDistro`;
   the poller's `TARGET_FAILURES` has it (`stopsTheOthers`).
10. `fakeWsl.test.ts`: `FakeDistro` gains `version?: 1 | 2` (default `2`); `-l -v` prints the digit; a `-d` to a version-`1` distro
    is refused with its own exit code and sentence ("the daemon needs WSL 2") — stricter than the real `wsl.exe`, which would
    run it; `recordingRunner.verboseTable` takes the same optional version.
11. `structure.test.ts`: the argv-word rule (`:79-84`) and the verb union (`:101-105`) still hold with `distroTarget.ts` and
    `listDistros` in place — no new speller; `src/wsl/distroTarget.ts` imports nothing under `src/root/`, `src/config/` or
    `src/client/`; the FAR_FROM_ROOT rule (`:250-255`) still holds.

Store and poller [W1a-2]:

12. `outcomeStore` tests: `setPicked('Debian', 'status', …)` leaves `snapshot()` (the default's) untouched; `snapshotOf('Debian')`
    reads it; `snapshotOf('Alpine')` is `EMPTY`; `clear()` empties the default's three outcomes and keeps `windowsStatus` AND
    every picked snapshot; `dropUnlisted(['Ubuntu'])` removes Debian's and keeps the default's; `onChange` fires on a picked write.
13. `poller.test.ts`: `readPicked('Debian')` asks `status`, then `preview` and `doctor`, each stored under Debian; NOTHING is
    stored in the default's slot; a tick between them stores Ubuntu's `status` in the default's slot and never under Debian;
    with the deferred-promise pattern of `distroSwitch.test.ts:48-72`, Ubuntu's `preview` resolving AFTER Debian's lands in
    Ubuntu's slot — the two never mix.
14. `poller.test.ts`: over a simulated day (the `ManualClock`, `:19-47`), one `readPicked('Debian')` is exactly one `status` +
    `preview` + `doctor` for Debian and the schedule's ticks are all `DEFAULT_TARGET` — no timer ever asks a picked distro.
15. `poller.test.ts`: a setting change (`target()` text changes) clears the default's snapshot and NOT Debian's; a
    `readPicked` in flight across that change still stores under Debian (no generation applies to it).
16. `poller.test.ts`: a picked distro whose `status` answers `stopped` stores `stopped` under Debian for all three verbs and
    asks no `preview` / `doctor`; `wsl1` likewise; `readPicked` never passes `startIfStopped` unless given it.
17. `poller.test.ts`: the poller's `listDistros()` calls `dropUnlisted` with the answered names, so a Debian that was unregistered
    loses its snapshot; a failed list drops nothing.

Actions and the enumeration [W1a-3]:

18. `cleanupController.test.ts`: `preview(ids, pickedTarget('Debian'))` sends `-d Debian -u root … act … --preview`; the held
    preview's confirm goes to Debian even when the DEFAULT is Ubuntu; `rootCheck`, `stop`, `runFullCheck`, `previewRestore` with
    the picked target each carry `-d Debian`; `gated`'s `distroChanged` still fires when the status answers for another distro.
19. `cleanupHost.test.ts` / `runFollower.test.ts`: a journal entry written for a picked run carries `targetKind: 'picked'` and
    its distro; after a (simulated) reload the follower asks `askStatus` with the PICKED target and resolves the entry from
    Debian's answer; an entry without `targetKind` (pre-W1a) asks the default as today. `archiveFlow.test.ts`: `setBaseFolder` /
    `clearBaseFolder` with a picked target carry `-d Debian`. `installDaemon.test.ts`: the terminal is `-d Debian --cd ~`.
20. **The enumeration** (`src/test/distroEnumeration.test.ts`, new, over the recording runner; the fake's list is `Ubuntu` (default)
    and `Debian`, both running, version 2): with the picked target `Debian` it runs every `VERB_NAMES` entry through `run`, every
    `RUN_READ_NAMES` entry through `read` (valid values), `terminalTarget`, every `ROOT_OPS` entry through `rootRequest` on
    `rootTarget(picked)` (A4 with a shown list, A21 with one process, A20 with one entry), every `ConfigOp` through
    `configRequest`, and every host flow (`CleanupHost.clean / runFullCheck / stop / archiveNow / restore / stopRelays`,
    `ArchiveHost.choose / stop / archiveNow`, `installDaemon`, `readPicked(…, { startIfStopped: true })`). Three assertions:
    (a) every recorded argv that carries `-d` carries `-d Debian` and none carries `-d Ubuntu`; (b) the SET of daemon tails seen
    equals the union of the closed registers (`VERBS`, `RUN_READ_VERBS`, the seven root tails, the two config tails, the terminal)
    — a verb or op added to a register without reaching the picked road fails here, so the registers are the oracle; (c) every
    one of the 19 `COMMAND_BUTTONS` keys is classified in the test as `distro` (its flow was driven above and carried `-d Debian`)
    or `windowsSide` / `opener` (`startWindowsTime`, the guard, the five Windows tasks, `enableGpu`, `openLogs`, `openArchive`,
    `openPanel`: driven, and no `-d` at all) — a 20th command without a classification fails.
21. **The host scenario** (`scenarios/clientFlows.test.ts`, `test:host`, the strict fake's log through `api.calls()`): with the
    fake listing Ubuntu (default) and Debian, `api.readPicked('Debian')` produces `-l -v`, `--list --running --quiet` and three
    `-d Debian` daemon calls, the status bar still shows Ubuntu's verdicts, `api.lastRendered()` is unchanged by the picked read;
    `api.readPicked('Debian')` while the fake marks Debian stopped produces no `-d` and the fake's log holds no `wouldStart`
    refusal; with Debian at `version: 1` no `-d` and no `--list --running --quiet`.
22. `commandButtons.test.ts` and `extensionWiring.test.ts` unchanged and green (no UI, no new command).

### 5. Break-its (product code, each restored byte for byte, SHA-256 compared)

Make `run`'s key ignore the target → RED 7 red. Resolve a picked target through `--list --quiet` without `-l -v` → RED 3 and 6
red. Drop the version check before the running check → RED 6 red (the fake's own WSL 1 refusal fires, RED 10's tripwire). Have
`readPicked` write through `set` instead of `setPicked` → RED 13 red. Let `clear()` empty the picked map → RED 12 and 15 red.
Remove `dropUnlisted` from the poller's `listDistros()` → RED 17 red. Give `HeldPreview` no target and confirm with the DEFAULT
→ RED 18 red. Add a verb to `VERB_NAMES` without a tail → the compile fails (`VERBS` is typed by it); add a root op with a tail
but route it through `rootTarget(DEFAULT_TARGET)` → RED 20(b) red. Never a tripwire disabled; never the fake made more permissive.

### 6. Build order

1. This section; the plan round (coai, the whole of W1a in one round; a code round per pull request, § 9).
2. **W1a-1** `src/wsl/distroTarget.ts`, `parseVerboseTable`, RED 1–11, then the client (`target()` per target, `wsl1`,
   `listDistros`, the maps, the keys), the fake and the recording runner, `failureText`, every call site passing
   `DEFAULT_TARGET`. Full suites green; docs (`research/module_vs_code.md`: the client's row and the §15h sequence diagram
   gain the picked road and `listDistros`; `module_tests.md`: the fake's `version`).
3. **W1a-2** RED 12–17, then the store's picked map and `dropUnlisted`, the poller's `readPicked` / `listDistros` /
   `askStatus(target)`, the test API's `readPicked` / `listDistros`. Docs: `module_vs_code.md` rows `OutcomeStore` and `Poller`
   (`:156-157`), the store diagram (`:561`); `architecture.md` only if the container diagram names the store's shape.
4. **W1a-3** RED 18–22, then the controller, the host, the journal field, the follower, the archive host and flow, the install,
   the enumeration test and the host scenario. Docs: `module_vs_code.md` (the cleanup, archive and install rows: the target
   parameter; the journal entry's optional field), `research/architecture-extension-e6.md` if it draws the journal entry.
5. After the third merge: the E18 table row W1a in [PLAN_disk_health.md](PLAN_disk_health.md) marked built; this section's
   *as built* subsection with the deviations; the `todo/README.md` row.

### 7. What W1a does not prove

- A real `wsl.exe -l -v` on a machine with a WSL 1 distro (the row shape is documented, not measured here) — the first WSL 1
  user's answer corrects the fixture, and the `wsl1` sentence is the only thing shown until then.
- A SET `wslCare.distro` naming a WSL 1 distro: still a `-d` and today's `notInstalled` / `unsupportedDistro` (§3.1).
- A WSL default changed mid-round while the setting is empty: already stated as not covered (`module_vs_code.md:545-547`); unchanged.
- The picker, the full view, a per-distro archive card and `where: 'full'` buttons (W1b); the default distro's name in the side
  bar (W2). Docker Desktop's own distro shown read-only (§3.6) is W1b's rule over `listDistros()`'s names.
- The remaining ~50 ms race of §15g m3 (a distro stopping between the running check and the `-d`) applies to a picked distro
  exactly as to the default; stated, not closed.

### 8. Definition of Done

- [ ] Goals 1–6 hold; RED 1–22 seen red for the real reason, then green; the break-its of § 5 turned the named test red.
- [ ] Every public client method, controller entry point, host flow, archive flow and the install take a `DistroTarget`; no call
      site names a distro from the setting; `structure.test.ts` unchanged and green (no new argv speller; `state/`, `poll/`,
      `panel/`, `install/` import no `root/` or `config/`).
- [ ] The default's behaviour is byte-for-byte today's: `distroSwitch.test.ts`, `poller.test.ts`'s churn numbers,
      `clientFlows.test.ts` and the host suites green without edits to their expectations (new tests only).
- [ ] `listDistros()` prunes every per-distro map; the growth line of §3.3 holds.
- [ ] Docs of § 6 updated; the gate's plan round and each pull request's code round resolved; the three pull requests merged in
      order; the E18 table row and this section's *as built* written.

### 9. Size, and the recommended split

Counted against § 3: W1a touches `client/` (the client, `outcome.ts`, `failureText.ts`), `wsl/` (two files), `state/`, `poll/`,
`root/cleanupController.ts`, `cleanup/` (host, flow, journal, follower), `archive/` (host, flow), `install/`, `extension.ts`,
`testApi.ts`, the fake, the recording runner, and some twenty test files — three of the extension's boundaries
(`structure.test.ts`'s argv rule, the root import rule, the config import rule) in one diff, with a RED list of 22. One pull
request of that size gets a code round nobody can read in one sitting, and a reviewer who cannot read it accepts it. **Split it,
sequentially, each pull request leaving `main` behaving as today** (every caller passes `DEFAULT_TARGET` until the next slice):

| Pull request | Contents | RED | Why it stands alone |
|---|---|---|---|
| **W1a-1** client + list | `distroTarget.ts`, `parseVerboseTable`, `listDistros`, the target parameter on the four client roads, `wsl1`, the per-distro maps and keys, the fake's `version` | 1–11 | the root and config requests already take a resolved target, so the whole picked road is provable from the client alone |
| **W1a-2** store + poller | the picked map, `dropUnlisted`, `readPicked`, `askStatus(target)`, the poller's `listDistros`, the test API | 12–17 | nothing calls `readPicked` but tests; the side bar cannot tell |
| **W1a-3** actions | the controller, host, journal field, follower, archive host and flow, install, the enumeration test, the host scenario | 18–22 | the enumeration is the cornerstone's proof and closes W1a |

One plan round for the three (this section is the plan), one code round each. W1b starts after W1a-3 is merged — the full
view's buttons need the host flows' target, and its picker needs `listDistros()` and `readPicked`. **Recommendation: split.**

### 9a. The gate's plan round (session `d18198b1`, 2026-10-21)

Two of three reviewers answered (kimi unparseable); `proceed` at 5 of 6. All six findings accepted and folded in here — they
bind the build as much as §§ 3–4 do:

1. **A failed running question is not "stopped".** `DistroInfo.running` is `boolean | undefined`: `undefined` when
   `--list --running --quiet` failed (the picker shows "state not known", never "stopped"). The PICKED resolution keeps
   today's safe direction — a failed running question answers `stopped` with no `-d` (a read never starts a distro) — and
   RED 2 / RED 5 pin both halves.
2. **Goal 4 narrowed** to the picked target and the EMPTY setting's default (a set setting naming a WSL 1 distro keeps today's
   road, § 7).
3. **`rootTarget(picked)` and `terminalTarget(picked)` never start a distro behind the user's back.** `rootTarget(picked)`
   goes through the full picked resolution (listed, version, running): a stopped distro answers `stopped`, a WSL 1 one
   `wsl1`, and NO target is handed out — so no root op or config write can build a `-d` for it. `terminalTarget(picked)`
   applies the listed and version checks (`wsl1` refused, no terminal) and answers whether the distro is running; the install
   modal (W1b's for a picked distro; today's for the default, unchanged) says "<distro> is stopped and will be started" when it
   is — the user's explicit act, as *Start … and check* is. RED 4b: `rootTarget(picked stopped)` and `rootTarget(picked WSL 1)`
   answer the Failure and the recording runner holds no `-d`; `terminalTarget(picked WSL 1)` answers `wsl1`.
4. **No write for a distro the list dropped.** The store keeps the names of the last `dropUnlisted(listed)`; `setPicked` /
   `setPickedChecking` for a name not in that set are ignored (a `readPicked` that resolves after its distro was unregistered
   re-inserts nothing). A pick always comes from a list, so a fresh store's first `readPicked` follows a `listDistros()`;
   W1a-2's test API calls `listDistros()` first. RED 17b: a `readPicked('Debian')` whose `preview` resolves after a list
   without Debian leaves no Debian snapshot.
5. **`stopsTheOthers` is extended explicitly** (`poller.ts`, beside `TARGET_FAILURES` `:88-90`) to stop on `wsl1`; RED 16 asserts
   `readPicked` of a WSL 1 distro asks no `preview` / `doctor` and mirrors `wsl1` into both.
6. **A row whose name fails `DISTRO_NAME` (`src/wsl/distros.ts:14`) is dropped** by `parseVerboseTable` and never minted (a
   name with a space, as a side-loaded distro might carry, cannot be a `-d` value the client accepts anyway); RED 1 gains a
   fixture row with a spaced name that is dropped while its neighbours parse.

The gate's operator order "this plan is a piece of a split already under way — build it as one unit" is applied: W1a is
built on ONE branch and goes through ONE code round and ONE pull request, as three commits in the order of § 9
(W1a-1 client + list, W1a-2 store + poller, W1a-3 actions + enumeration), so a reader can still take it a slice at a time.
§ 9's three-PR recommendation is recorded as what this would have been cut into.

### 10. As built (2026-10-21)

Built on ONE branch (`feat/wc-e18-w1a-distros`) as three commits in the order of § 9 — W1a-1 client + list, W1a-2 store + poller, W1a-3 actions +
enumeration + host scenario. Every extension caller passes `DEFAULT_TARGET`; the existing expectations (`distroSwitch`, `poller` churn numbers,
`clientFlows`, the host suites) are unchanged — their call sites gained the target argument and nothing they assert moved. What shipped differently:

- **One parser, two answers.** `parseVerboseTable` returns `{ name, marked, stateText, version }` and drops a name failing `DISTRO_NAME` or holding
  a space (the header's state column tells a spaced name from a two-word state). `parseDefaultDistro` runs over the same rows through `defaultRow`
  WITHOUT the name filter: a default named `-x` must still answer `distroRefused` naming the option rule (`client.test.ts`), where the picker simply
  never offers it. `listDistros` builds its answer in a new pure module, `src/wsl/distroList.ts` (not in the client).
- **`wsl1` words:** bar label `needs WSL 2`; the strict fake's refusal is `FAKE_EXIT.wsl1` = 94. `TerminalTarget` gained the optional `running`.
- **`installDaemon(deps)` kept its one-parameter signature** (its test pins "no parameter a page value could reach"): the extension's `installer(parts, target)`
  binds the `DistroTarget` into `deps.target`, instead of `InstallDeps.target` taking it.
- **The follower asks per entry.** A journal entry's `targetKind: 'picked'` + `distro` build its target (`entryTarget`); `RunFollower` asks `status` once per
  DISTINCT target its entries follow — the DEFAULT always, and first (review round: the default's observation must not stall because every entry is a picked one),
  the picked ones at most `settleAtOnce` at a time — and reads `runs show` / `runs` from the entry's distribution; a picked entry whose status answers `distroRefused`
  (the distribution was unregistered) is resolved as `unreadable` ("state unknown — the record could not be read (distroRefused)") and followed no more. The observing poll stays the default's.
  The enumeration (RED 20) never ticks the follower; these rules are held by `runFollowerPicked.test.ts`.
- **`ArchiveHost` does not re-read its (default) view after a picked write** — the view is the default's; a per-distro card is W1b's. `archiveNow(picked)` is judged on the picked
  distribution's own `status` (capabilities, A13, a failed status) and on its OWN `archive status` read once for the folder (review round, B).
- **A picked full check or stop** is written for the picked name and needs no default `status`; `CleanupHost.controls(aim)`, `restoreBlocker(aim)`, `runFullCheck`, `archiveNow`, `stopRelays` and `stop`
  gate on THE AIM'S snapshot (`snapshotOf`), never the default's (review round, B). `HeldPreview` and `PreviewContext` carry `target`.
- **What RED 20(c) proves, after the review round.** The 19 commands are classified (`distro` — a flow the enumeration drives; `opener`; `windowsSide`; and `startWindowsTime` as `defaultOnly`, because its `afterDone`
  runs *Run full check now* for `DEFAULT_TARGET` — pinned by a match on `extension.ts`). An AST scan of `extension.ts` proves every call it makes on a WSL road (`host`, `archive`, `client`, `poller` members)
  names `DEFAULT_TARGET` or forwards a `target` — nothing in the extension can name a distribution from the setting — with a planted-violation companion. It does NOT run the windows-side commands (they need
  VS Code) and does not prove at runtime that they never reach `wsl.exe`; the earlier import scan of the windows-side modules was dropped as proving less than it claimed.
- **What RED 20(a)/(b) prove, after the review round.** (a) every recorded call is `-l -v`, `--list --running --quiet` or starts with `-d Debian ` (a call to the default road's `--list --quiet` fails); (b) the SET of
  daemon-call shapes (root/user, tail with values replaced) EQUALS the set the registers build — `VERBS`, `runReadTail` over every read, `rootRequest` over every root op plus the confirm's plain and A21 shapes,
  `configRequest` over both ops — both directions; every flow's outcome is asserted (`handedOff:accepted`, `written`, `typed`, `answered` …), so a flow that ended in a fake refusal does not count as reached. The host-flow list is
  `HOST_FLOWS`, a typed table: a flow added without a row does not compile. The plan §5 break-it "a root op routed through the default" is made concrete and proved (below). Running the strengthened test found that a Debian run left in the
  journal correctly greys the next Debian flow (the old gating read the DEFAULT's snapshot and missed it), so the test ends each handed-off run by hand.
- **RED 21 lives in `test/host/suite.ts`** (`test:host`), not in `scenarios/clientFlows.test.ts` (a node test, which has no test API): three scenarios assert the WHOLE call log with the window unfocused (no scheduled tick) —
  a running Debian is the list round (`-l -v`, `--list --running --quiet`), then status, then preview and doctor, EACH behind the picked road's own two questions (11 calls; preview / doctor interleave); a stopped Debian
  is two list rounds and nothing else, run against a `startable` fake and followed by a read of the fake's scenario file proving Debian is still `running: false`; a version-1 Debian is the list round and the picked `-l -v` alone.
- **Not red-first, said plainly:** `installDaemonPicked.test.ts` was green on arrival and pins the CLIENT's picked terminal road through the same `InstallDeps` shape — NOT the extension's `installer(parts, target)` binding,
  which needs VS Code and is held only by the road scan above (the installer's `terminalTarget(target)` call forwards its `target`); the enumeration (RED 20) and the host scenarios (RED 21) were written after their product slice,
  their teeth are the break-its below; `statusBar` / `poller` wsl1 cases and the structure scan were red-first.
- Test support added: `support/manualClock.ts` (the clock `poller.test.ts` had inline), `support/distroNames.ts` (`listed` / `pick`), `NO_DISTRO_LIST` in `support/outcomes.ts`;
  `Poller` has a required `listDistros` option and the test API `listDistros()` / `readPicked(name, options?)` (the list first, an unlisted name throws).

**Break-its** (each restored byte for byte, SHA-256 compared, never a tripwire touched): the in-flight key without the target → `clientDistros` 2 red; the picked road through
`--list --quiet` → 6 red; the version check dropped → the 3 wsl1 tests red; `listDistros` not pruning (`knownVersion`, `knownContainers`) → 1 red each; a cast into `DistroName` in the client →
the structure test red; `wsl1` out of `TARGET_FAILURES` → the poller test red; `readPicked` writing through `set` → 9 red; `clear()` emptying the picked map → 3 red; the poller not calling
`dropUnlisted` → 10 red; the store accepting an unlisted name → 3 red; the confirm resolved through the default instead of the preview's target → the controller test red (the
enumeration stays green there: the `distroChanged` guard refuses the call, so nothing wrong is SENT); `rootTarget(picked)` routed through the default → the enumeration + 2 client tests red;
the archive flows ignoring the target → 3 red, the enumeration among them; the journal dropping `targetKind` → 8 red; (the first design's "follower always asks the default" break-it is obsolete — the review round reversed that rule); a verb added to
`VERB_NAMES` or an op to `ROOT_OPS` without its row → the compile fails (`VERBS`, `TAILS`, the enumeration's typed tables); `askPicked` through `DEFAULT_TARGET` → all 3 host scenarios red.

**Final counts (extension, after the review round):** typecheck and lint clean; `npm test` 1751 tests, 1750 pass, 0 fail, 1 skipped (a first full run under heavy machine load — Docker Desktop and others — timed out 26 fake-spawning tests; all 133 of them
pass alone, and the quiet re-run of the whole suite is the count above); `test:host` green on VS Code 1.85.0 and stable, fake and closed; `check:vsix` OK. Daemon classes that read `src_vs_code` (Release build, 0 errors; main merged): Scenarios 146 total / 0 failed / 55 skipped (the environment's), Core 34 / 0, Cli 22 / 0 over
`ReleaseExtension`, `Architecture`, `InteropModulePath`, `FullCheckLine`, `FixturePrivacy`, `ExtensionPackageName`, `ReleaseConfig`. Family checks clean. The WSL 1 row shape is still Microsoft's documented layout, not measured here (§ 7).

### 10a. The review round (2026-10-21)

The coai code round (session d18198b1, 12 reviewers, verdict `good_enough`, 14 findings of which 5 not accepted) and an Opus review of the built branch (no defect on a shipped road; the cornerstone's PROOF was too weak).
`origin/main` (daemon 0.6.0, #163 — daemon only) was merged into the branch before the round. All work is in one more commit.

**Accepted from the code round — 0, 1, 2, 3, 4, 5, 6, 8, 12, 13, 14, 15 (grouped as A–E of the round's brief):**

- **A. Immutability.** `WslCareClient`'s `knownVersion`, `knownContainers` and `versionsInFlight` are replaced, never edited (`withEntry` / `withoutEntry` / `onlyListed`), and so are `CleanupController.rootOk`.
- **B. Picked host flows gate on the PICKED distribution's snapshot.** `CleanupHost.controls(aim)`, `restoreBlocker(aim)`, `runFullCheck`, `archiveNow`, `stopRelays`, `stop` read `snapshotOf(distro)`; `ArchiveHost.archiveNow(picked)` is judged on
  the picked `status` and its own `archive status`. RED (`hostGatingPicked.test.ts`, the two distributions holding opposite facts): `Expected values to be strictly deep-equal` — the controller was reached, or not, by the DEFAULT's state — in 5 of 7.
- **C. The follower.** (1) it always asks the default's `status`, even when every entry is picked — RED `Expected values to be strictly deep-equal` (the default was missing); (2) its per-target asks run at most `settleAtOnce` at a time — RED `the default and the
  five picked distributions were all asked: 5 !== 6` (the bound is asserted as peak concurrency ≤ 2); (3) a picked entry whose status answers `distroRefused` is resolved as
  `unreadable` and followed no more — RED `0 !== 1` shown results; a default entry's refusal is not resolved by it.
- **D. Types.** `DistroInfo.name` and `dropUnlisted`'s argument were already `DistroName`, minted through `listedNameOf` (the only cast into the brand is in `distroTarget.ts`, held by the structure scan); the test fixtures use `listedNameOf`. Verified, nothing to change.
  The client's `knownVersion` / `knownContainers` stay keyed by `string` (rejected finding 7 below).
- **E. `distroList.ts`'s default** already reuses `parseDefaultDistro` (two `*` rows → no default, neither row `isDefault`); a test now pins it (`clientDistros.test.ts`, green on arrival).

**Rejected, with reasons:**

- **7** — the maps serve the default road too, where the key is the name WSL printed (and may fail `DISTRO_NAME`): `string` is the honest key.
- **9 and 10** — copying a `Map` copies references, not the answers; N is the machine's distro count, and the repository's immutability rule is about the map binding, which is now replaced (A).
- **11** — the version is judged before the running question, as the picked run road does (spec §3.1; RED 6).

**From the Opus review (F–H):**

- **F. RED 20 strengthened** (see the corrected bullets of § 10): (a) every call is a picked-road list question or `-d Debian …`; (b) SET equality of daemon-call shapes with the registers' union, both ways; every flow's outcome asserted; `HOST_FLOWS` typed; (c) `startWindowsTime` reclassified
  `defaultOnly` (its `afterDone` runs `runFullCheck(DEFAULT_TARGET)`), the import scan replaced by an AST scan of `extension.ts`'s road calls. The plan § 5 break-it is proved: `controller.stop` routed through the default → the enumeration red; so is `previewRestore`.
- **G. RED 21** asserts the whole call log (§ 10), with a `startable` fake and a read of the fake's scenario file for the "was it started" check; the break-it `targetOf(…, true)` in the picked resolution turns the stopped host scenario and three `clientDistros` tests (RED 5 among them) red.
- **H.** `CleanupController.forgetUnlisted` prunes `rootOk` on every answered list (wired in `extension.ts`); a late `remember` / `rememberVersion` after `forgetUnlisted` re-inserts nothing (`mayRemember`, guarded by the last listed set; before the first list everything is remembered, so the
  default road is unchanged — after one, a distribution WSL listed under a name `parseVerboseTable` drops, e.g. one with a space, is simply not cached and its `--version` is asked again); § 10's follower sentence and its `installDaemonPicked` claim are corrected above.

**Break-its of the round** (restored byte for byte, SHA-256 compared): the `mayRemember` guard removed → the late-answer test red; `forgetUnlisted` of `rootOk` removed → its test red; `controller.stop` via the default → the enumeration red; `previewRestore` via the default → the enumeration red;
`targetOf(…, true)` in the picked resolution → 3 `clientDistros` tests (RED 5, the failed running question, `rootTarget(picked stopped)`) and the stopped `test:host` scenario red.

**The security check of the picked root road (Fable, verdict SHIP — nothing Blocking or Major).** Its cheap minors, fixed in one more commit, RED-first:

1. **Only a provable WSL 2 gets a `-d`.** `pickedListed` answers `wsl1` for version 1 and the new `versionUnknown` ("WSL did not report … as WSL version 1 or 2") for a version column that is neither (a `3`); a row with no version column is no row, so
   the name is `distroRefused`. Chosen over reusing `wsl1` because "is a WSL 1 distribution" would be false for a 3. `versionUnknown` is in the poller's `TARGET_FAILURES`. RED: the picked `run`, `rootTarget` and `terminalTarget` of a version-3 row sent a `-d`
   (`Expected values to be strictly deep-equal`). The EMPTY setting's default `*` row keeps today's road for an unknown version (only a version 1 is `wsl1`) — the default is "byte for byte today's".
2. **`persistedNameOf` applies `DISTRO_NAME`** (every name `listDistros` can mint already does): a journal name with a space, a quote, a newline or `~` is not minted and the entry is dropped. RED: `"My Distro"` was minted.
3. **`TerminalTarget.running` is `boolean | undefined`** — `undefined` when the running question failed, so W1b's modal never says "stopped" on an unanswered question. RED: `running` read `false`.
4. **`freezePreview` freezes the `target`** too. RED: `ok(Object.isFrozen(outcome.preview.target))`.
5. **The header of `-l -v` is the line immediately before the first row** (not the first non-row line): a stray line above it no longer becomes the state column. RED: the fixture with a stray notice lost its two-word state.

Break-its (SHA-256 restored): `version !== 1` passing as 2 → the version-3 test red; `persistedNameOf` by length only → the name tests red.

## W1b — the full view

> Specified 2026-10-22 on `main` @ `2bbd8d30` (W1a merged). **Plan only; the plan round is next.** W1b is the first thing the
> owner SEES of #87's split: an editor-area page, built from the kit's components (W0) over the per-distro data path (W1a), that
> shows everything the side bar shows and more, per distro, with every action a button. It implements
> [PLAN_disk_health.md](PLAN_disk_health.md) §3.5 (the full view READS the store, never polls; `preview` / `doctor` only on open,
> *Refresh* and becoming visible; a hidden view gets no posts; the spawn ledger as "AI OS Care's own cost"; `program` + safe prefix,
> the command line revealed per row; "not available yet" muted; binary units for held memory), §3.6 items 1–3 (no table scrolls
> sideways; cards in one row of one height, buttons at the bottom; the distro picker on top of the WSL tab and the VM / distro split)
> and the E18 table row W1b. The headless layout check is the kit's (K2, kit 0.3.0, specified in `dew_flow_vscode_kit`); W1b's side
> of it is § 3.10.

### 1. Goal

What must be true when W1b is done:

1. **One page, opened by a button.** `wslCare.openFullView` opens (or reveals) ONE `WebviewPanel` `wslCare.fullView` in the editor
   area — from an icon in the side bar's title bar AND a button on the side bar's page; a page open at a reload is restored by a
   serializer on its persisted tab and distro.
2. **It reads, it does not poll.** Its data is the store's (`OutcomeStore`), the hosts' derived views (cleanup, archive, relays,
   GPU, Windows Time, Windows tasks, the bundled daemon) and the spawn ledger. It starts reads only on open, *Refresh*, a pick and
   becoming visible — never on a timer — and posts nothing while hidden.
3. **Two tabs, per side.** *WSL: <distro>* and *Windows*, each with its status mark. On top of the WSL tab, a distro picker (a
   radio group: name, state, its mark; arrows / Home / End); every figure on the tab follows the picked distro, and the WSL VM's
   figures are labelled "the WSL VM — shared by every distro", apart from the distro's own.
4. **Every picker state has its page**: a running distro with a daemon; no daemon (*Install daemon in <distro>…*, the VM's
   figures "measured by <source>'s daemon"); stopped (last known figures, muted, with their time, and *Start <distro> and check*);
   running state not known; WSL 1 and an unknown WSL version (the W1a sentences, no VM figures); Docker Desktop's own distros
   (read-only); a failed daemon read (its sentence); not read yet.
5. **No command line before a click.** Tables show `program` + the daemon's `argumentsShown`; the redacted `commandLine` reaches
   the page only as the host's answer to one row's reveal, and never sits in a button label.
6. **Units and absence.** Held memory in binary adaptive units (14 MiB reads "14.0 MiB"); PIDs never wrap; what `main` cannot
   read yet is a muted "not available yet — needs <what> (<story>)", never a figure and never 0.
7. **Every action is a button here too.** Every contributed command has its full-view button (`commandButtons.ts` gains the
   `full` place), proved by rendering the page and pressing every button, as the side bar's test does.
8. **The layout holds in a real Chrome**: the kit's layout-check bin passes over a static rendering of every tab and picker state
   at 300 / 1280 / 1920 px × four themes × three sizes — no page, box, table or cell wider than its room; cards in a row of one
   height.

### 2. Where things are today (checked on `main` @ `2bbd8d30`, 2026-10-22)

- **The page shape to reuse** — `src_vs_code/src/logsPage/logsPanel.ts`: `viewType` (`:24`), a `PanelSlot` (`:27`, one panel,
  created or revealed synchronously), `post` through the slot (`:30`), `show` (`:34-46`, `createWebviewPanel(…, panelOptions(media))`),
  `restore` (`:49-53`), `statusChanged` (`:56-60`), `wire` (`:70-81`: `panelShell({ nonce, scriptUri, styleUri, page })`,
  `onDidReceiveMessage` → `controller.receive(raw)` with the log as the detached edge). The serializers: `extension.ts:513` (Archive),
  `:523` (Logs); activation `package.json:52-53` (`onWebviewPanel:…`). `archivePage/archivePanel.ts:80` drops the page's state on
  `onDidDispose` (`pageClosed`) and names rows by index with the view's `generation` (`archivePage/archiveMessages.ts:25-28`, `:49`).
- **Visibility exists only in the side bar**: `panel/panelProvider.ts:98` (`onDidChangeVisibility`), `:108-110` (`isVisible`),
  `:116-120` (refresh on becoming visible). No page panel handles `onDidChangeViewState`; `panelOptions` (`panel/panelHtml.ts:74-76`)
  sets no `retainContextWhenHidden`, so a hidden page's webview is torn down and sends `ready` again when shown
  (`LogsController`'s `ready`, `logsPage/logsController.ts:180`).
- **The shell**: `type Page = 'panel' | 'logs' | 'archive'` (`panelHtml.ts:23`), `PAGES` (`:26-30`, root id and title), ONE
  `scriptUri` and ONE `styleUri` (`:15-21`, `:62`, `:67`), the CSP `default-src 'none'; script-src 'nonce-…'; style-src 'nonce-…'`
  (`:60`), the nonce 32 hex (`:32`), `SAFE_URI` (`:33`); `test/panel/webviewHost.test.ts:123-129` pins the CSP equal across pages and
  each page's root and title.
- **The full view's bundle (W0)**: `src/webview/fullView.ts:1-25` (imports the kit, acquires the API once, empties `#view`);
  `scripts/bundle-entries.json` (the `webview` entry → `dist/webview/full.js`, IIFE, `chrome114`; esbuild emits `full.css`);
  `webviewBoundary.test.ts:26-28`, `:51-63` (a page file imports only its folder and the kit's two browser entries; no host module
  imports `src/webview/`); `kitPage.test.ts` + `test/webview/kitPageEntry.ts` (the reveal wiring `{ type: 'reveal', index, request }`);
  `fullView.test.ts` runs the shipped `full.js` (no throw, nothing posted, the API acquired once).
- **The side bar's data path**: `panel/viewModel.ts` builds the page from `FIELD_MAP` — `fromOutcome` (`:57`), `rowFor` (`:65`, not
  exported), `sections` (`:73`), `buildPanelView` (`:141`); `panel/fieldMap.ts` `SECTIONS` (`:30-42`), `FIELD_MAP` (`:118-164`);
  `panel/rowRenderers.ts:36-42` renders processes with a *Command line* column from `commandLine` and `gib()` for Held (`0.0 GiB` for
  14 MiB — #87's screenshot). `text/format.ts` has `gb` (`:9`), `sizeText` (decimal adaptive, `:20`), `gib` (`:28`), `percent` (`:32`);
  no binary adaptive helper; the words "not available yet" exist nowhere.
- **The side bar's closed set**: `panel/messages.ts` `PageMessage` (`:38-63`), `BARE` (`:65`), `rowIdsOf` / `isIndex` (private),
  `READERS` (`:116-124`), `PAGE_MESSAGE_TYPES` (`:137`); `panel/view.ts:51` `PAGE_ACTIONS`. `panel/panelProvider.ts` `PanelActions`
  (`:23-75`), `receive` (`:122-153`), `post` (`:155-157`, the DEFAULT snapshot only).
- **The buttons rule**: `panel/commandButtons.ts:18-21` (`where`: `page` with an action | `title` | `opener`), `:23-44` (19 commands);
  `test/commandButtons.test.ts:42-47` (every command has an entry), `:52-88` (renders `media/panel.js` over real views and presses
  every button), `:100-111` (a page button is rendered, accepted and named after its command), `:113-124` (only `openPanel` is an
  opener; a `title` command is in `view/title`). `package.json:174-184` (`view/title`: refresh, openLogs).
- **The W1a roads W1b stands on**: `OutcomeStore.snapshot()` (the default's), `snapshotOf` (`state/outcomeStore.ts:70`), `setPicked`
  (`:79`), `pickedNames` (`:89`), `dropUnlisted` (`:98`), `clear()` (`:61-64`, keeps the picked map); a `stopped` / `wsl1` read
  REPLACES the picked snapshot's answered outcomes verb by verb (`poll/poller.ts` `afterPickedStatus`), so a stopped distro's last
  figures are gone from the store after a read. `Poller.refreshPanel` (`:176`, the default + the Windows read, `panelRound` `:205-219`),
  `readPicked` (`:185`), `listDistros` (`:190`); the Windows read `askWindows` (`:293`) is private. `DistroInfo.running` is
  `boolean | undefined`, `version` `1 | 2 | undefined` (`wsl/distroList.ts`); `CleanupHost.controls(aim)` (`cleanup/cleanupHost.ts:151`)
  but `relays()` (`:175`) reads the default only; `installPrompt(distro)` (`install/installDaemon.ts:57-72`) has no running fact.
- **The Windows side**: the store's `windowsStatus` slot (`outcomeStore.ts:25`, `:49`), read by the poller (`:293-360`) through
  `WindowsCareClient.status` (`client/WindowsCareClient.ts:171-180`, `client/windowsStatus.ts:57`) — and NOTHING renders its body. The
  Windows binary's report: `HostReport` (`src_daemon/src/WslCare.Core/Status/StatusReport.cs:197-209`: memory, system drive, vmmem
  working set, `vmmemAdvice`), `WindowsMcpServers` (`:88`), `Worktrees` (`:100`, E19.S3/S4 merged); verdicts `host.commit`,
  `host.compression`, `host.vmmem` (`Thresholds/HostRules.cs:60`, `:71`, `:83-85`). There is NO Windows status golden: the goldens are
  the Linux binary's over the captured fixtures (`tests/WslCare.Scenarios/GoldenContracts.cs`), and E19's plan records the same gap
  ([PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md), E19.S2b's **OPEN** item). A Windows status is already built
  from a fixed `ProbeSample` with `HostSide.Windows` in `tests/WslCare.Cli.Tests/McpStatusTests.cs`.
- **The process door**: `process/runner.ts` (`Runner` `:58`, `spawnRunner` `:262`, `closedRunner` `:291`), the only importer of
  `child_process` (`structure.test.ts:58-60`). `extension.ts:151` builds ONE client runner (`clientRunner(…, runnerFor(choice), calls)`,
  also the Windows client's and the archive's); the PowerShell hosts take `runnerFor({ kind: 'real' })` (`:179`, `:280`, `:355`,
  `:402`). Nothing counts spawns.
- **The harness after W0** (`test/support/pageHarness.ts`): tags `:37`, attributes `:44` (`role`, `scope`, `title`, `type`, `colspan`,
  `min`, `max`, `id`, `tabindex`; `aria-*` / `data-*` by prefix), events `:47` (`click`, `keydown`, `input`), `focus()` on an attached
  button, input or `tabindex` element (`:392-396`). A radio group of buttons with `role="radio"` + `aria-checked` + a roving
  `tabindex` + arrow keys needs NOTHING more.
- **Structural lists that name page hosts**: `structure.test.ts:226` (`FAR_FROM_ROOT`: `src/panel/`, …, `src/logsPage/`,
  `src/archivePage/`), used by the root (`:250-255`) and config (`:309-312`) import rules.

### 3. Design

#### 3.1 The panel

- **`src/fullView/fullViewPanel.ts`** (new, the `LogsPanel` shape, `logsPanel.ts:23-82`): `viewType = 'wslCare.fullView'`, title
  `AI OS Care — Full view`, one `PanelSlot`, `show()`, `restore(panel)`, `isOpen()`, `dispose()`, and `wire(panel)` with the shell
  page `full` (`PAGES` gains `full: { root: 'view', title: 'AI OS Care — Full view' }` — `view` is W0's mount point, and the word
  `root` stays out of shipped sources but the root-call module, W0 § 9). `onDidDispose` → `controller.pageClosed()` (the Archive
  page's rule): the reveal texts, the pick timer and the generations are dropped.
- **`localResourceRoots`: `dist/webview/` only, for this page only.** `panelOptions(root)` already takes its root
  (`panelHtml.ts:74-76`); the full view passes `Uri.joinPath(extensionUri, 'dist', 'webview')`, the other pages keep `media/`. No
  `retainContextWhenHidden`: the page holds no state the host does not (the tab and the distro are the host's, § 3.2), a revealed
  command line SHOULD vanish when the page is hidden, and a torn-down webview costs no memory. Sort and search state is the page's
  and is lost on hide — stated (§ 7).
- **ONE stylesheet, decided.** The page's own CSS (`src/webview/fullView.css`: the card grid, the card, the picker, muted and
  section rules) is imported by `fullView.ts` beside the kit's, so esbuild emits ONE `dist/webview/full.css`. Reasons: `ShellParts`
  keeps its single `styleUri` (`panelHtml.ts:15-21`) and the CSP stays byte-equal across pages (`webviewHost.test.ts:123-129`
  unchanged); W0's `full.css` checks (no `url(`, `@import`, `@font-face`) cover the page's rules for free; `.vscodeignore` and
  `vsix-files.txt` are unchanged. A second `<link>` would widen the shell for one page and buy nothing. `src/webview/stylesheets.d.ts`
  gains the `*.css` side-effect module declaration TypeScript 6 needs (W0 § 9).
- **The command and its buttons.** `package.json` contributes `wslCare.openFullView` ("Open full view", category `AI OS Care`,
  icon `$(open-preview)`), a `view/title` entry `when: view == wslCare.panel` (the icon), and `onWebviewPanel:wslCare.fullView`; the
  side bar's page gains the bare action `openFullView` (`PAGE_ACTIONS`, `messages.ts` `BARE`, `PanelActions.openFullView`), rendered
  by `media/panel.js` among the head actions. `extension.ts` registers the command, the serializer
  (`registerWebviewPanelSerializer(FullViewPanel.viewType, …)`, as `:523`) and the change subscriptions (§ 3.2). W2's "*Open full
  view*" item of PLAN_disk_health.md's E18 table is therefore delivered here; W2 keeps only its placement under the recommendation.

#### 3.2 Reads, visibility and the host-held state (`src/fullView/fullViewController.ts`)

- **The host holds the page's state**, never the page: the shown tab (`wsl` | `windows`), the shown distro (a `DistroName`, or the
  default entry), the last `DistroList`, and three counters — `list` (bumped when the list's names change), `pick` (bumped on every
  accepted pick and every list change) and one `generation` per reveal table (§ 3.6). The tab and the shown distro's NAME are written to
  `workspaceState['wslCare.fullView']` (`{ tab, distro? }`, < 200 bytes, overwritten); a restore re-mints the name with
  `persistedNameOf` (`wsl/distroTarget.ts`) and keeps it only if the next `listDistros()` lists it — otherwise the default entry.
- **The default entry is read through the DEFAULT road, every other entry through `readPicked`** — decided, because the side bar and
  the full view must never disagree about the default distro, and a picked read of the default would double every open's spawns
  (W1a: a default call and a picked call never share, `§3.1`). `defaultEntryOf(list, settingText, snapshot)` (pure,
  `src/fullView/distroEntries.ts`): the distro the default snapshot's `status` names (answered, or a failure that names one), else a
  SET `wslCare.distro` that the list holds, else `list.default`, else none. That entry renders `store.snapshot()`; any other renders
  `store.snapshotOf(name)`.
- **When it reads** (never on a timer; each read through the poller, so the store stays the one data service):

  | Event | Reads | Posts |
  |---|---|---|
  | `ready` (a new or re-created page) | none | the current view at once |
  | open, *Refresh* on the WSL tab, becoming visible | `poller.listDistros()`, then the shown entry: default → `poller.refreshPanel()`; picked → `poller.readPicked(name)`; a WSL 1 / version-unknown / Docker Desktop / stopped entry → none | after each store change (visible only) |
  | *Refresh* on the Windows tab | `poller.refreshWindows()` — NEW, public: today's private `askWindows(true)`, tracked (a widening of W1a's poller, `poller.ts:293`) | as above |
  | `pick` | the picked entry's read, started `PICK_SETTLE_MS` (400) after the LAST pick — arrow keys over five distros start one read, not five; the view from the store is posted at once | as above |
  | `tab` | none (the tab shows the store's newest; *Refresh* reads) | the view |

  `PICK_SETTLE_MS` is a named constant with that reason, not a setting (a UI settle, not a threshold); the timer is injected
  (`ONE_SHOT`'s shape) and replaced, never stacked; `pageClosed` clears it.
- **Hidden means silent.** The panel port exposes `visible`; `post` returns without posting while the panel is not visible. The
  store's and the hosts' `onChange` (`store`, `host`, `archive`, `gpu`, `guard`, `bundled`, `windowsTasks`, the ledger) call
  `controller.changed()`, which posts only when visible. `onDidChangeViewState` with `visible` turning true runs the "becoming visible"
  row; the page's own `ready` (it was torn down) gets the current view.

#### 3.3 The wire — one shared module and two closed message sets

- **`src/shared/fullViewWire.ts`** (new) holds the posted view's TYPES, the closed tables (`TAB_IDS`, `REVEAL_TABLES`,
  `FULL_MESSAGE_TYPES`, the button-message shapes) and NOTHING that imports: the host's parser validates against it and the page
  renders from it, so the contract is written once (`common.testing`: a contract with two implementations is enumerated, not
  retyped). The W0 boundary widens by exactly this file, RED-first: a page file may import `src/shared/fullViewWire.ts` and no other
  host module; the wire imports nothing (a planted import is found); it compiles under both configs, so a DOM or Node reference in
  it fails one of the two typechecks. Reason for `shared/` rather than `webview/`: the host owns the contract (it is the side that
  validates), and `shared/` already holds pure modules both sides of a boundary use (`shared/viewLevel.ts`).
- **Page → host** (`src/fullView/fullMessages.ts`, `parseFullMessage(raw): FullMessage | undefined`, the `messages.ts` style:
  each type checked with EXACTLY its keys; `rowIdsOf`, `isIndex`, `MAX_STOP_INDEX` and the GPU bound are exported from
  `panel/messages.ts` and reused, not copied):

  | `type` | keys besides `type` | values |
  |---|---|---|
  | `ready`, `refresh` | — | |
  | `rendered` | `cards`, `styled` | a whole number; a boolean (§ 3.11) |
  | `tab` | `id` | `wsl` \| `windows` |
  | `pick` | `index`, `list` | an index into the host's list of THAT `list` generation (< its length, ≤ 63); the generation |
  | `reveal` | `table`, `generation`, `index`, `request` | a `REVEAL_TABLES` id; whole numbers (index ≤ 999); the kit's request, `^\d{1,9}:\d{1,9}$` |
  | `startWsl`, `installDaemon`, `runFullCheck`, `archiveNow`, `chooseArchiveFolder`, `stopArchiving`, `openArchive`, `stopRelays`, `openRunLogs` | `pick` | the pick generation the button was rendered in |
  | `clean`, `cleanSelected` | `rowIds`, `pick` | the side bar's row-id rule (one id for `clean`) |
  | `stop` | `index`, `pick` | ≤ `MAX_STOP_INDEX` |
  | `openSettings`, `openLogs`, `startWindowsTime`, `installWindowsTimeGuard`, `removeWindowsTimeGuard`, `installWindowsWatchTask`, `installWindowsArchiveTask`, `removeWindowsTasks`, `runWindowsWatchTask`, `runWindowsArchiveTask` | — | |
  | `enableGpu` | `index`, `readAt` | the side bar's rule |

  A distro action whose `pick` is not the CURRENT pick generation is dropped and the view re-posted with the notice "The distro
  changed — press the button again." (a click on a button rendered for Ubuntu that lands after a pick of Debian never acts on Debian);
  the modals still name their distro (W1a), a second guard. A `pick` of a stale `list`, or an index past the list, is ignored.
- **Host → page**: `{ type: 'view', view: FullView }` and `{ type: 'revealed', table, generation, index, request, text }`. Every
  button in a view carries the COMPLETE message it posts, built by the host from the closed shapes; the page posts it verbatim and
  composes only `ready`, `rendered`, `tab`, `pick` and `reveal` itself.

#### 3.4 The picker and the states of an entry (`src/fullView/distroEntries.ts`, pure)

`entryOf(info, source)` turns one `DistroInfo` and its snapshot into a `PickerEntry { name, meta, mark: { tone, word }, kind }`
and decides which cards the WSL tab shows. The kinds, checked in this order:

| Kind | When | Picker meta / mark | The WSL tab | Reads |
|---|---|---|---|---|
| `dockerDesktop` | name is `docker-desktop` or `docker-desktop-data` (the two names Docker Desktop creates; exact) | "Docker Desktop's own" / neutral "read only" | one card: "Docker Desktop creates and manages this distro. AI OS Care installs nothing here and offers no action on it." + its version and state | none |
| `wsl1` | `version === 1` | "WSL 1" / neutral "WSL 1" | the W1a sentence "WSL 1 — AI OS Care needs WSL 2 …"; no VM card, no button | none |
| `versionUnknown` | `version === undefined` | "version not known" / warning "version not known" | the W1a `versionUnknown` sentence; no VM card, no button | none |
| `stopped` | `running === false` | "stopped" / neutral "stopped" | "<d> is stopped — AI OS Care does not start a distro on its own." + *Start <d> and check* (`startWsl`); the LAST KNOWN distro cards, muted, "as of <local minute>" (or "no figures were read in this window") | none |
| `stateUnknown` | `running === undefined` | "state not known" / neutral "state not known" — never "stopped" | as `stopped`, worded "WSL did not say whether <d> runs" | none |
| `checking` | running, no `status` outcome yet | "checking…" / neutral | "Asking <d>'s daemon…" | the entry's read |
| `noDaemon` | `status` is `notInstalled` | "no daemon" / info "no daemon" | *Install daemon in <d>…* (`installDaemon`); the VM section "measured by <source>'s daemon" (§ 3.5); each distro card "not available — needs the daemon in <d>" | the entry's read |
| `failed` | any other failure of `status` | `failureText(…).label` / critical | the failure's sentence; *Install daemon…* when it is `daemonTooOld`; last known, muted | the entry's read |
| `daemon` | `status` answered | "default · running · daemon <version>" / the worst verdict: critical "N critical", warning "N warnings", ok "OK" | every card of § 3.5 | the entry's read |

- **Last known figures** need the store to keep them: a `stopped` read replaces the answered outcomes. `OutcomeStore` gains
  `lastKnownOf(target): { snapshot, at } | undefined` — updated on every write of an `answered` outcome (default or picked), never by a
  failure, cleared for the default by `clear()` (a setting change is another distro) and for a picked name by `dropUnlisted`; `at` is
  the answer's `sampledAt`. In memory only (§ 8), so after a reload a stopped distro says "no figures were read in this window".
- **The page's picker** (§ 3.11) is a `div role="radiogroup" aria-labelledby` of `button role="radio" aria-checked` chips (name,
  meta, mark word — the mark's tone is a glyph AND a word, never colour alone), roving `tabindex`, ArrowRight / ArrowDown /
  ArrowLeft / ArrowUp (wrapping), Home, End with `preventDefault` and `focus()` — the WAI-ARIA radio group, selection follows focus;
  each selection posts `pick`. The chips wrap (`flex-wrap`), never scroll. The tab label follows the pick: the kit's `Tabs` has no
  `setLabel` (kit §4.5), so the page rebuilds the tablist when the label changes (on a pick only), keeps the selected tab and puts
  focus back on the checked chip; other posts only `setStatus` and replace the panels' content.

#### 3.5 The view model (`src/fullView/fullViewModel.ts`, `wslCards.ts`, `windowsCards.ts`, pure)

- **Host-side, spelt.** `buildFullView(input): FullView` takes the snapshots, the list, the host views and the ledger and returns
  strings and numbers only — every daemon string through `safeText` (`panel/format.ts`) — the side bar's rule (`view.ts:9-13`).
- **One renderer for both views.** A figure the side bar also shows is rendered by the SAME code: `viewModel.ts` exports `rowFor`
  (`:65`, a widening, `common.reuse-first` step 1) and the cards place `FIELD_MAP` rows by id. New renderers exist only for what the
  side bar does not show (the reveal tables, PSI, the virtual disks, MCP instances, the Windows report). The side bar's own
  *Command line* column (`rowRenderers.ts:36-42`) is W2's to remove (§ 3.12).
- **`FULL_CARDS`**, one typed table: per card `{ id, tab, scope: 'vm' | 'distro' | 'windows', title, paths, build }`. `scope: 'vm'`
  cards carry the note `VM_LABEL = 'the WSL VM — shared by every distro'`; when their figures came from another distro's snapshot
  (a `noDaemon` entry) the note adds "measured by <source>'s daemon, <age>", the source being the default snapshot when answered,
  else the first listed picked snapshot with an answered `status`, else the card reads "not available — no distro's daemon has
  answered". `paths` is what makes the split checkable (RED 11): §3.6's left column as status paths and verdict-id prefixes —
  `vm.memory`, `slow.windowsClock`, `disk.vhdx`, `disk.volumes`, verdicts `memory.*`, `pressure.*`, `clock.*`, `kernel.*`, `host.*`,
  `disk.vhdxGap`, `disk.vhdxGrowth`, `disk.hostFree`, `disk.volumeFree` — the VM's; everything else the distro's.
- **The WSL tab** (`daemon` kind; the mockup's order):

  | Section | Card (scope) | Reads | Buttons |
  |---|---|---|---|
  | The WSL VM — shared by every distro | Memory (vm) | `vm.memory.*`, swap, fragmentation, unattributed; verdicts `memory.*` | — (A1 / A2 are ladder rungs, E16) |
  | | Pressure (PSI) (vm) | `vm.memory.pressure.{memory,io,cpu}.{some,full}.{avg10,avg60,avg300}` as a table; verdicts `pressure.cpu`, `pressure.io`, `memory.pressure` | — |
  | | Clock (vm) | `slow.windowsClock`; verdicts `clock.*` | — |
  | | Virtual disks (vm) | `status.disk.vhdx[]` with `basis` and age; verdicts `disk.vhdxGap`, `disk.vhdxGrowth` | — |
  | | Responsiveness and the restart forecast (vm) | not available yet — needs the responsiveness probe and the forecast (E16.S1, E16.S2) | — |
  | <distro>'s own | Recommendation (distro) | not available yet — needs the remedy ladder (E16.S3) | — |
  | | Disk (distro) | `vm.disk`; verdicts `disk.root`, `disk.discard`; this distro's own `vhdx` row (by name) | — |
  | | Docker (distro) | `preview.docker`, `preview.totals`, `vm.containers`, `slow.containerStats`, the cleanup rows, kept volumes, hygiene | each row's *Clean…* (`clean`), *Clean selected…* (`cleanSelected`) — `CleanupHost.controls(aim)` |
  | | Top processes (distro) | `vm.processes.top` (reveal table), `families`, `mntWalkers` (reveal table) | — |
  | | MCP servers (distro) | `mcpServers` counts, `instances[]` with the owner (reveal table on `owner.commandLine`), `servers[]` | — (A19 is a ladder rung) |
  | | Orphaned AI-agent processes (distro) | `processes.top[].orphaned`, `mcpServers.instances[].owner.orphaned` | — ("ending them: not available yet — needs the remedy ladder (E16.S3)") |
  | | Interop relays (distro) | `interopRelays` | *Stop client-gone relays…* (`stopRelays`) — `CleanupHost.relays(aim)`, widened like `controls(aim)` |
  | | Big folders, Container starts (distro) | `folders`, `containerStarts` | — |
  | | CPU by Claude session (distro) | not available yet — needs E15.S6 | — |
  | | Daemon (distro) | `productVersion`, doctor `healthy` / `checks` / `versions` / `configError`, `running` | *Run full check now* (`runFullCheck`), *Install daemon…* when outdated (`installDaemon`), *Stop* (`stop {index}`) |
  | | Last cleanup (distro) | `lastCleanup` | *Logs* (`openRunLogs`) — the default entry only (the Logs page is the default's, W1a §3.5) |
  | | AI-session archive (distro) | the default entry: `ArchiveHost`'s controls; a picked entry: `src/fullView/pickedArchive.ts` reads `archive status` through `client.read(request, pickedTarget(name))` on the entry's read and derives with `deriveArchive` (`archive/archiveView.ts`, reused) | *Archive now…*, *Choose folder…*, *Stop archiving…* with the entry's target; *Open archive* the default entry only |
  | | AI OS Care's own cost (distro-independent, shown on the WSL tab) | the spawn ledger (§ 3.9); "the daemon's own cost — not available yet — needs E15.S9" | *Settings* (`openSettings`) |

- **Units.** `text/format.ts` gains `binarySize(bytes)`: `B` below 1 KiB as a whole number, else one decimal in the largest of
  KiB / MiB / GiB / TiB it reaches (14 680 064 → "14.0 MiB"); a negative or non-finite count is a `TypeError` (a defect, not data);
  every held-memory figure of the full view uses it (Held, `memory.current`, MCP held, families). Disk and Docker stay decimal, as
  `df -H` and `docker system df` print them (`panel/format.ts:6-8`). An absent figure is `—` with a `null` sort value (kit §4.6).
- **"not available yet"** is ONE table, `src/fullView/notYet.ts`: `NOT_YET = 'not available yet'` and per card the missing piece
  and its story (E16.S1/S2, E16.S3, E15.S6, E15.S9 on the WSL tab; Windows processes E15.S3/E15.S6, Defender E15.S10/E16.S5, disks by
  number E15.S4 on the Windows tab). Rendered muted (`wc-card--muted`), with no figure and no button. Each story that lands flips its
  row — the table is the checklist.

#### 3.6 Tables and the reveal (`src/fullView/fullTables.ts`)

- Every table is the kit's `DataTable` (W0) with **short headers** and a declared **`stack`** — never the default:

  | Table | Columns | `stack` |
  |---|---|---|
  | `processes`, `mntWalkers` (reveal) | PID · Program · Cmd · Family · CPU · Held · Age | `wide` |
  | `mcpInstances` (reveal) | PID · Server · State · Owner · Held · Age · Cmd | `wide` |
  | `windowsMcp` (reveal, § 3.7) | PID · Server · State · Held · Cmd | `medium` |
  | families, containers, PSI, own cost | 3–4 columns | `narrow` |
  | virtual disks, volumes, folders, orphans, doctor checks | 4–5 columns | `medium` |

  *Program* is `program` followed by the daemon's `argumentsShown` joined by spaces; *Owner* is `owner.agent` + `owner.program`. A
  `pid` column is `align: 'end'`, which the kit keeps on one line while the table is wider than `30em` (kit §4.6a) — and every PID
  table stacks at `narrow` or wider, so below that width it is a label line, never a wrapped number. Each cell is `{ text, sort }`;
  the page's `value` is `sort` and its `format` is `text`.
- **The reveal.** A reveal table's rows carry NO command line; each has a `revealLabel` — "Show the command line of <program> (PID
  <pid>)" — the kit's `button(row)` text, never the hidden value. The host keeps, per reveal table, the `commandLine` texts of the rows
  it last posted under that table's `generation`; the generation is bumped only when that table's posted rows differ (a deep compare
  of the spelt rows), so a background re-post does not wipe a revealed line. `reveal { table, generation, index, request }` is
  answered `revealed { …, text }` with `safeText(commandLine)` when the generation is current and the row has one, else
  `not available` (the kit's contract for a host that cannot answer, kit §4.6). The page calls `table.reveal(index, text, request)`
  and ignores `false`. When a table leaves the page (a pick, a tab rebuild, `pageClosed`), the page calls `setRows([])` and then
  `dispose()` (kit §4.6: `dispose` alone keeps a revealed text). `onReveal` posts; if posting throws, the kit restores the row to
  clickable and rethrows, and the page's handler logs nothing and survives (RED 16).

#### 3.7 The Windows tab and the Windows status golden

- **The golden, from the emitter.** A daemon test beside `McpStatusTests` (`tests/WslCare.Cli.Tests/WindowsStatusGoldenTests.cs`)
  builds `status --json` of a FIXED Windows `ProbeSample` (host memory, the system drive, a vmmem working set over the advice
  threshold, two Windows MCP servers with owners and redacted command lines, a worktree section) through `StatusReports.From` and the
  CLI's JSON writer, and holds `contracts/golden/head/status-windows.json` equal to it (`WSL_CARE_WRITE_GOLDENS=1` writes it, as
  `GoldenContracts.cs` does) — platform-independent: no Win32 call. `FixturePrivacyTests` reads it like every golden. The extension's
  `test/support/outcomes.ts` gains `windowsGoldenSnapshot()` through `windowsStatusOutcomeOf` (the real validator: the fixture must
  answer `answered`, `side: 'windows'`, `common.testing`). The strict fake's Windows answer moves from the distro golden with
  `side: "windows"` to this file. This closes E19's **OPEN** item for E19.S5 too (§ 3.12).
- **The cards** (scope `windows`): *Bundled Windows daemon* (`bundled/bundledView.ts`); *GPU adapters* (`GpuView`, *Enable <GPU>…*
  by `index` + `readAt`); *Windows memory* (`host.memory`, `vmmemWorkingSet` in binary units, `vmmemAdvice` as text, verdicts
  `host.commit`, `host.compression`, `host.vmmem`); *Windows volumes* (`host.systemDrive`, and the default snapshot's `status.disk.volumes`
  with its basis — "read by <d>'s full run, <age>" — and verdicts `disk.volumeFree`, `disk.hostFree`); *Windows MCP servers*
  (`windowsMcpServers`, reveal table); *AI-agent worktrees* (`worktrees`, read-only, verdict `worktrees.reclaimable`; its buttons are
  E20's); *Windows Time* (`GuardView` + its three buttons); *Windows tasks* (`WindowsTasksView` + its five buttons); not-yet cards
  *Windows processes*, *Defender*, *Physical disks*.
- When the Windows reading is not an answer (`notWindows`, the exe not usable, a timeout), the four report cards say
  "not available — <failureText sentence>" — a failure, not the muted not-yet; the host-derived cards (GPU, Windows Time, tasks)
  keep their own reads.

#### 3.8 Every action is a button here too

- `CommandButton` gains a REQUIRED `full` field: `{ where: 'full'; action: FullAction }` or `{ where: 'notInFull'; why: string }`.
  The side bar's `where` keeps its meaning (W2 slims it). Required, so a command added without a decision does not compile. After
  W1b exactly two commands are `notInFull`: `wslCare.openPanel` ("the side bar's own opener") and `wslCare.openFullView` ("this is
  the full view"). `openFullView`'s side bar entry is `{ where: 'page', action: 'openFullView' }` and it is also in `view/title`.
- Distro actions run against the SHOWN entry's `DistroTarget` (W1a's roads: `CleanupHost.clean / runFullCheck / stop / archiveNow /
  stopRelays (target)`, `ArchiveHost.choose / stop / archiveNow (target)`, `installer(parts, target)`, `poller.readPicked(name,
  { startIfStopped: true })` or `refreshAll({ startIfStopped: true })` for *Start <d> and check*) — the target is the host's, the page
  sends only the pick generation. The install modal for a picked distro uses W1a's `TerminalTarget.running`: `installPrompt(distro,
  running)` adds "<d> is stopped — opening the terminal starts it." for `false` and "whether <d> runs is not known — opening the
  terminal may start it." for `undefined` (W1a § 9a #3); the default's prompt is unchanged.
- Windows-side actions call the same host functions the side bar's `PanelActions` do; nothing new is launched.

#### 3.9 The spawn ledger (`src/process/spawnLedger.ts`)

- `SpawnLedger` (pure): per UTC hour of a call's START, per key, `{ calls, wallMs }`; it keeps the current hour and the 23 before
  it (`LEDGER_HOURS = 24`) and drops older buckets on the next record. `ledgered(inner, ledger, keyOf, clock): Runner` — a decorator
  that records the call at start, adds its wall time (the monotonic `clock`) when the inner promise settles, and passes the request
  and the result (or the rejection) through unchanged. It imports nothing but the runner's types.
- **Keys never carry data.** `ledgerKeyOf(request)` is exported by `client/WslCareClient.ts` — the module allowed to spell argv
  words (`structure.test.ts:79-84`): for `wsl.exe`, `wsl.exe list` (the list questions) or `daemon <first word of the daemon's
  tail>` (+ ` (root)` for `-u root`); for the bundled exe, `wsl-care.exe <first argument>`; for anything else the program's file name
  only (PowerShell's script is never part of a key). No distro name, path, value, id or pid ever reaches a key (RED 28).
- **Every runner is wrapped**: `extension.ts` wraps the client runner (`:151`) and every `runnerFor({ kind: 'real' })` it builds (one
  helper), and an AST scan holds it (RED 29). The ledger is per window and in memory: what THIS window's extension started. The
  card shows this hour's rows (Verb · Calls · Wall) and the previous hour's total; it judges nothing — the budget verdict
  `selfCost.extensionCalls` is the daemon's (E15.S9, PLAN_disk_health.md §2 item 7), so the extension holds no threshold (#87 item 3).

#### 3.10 One height per row, and the layout check (kit 0.3.0)

- **The page's CSS** (`src/webview/fullView.css`, classes only): `.wc-grid` is a grid of `repeat(auto-fill, minmax(min(100%, 24em),
  1fr))` with `align-items: stretch` (`.wc-grid--wide`: `36em`), `.wc-card` a flex column with `min-width: 0`, `.wc-card__actions`
  `margin-top: auto` — so cards side by side have one height and their buttons line up at the bottom (§3.6 item 2); the picker's chips
  `flex-wrap`. Tokens are VS Code's `--vscode-*` and the kit's; no `url(`, no `@import`.
- **The bin.** Kit 0.3.0 (K2) ships `vscode-webview-kit-layout-check`: a static page from a file, headless Chrome over the DevTools
  protocol, widths 300 / 1280 / 1920, four themes, three sizes; it fails on page, box, table and cell overflow and on unequal card
  heights in a row by a consumer-given selector. W1b pins EXACTLY `0.3.0` (the lock entry with `version` + `integrity` and no
  `resolved`, W0 § 3 / § 9) and never copies the probe.
- **W1b's side** — `scripts/layout-full.mjs`, `npm run layout:full`, after `npm run bundle` and the test compile:
  1. builds the fixture messages with the REAL view model (`out/test/layout/fullFixtures.js`, compiled from
     `src/test/layout/fullFixtures.ts`): the WSL tab for every kind of § 3.4, the Windows tab answered (the Windows golden) and
     unavailable, and a "wide data" fixture — 50 processes, a 120-character unbreakable token in `argumentsShown`, a long server name,
     and a `revealed` message carrying a 200-character command line;
  2. writes one static page per fixture into `os.tmpdir()/testsToDelete-full-layout-<pid>/`: `full.css` and `full.js` copied beside
     it, `<main id="view">`, a stub `acquireVsCodeApi` (records, posts nowhere), the fixture's messages dispatched as `message`
     events;
  3. runs the kit's bin per page with the card selector `.wc-grid > .wc-card` (the flag spelling is K2's); any failure is the
     script's exit code, naming the page;
  4. removes its folder in a `finally`, and at start removes any `testsToDelete-full-layout-<pid>` folder whose pid is not alive (a
     killed run's leftover). Chrome not found is a failure that says so, never a skip (kit §4.6a's rule).
  CI runs it on the extension workflow's ubuntu leg (Google Chrome is on the image). Nothing of it ships (`vsix-files.txt` unchanged).

#### 3.11 The page script (`src/webview/`)

- `fullView.ts` (the entry) acquires the API once, listens for `message`, posts `ready` once, and renders `view` / `revealed`
  messages; `cards.ts` (a card: head, note, lines, tables, buttons; muted and failed states), `picker.ts` (§ 3.4), `tables.ts` (the
  kit `DataTable` per `TableSpec`, its reveal wiring, `setRows([])` + `dispose()` on removal), `classes.ts` (every class name the page
  sets — the CSS test iterates it). DOM by `createElement` / `textContent` / `setAttribute` only — the strict harness refuses
  anything else.
- `rendered { cards, styled }` after each render: `styled` is whether the stylesheet applied — `typeof getComputedStyle ===
  'function' && getComputedStyle(grid).display === 'grid'`. The harness has no `getComputedStyle`, so it is `false` there; in a real
  VS Code it is `true` only if `full.css` loaded under the nonce CSP — W0 § 7's open item, closed by the host scenario (RED 23).

#### 3.12 Boundaries (written on both sides when each other story starts)

| Item | W1b builds | The other story |
|---|---|---|
| The side bar | *Open full view* (title icon + page button) | W2: the slim list (`justifies`, the `sidebar` / `full` column), the removal of the side bar's *Command line* column and "arrives in E#" rows, the recommendation's placement |
| One renderer | `rowFor` exported and reused by the full view | W5: `pagesShareOneRenderer.test.ts` |
| The Windows `status` golden | the emitter-side golden and the fake's answer (§ 3.7) | E19.S5: reuses it for the bar and side bar halves (`disk.volumeFree` from the newer reading, the worktree row); the boundary row is written into [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md) in W1b's first commit |
| Worktrees | a read-only card on the Windows tab | E19.S5: the side bar's suffix; E20 (DH-5): *Scan* / *Clean build output…* buttons, which then join the card |
| The layout probe | the static pages, the selector, the CI step | K2 (`dew_flow_vscode_kit`): the bin, its checks, its release 0.3.0 |
| Accessibility | the picker's radio group and the reveal labels | W5: keyboard sort / search across both pages, the contrast job |
| Disjoint | no daemon change except the golden's test; no new verdict, threshold or verb | — |

### 4. RED list (each seen failing for the real reason first; the commit it belongs to in brackets, § 10)

Panel, picker, WSL tab [W1b-1]:

1. `webviewHost.test.ts`: `panelShell({…, page: 'full'})` has `<main id="view">`, the title `AI OS Care — Full view` and the panel's
   CSP byte for byte; the full view's options are `localResourceRoots` = exactly `[…/dist/webview]`, `enableCommandUris: false`, no
   `retainContextWhenHidden`.
2. `manifest.test.ts` / `extensionWiring.test.ts`: `wslCare.openFullView` is contributed, in `view/title` for `wslCare.panel`, its
   activation `onWebviewPanel:wslCare.fullView`, its serializer registered.
3. `commandButtons.test.ts`: every entry has `full`; the `notInFull` commands are exactly `openPanel` and `openFullView`, each with a
   non-empty `why` (during W1b-1 the Windows-side commands are `notInFull` "the Windows tab (W1b-2)"; W1b-2 shrinks the set — the
   final assertion is the two); `openFullView`'s side bar button is pressed in `postedByPageButtons` and the host accepts it.
4. **The full-page button pass**: views built by the REAL view model over fixtures that between them show every button (each picker
   kind, a wedged run for *Stop*, a disabled GPU, the guard present and absent, the tasks state of `commandButtons.test.ts`), rendered
   by the SHIPPED `dist/webview/full.js` in the strict harness, every button clicked: the posted types include every `full.action`;
   each posted message parses (`parseFullMessage`); `command === wslCare.<action>`.
5. `fullMessages.test.ts`: over `FULL_MESSAGE_TYPES` (derived, not retyped) every type parses with exactly its keys; one key more,
   one fewer, a value of the wrong type, an index past its bound, an unknown table or tab, a request not of the kit's shape — each
   `undefined`.
6. `fullViewController.test.ts`, the panel port faked (`visible`, a post spy, view-state events): while hidden, store and host
   changes post NOTHING; turning visible runs `listDistros` then the shown entry's read (default → `refreshPanel`, picked →
   `readPicked`) and posts after the store changes; `ready` posts the current view at once, before any read resolves.
7. Pick: a current `pick` posts the store's view at once and starts `readPicked` after `PICK_SETTLE_MS` (fake timers); three picks
   inside it start ONE read, the last; a stale `list` or an index past the list starts nothing; picking the default entry calls
   `refreshPanel`, never `readPicked`; a pick clears no snapshot (the store's spy).
8. `distroEntries.test.ts`: `defaultEntryOf` — the answered `status`'s distro wins over a set setting and over `list.default`; a set
   setting the list does not hold is passed over; nothing → no default entry.
9. `entryOf` for each kind of § 3.4 (fixtures through `distroListOf`, W1a's real parser): its meta, mark tone AND word, cards and
   buttons; `wsl1`, `versionUnknown` and `dockerDesktop` start no read (the poller's spy) and carry no VM card and no button;
   `stateUnknown` never says "stopped"; `stopped` shows *Start <d> and check* and the last known cards muted with "as of" — after a
   `stopped` read replaced the answered snapshot (`lastKnownOf`).
10. **Every figure follows the pick**: two distros answer from edited golden copies with planted distinct values (Ubuntu
    `vm.disk.usedPercent` 11.1, top pid 11111, `interopRelays.count` 1; Debian 22.2, 22222, 2): Debian's view holds Debian's on
    every distro-scoped card and none of Ubuntu's; its relays button is derived from Debian's snapshot (`relays(aim)`); a `noDaemon`
    Debian's VM cards hold Ubuntu's figures and say "measured by Ubuntu's daemon".
10b. Debian's archive card is derived from Debian's own `archive status` read (`-d Debian … archive status --json` in the recording
    runner) and its buttons carry the pick generation; *Open archive* and *Logs* are on the default entry's cards only.
11. **The split is derived**: for every card in `FULL_CARDS`, a `vm` card reads only §3.6's VM paths and carries `VM_LABEL`; a
    `distro` card reads none of them — iterating the table, not a retyped list (a card added in the wrong scope fails).
12. Every `FIELD_MAP` read row is placed on some full-view card, and its text there equals `rowFor`'s (one renderer) — except the
    `processes` kind, whose full-view home is the reveal table.
13. `textFormat.test.ts`: `binarySize` — 0 → "0 B", 1023 → "1023 B", 1024 → "1.0 KiB", 14 680 064 → "14.0 MiB", 1.5 GiB → "1.5 GiB",
    2 TiB → "2.0 TiB"; −1, `NaN`, `Infinity` → `TypeError`. A process holding 14 680 064 bytes shows Held "14.0 MiB", never "0.0 GiB".
14. Over every `TableSpec` the view model builds from the fixtures: a `pid` column is `align: 'end'`; every table names its `stack`
    and a PID table's is `narrow` or wider.
15. **The reveal, host side**: the posted view's JSON contains NONE of the fixtures' command lines (derived: every `commandLine`
    and `owner.commandLine` in the bodies, with a planted sentinel unlike `program` / `argumentsShown`); `reveal` with the current
    generation answers that row's `safeText(commandLine)`; a stale generation, an index past the rows, a row without one → `not
    available`; no `revealLabel` contains its row's command line; a re-post with equal rows keeps the generation.
16. **The reveal, page side** (harness): *Show* posts exactly `{ type: 'reveal', table, generation, index, request }`; before the
    reply the row holds no command line; the reply shows it; a view with a new generation drops it; a pick that removes the table runs
    `setRows([])` then `dispose()`, after which a late `revealed` changes nothing; with an API whose `postMessage` throws, the row is
    clickable again and the page keeps running.
17. Each `NOT_YET` row renders muted "not available yet — needs … (E1x.Sy)" with no figure, no 0 and no button.
18. The page in the harness: the picker is a `radiogroup` of `role="radio"` buttons with one `aria-checked="true"` and a roving
    `tabindex`; each of ArrowRight / ArrowDown / ArrowLeft / ArrowUp / Home / End posts `pick` with the expected index (wrapping) and
    records `preventDefault`; and `harness.test.ts` pins `CREATABLE`, `ATTRIBUTES` and `ELEMENT_EVENTS` equal to W0's — W1b widens
    nothing (a widening the build turns out to need is its own RED test first, with its reason).
19. A serializer restore reopens on the persisted tab and distro; a persisted name the next list does not hold falls back to the
    default entry; a name failing `persistedNameOf` is never minted.
20. `fullViewCss.test.ts` over the bundled `full.css`: the four rules of § 3.10; every class in `classes.ts` has a rule; no `url(`,
    `@import`, `@font-face`.
21. `installDaemon.test.ts`: the prompt for `running: false` and `undefined` carries its sentence; `true` and the default's prompt are
    unchanged.
22. `structure.test.ts`: `FAR_FROM_ROOT` gains `src/fullView/` and the scan sees a module there; `webviewBoundary.test.ts`: a page file
    may import `src/shared/fullViewWire.ts` and a planted page import of another `src/shared/` module is refused; the wire imports
    nothing (planted companion).
23. **Host scenario** (`test/host/suite.ts`, VS Code 1.85 and stable, the strict fake): `api.openFullView()` → `rendered` with
    `cards > 0` and `styled: true`; `api.fullPick('Debian')` → the W1a call sequence for Debian, the status bar still Ubuntu's; with the
    panel hidden (another editor in its group) a store change posts nothing (`api.fullPosts()` unchanged). `fullView.test.ts` (W0)
    now expects exactly one `ready` with a mount and none without.

Windows tab and the ledger [W1b-2]:

24. `WindowsStatusGoldenTests`: the built report equals `contracts/golden/head/status-windows.json` (red before the file exists);
    `windowsStatusOutcomeOf` answers it `answered` with `side: 'windows'`; `FixturePrivacyTests` green over it.
25. `windowsCards.test.ts`: from the Windows golden — memory (binary units), the three host verdicts, the system drive, Windows MCP
    (a reveal table, its command lines absent from the view), worktrees; the volumes from the DEFAULT snapshot's `status.disk` with
    their basis; with a `notWindows` / timed-out reading each report card says "not available — <sentence>" and is not muted.
26. RED 3 and 4 complete: the Windows-side commands are `full`, pressed in the button pass; `notInFull` is exactly the two.
27. *Refresh* on the Windows tab calls `refreshWindows()` and no distro read; on the WSL tab it starts no Windows read.
28. `spawnLedger.test.ts`: `ledgered` hands the same request and result objects through; a call counts at start and adds its wall
    time at settle (fake clock); a rejection still counts and is rethrown; buckets follow the call's start hour; a 25th hour drops the
    oldest; over every request W1a's enumeration (RED 20) recorded, `ledgerKeyOf` never yields a key with a distro name, `/`, `\`,
    `=` or a run of five digits.
29. An AST scan of `extension.ts`: every `clientRunner(` / `runnerFor(` result is passed to `ledgered(`; a planted unwrapped
    `runnerFor({ kind: 'real' })` is found. The own-cost card shows this hour's rows and the previous hour's total, and the
    daemon half as not-yet.

The layout [W1b-3]:

30. `manifest.test.ts` (W0's RED 1 retargeted): the kit is exactly `0.3.0`; the lock entry has `version` + `integrity` and no
    `resolved`; `node_modules/.bin/vscode-webview-kit-layout-check` exists after `npm ci --ignore-scripts`.
31. `npm run layout:full` passes over every fixture page at 300 / 1280 / 1920 × four themes × three sizes. **Seen red first** on the
    built page with the card rules of § 3.10 taken out (unequal heights reported for the WSL tab's VM row) and with a table spec at
    `stack: 'never'` (the wide-data fixture overflows at 300 px).
32. `layoutFull.test.ts` (no Chrome): the fixture list covers every `PickerEntry` kind and both Windows states (derived from the
    kinds' union); a `CHROME_PATH` naming nothing exits non-zero with its reason; a leftover folder of a dead pid is removed at start,
    a live pid's is kept.

### 5. Break-its (product code, each restored byte for byte, SHA-256 compared; never a tripwire touched)

The visible guard removed from `post` → RED 6 red. The default entry read through `readPicked` → RED 7 red. The settle timer
removed (a read per pick) → RED 7 red. `commandLine` put in the *Program* cell → RED 15 red. The generation check removed from the
reveal answer → RED 15 red. `gib` back in Held → RED 13 red. `align: 'end'` dropped from one PID column → RED 14 red. A VM path
moved onto the Disk card → RED 11 red. A reader accepting one extra key → RED 5 red. `readPicked` started for a WSL 1 entry → RED 9
red. The `relays(aim)` widening reverted to the default → RED 10 red. One `runnerFor` left unwrapped → RED 29 red. `styled`
hard-coded `true` → the harness half of RED 23 red. `align-items: start` in `.wc-grid` → RED 31 red. A command's `full` field
deleted → the compile fails (required field).

### 6. Build order

1. This section; the plan round (coai, the whole of W1b in one round).
2. **W1b-1** — first the boundary row in PLAN_disk_hygiene_windows_E19.md (§ 3.12); RED 1–23, then: the shell page and the panel,
   the command and both side bar buttons, `fullViewWire.ts` and the boundary widening, the parser, the controller (visibility,
   reads, pick settle, persisted state), `distroEntries.ts`, `lastKnownOf`, `relays(aim)`, `rowFor` exported, `binarySize`,
   `notYet.ts`, the WSL cards and tables, the reveal, `pickedArchive.ts`, the `full` field, the install prompt, the page script and
   its CSS, the test API's `openFullView` / `fullPick` / `fullPosts`. Full suites green.
3. **W1b-2** — RED 24–29: the Windows golden (daemon test, regenerated in WSL as every golden is), the fake's Windows answer,
   `refreshWindows()`, the Windows cards and buttons, the spawn ledger and its wiring, the own-cost card.
4. **W1b-3** — after kit 0.3.0 is on GitHub Packages: the pin, `layout-full.mjs`, the fixtures module, the CI step; RED 30–32; the
   page CSS fixed for whatever the bin finds, each fix named in W1b's *as built* subsection.
5. Break-its (§ 5); docs: `research/module_vs_code.md` (the full view's modules, the wire, the read table of § 3.2, a sequence
   diagram open → ready → reads → post / pick / reveal, the ledger, the units helper), `research/architecture.md` (the second page
   and its resource root; the ledger around the process door), `research/module_tests.md` (the layout check, the Windows golden, the
   host scenario and what they do not prove), README (the command and the full view), PLAN_disk_health.md's E18 row W1b marked built.
6. Full suites: `npm run typecheck`, `lint`, `test`, `test:host`, `check:vsix`, `layout:full`; the daemon tests that read
   `src_vs_code/` and the goldens (`GoldenContract`, `FixturePrivacy`, `Architecture`, `ReleaseExtension*`, the new Windows golden
   test); the family checks; a private-data scan before the push.
7. Reviews: the coai code round over the branch, an own Opus review, and a Fable security check of the reveal road and the wire
   (command lines are the one secret-adjacent thing this page carries); the pull request; merge on every check green.

### 7. What W1b does not prove

- A real WSL 1 row (W1a § 7) and Docker Desktop renaming its distros (the two exact names are matched).
- The Windows golden is a fixed sample: the owner's machine's Windows answer is read once by hand after deploy (POST_DEPLOY), not by
  a test.
- `styled: true` proves that the stylesheet's grid rule applied under the nonce, not every rule; the layout check runs WITHOUT the
  CSP (a static page) and only at its three widths, four themes and three sizes — VS Code's own zoom and other fonts are not covered.
- A real keyboard and screen reader on the picker and the tables (W5; the kit's Storybook for the components).
- The ledger counts this window's spawns only: other windows, the daemon's timers and the root units are E15.S9's.
- Sort and search survive neither a hide nor a re-post that changes a table's rows (the page's state, lost by design with the
  webview); a revealed command line likewise.
- A layout under data larger than the wide-data fixture.

### 8. Growth surfaces (planning-docs rule)

| Surface | Projected size | Retired by | Interrupted |
|---|---|---|---|
| The spawn ledger | ≤ 24 hours × ~25 keys ≈ 600 small entries, < 50 KB per window | the hour rollover (`LEDGER_HOURS`), the extension's disposal | in memory; nothing to sweep |
| `lastKnownOf` snapshots | ≤ the machine's distros × ≈ 60 KB (W1a §3.3's bound), under 1 MB at a dozen | `dropUnlisted`, `clear()` for the default, disposal | in memory; nothing to sweep |
| The reveal texts | per reveal table, the posted rows' command lines (≤ 200 chars each; tens of rows) — < 50 KB | replaced per table generation; dropped by `pageClosed` | in memory |
| `workspaceState['wslCare.fullView']` | one object < 200 bytes | overwritten on every tab / pick | written whole |
| The layout pages | one folder per run, ≈ 1–2 MB (copies of `full.js` / `full.css` + ~12 pages) | removed in `finally`; a dead run's folder swept at the next start | the sweep by pid |
| `contracts/golden/head/status-windows.json` | one file, ≈ 20 KB | replaced on regeneration | written by the test only under `WSL_CARE_WRITE_GOLDENS=1` |
| The pick timer | one at a time | replaced on each pick, cleared by `pageClosed` | — |

### 9. Definition of Done

- [ ] Goals 1–8 hold; RED 1–32 seen red for the real reason, then green; the break-its of § 5 turned the named test red.
- [ ] The default distro's behaviour, the side bar, the status bar and the Logs and Archive pages are unchanged
      (`distroSwitch`, `poller` churn numbers, `clientFlows`, `commandButtons`' side bar half, the host suites green without edits to
      their expectations — new tests only, and W0's `fullView.test.ts` expectation updated for `ready`).
- [ ] No command line in any posted view; every reveal answered by the host only, by generation.
- [ ] The kit pinned to exactly 0.3.0; `layout:full` green locally and in CI.
- [ ] Docs of § 6 step 5 updated; the E19 boundary row written; the plan round and the code round resolved; merged; this section's
      *as built* written and the `todo/README.md` row updated.

### 10. Size, and the commit shape

Counted against § 3: about fifteen new host modules (`src/fullView/` ×9, the wire, the ledger, the units helper, `notYet`, the
fixtures module, the layout script), four page files and the page CSS, edits in `extension.ts`, `package.json`, `panelHtml.ts`,
`messages.ts`, `commandButtons.ts`, `viewModel.ts`, `outcomeStore.ts`, `poller.ts`, `cleanupHost.ts`, `installDaemon.ts`,
`WslCareClient.ts`, `media/panel.js`, one daemon test and one golden, and some twenty test files — the largest extension story so far,
with a RED list of 32. Read in one sitting it is not reviewable; read as three slices it is, because each leaves `main`-quality
behaviour (the side bar untouched) and each has its own proof:

| Commit | Contents | RED | Why it stands alone |
|---|---|---|---|
| **W1b-1** panel + picker + WSL tab | § 3.1–3.6, 3.8 (WSL side), 3.11 | 1–23 | the page opens, reads, picks and reveals; the Windows-side buttons are declared `notInFull` for one commit |
| **W1b-2** Windows tab + ledger | § 3.7, 3.8 (Windows side), 3.9 | 24–29 | the Windows golden and the ledger are independent of the picker |
| **W1b-3** layout check | § 3.10 after kit 0.3.0 | 30–32 | the only slice that waits on another repository |

**Recommendation, respecting the gate operator's order** ("a piece of a split already under way: build it as one unit"): ONE branch
(`feat/wc-e18-w1b-full-view`), ONE plan round over this whole section, three commits in the order above, ONE code round and ONE pull
request — W1a's shape (§ 9a). Were the order not standing, the three commits are the three pull requests it would have been cut into.
K2 is the critical path: W1b-1 and W1b-2 are built while K2 is reviewed and released; the code round waits for W1b-3, because the
unit is not shipped without its layout proof.

### 10a. The gate's plan round (session `6fc851ec`, 2026-10-22)

Two reviewers answered; `proceed` at 5 of 6. Five findings accepted and one rejected; the accepted ones bind the build as much as
§§ 3–4 do:

1. **`layout:full` is bounded.** Each bin invocation runs under a ceiling (`LAYOUT_PAGE_MS`, a named constant) and the whole script
   under another; the bin's own deadlines and its browser-tree stop by PID (K2 § 9 #3, #5) do the killing; on a ceiling reached the
   script stops the bin by ITS pid, never by image name, and exits non-zero naming the page. RED 32 gains a stub bin that never
   exits: the script returns non-zero within the ceiling and leaves no child.
2. **A failed distro list has its own state.** When `listDistros()` answers a failure on open, *Refresh* or becoming visible, the WSL
   tab shows "WSL did not list its distributions — <failureText sentence>" with a *Refresh* button and keeps the last good list's
   picker muted (or none); RED 6 gains the three triggers with a failing list.
3. **K2 is a hard prerequisite of W1b-3, with no stub.** W1b-1 and W1b-2 are built and committed while K2 is reviewed and released;
   W1b-3, the code round and the pull request start only after kit 0.3.0 is on GitHub Packages and verified by its release run. No
   mock of the bin ever stands in for it in CI (a check that cannot run has not passed). Google Chrome is on GitHub's ubuntu image;
   its absence is a red run that says so.
4. **A distro action acts on the distro it was RENDERED for.** The host keeps the target of the last eight pick generations; a
   distro action's `pick` selects that target, so a click that lands after a pick still acts on the distro its button was drawn for —
   and its modal names that distro (W1a) — instead of being dropped. A generation older than the eight is refused with "The distro
   changed — press the button again." RED 7 gains: a *Clean…* rendered for Ubuntu, posted after a pick of Debian, opens Ubuntu's
   modal and builds `-d Ubuntu`; one from nine generations back is refused with the notice.
5. **A pick shows its state at once.** The picked entry's mark is "checking…" (neutral) from the pick until its read settles, so the
   page never presents another moment's figures as current; RED 7 asserts the mark in the view posted at once.

Rejected: **keeping revealed command lines across a hide** — a revealed line vanishing when the page is hidden is deliberate. The
command line is the one secret-adjacent thing this page carries (E15.S7's redaction notwithstanding), the owner's rule is "arguments
hidden by default", and restoring reveals automatically would re-show them without a click. A data-only re-post with equal rows
already keeps a table's generation (§ 3.6), so a background refresh does not wipe a reveal.

### 11. Questions the specification could not settle

0. **Settled 2026-10-22:** K2 ships `--single-line <selector>` (kit `todo/PLAN_layout_check_bin.md` §1.2, §9 #6), so `layout:full` also runs `--single-line '.kit-table__cell--end'` over the PID columns — question 1 below is answered.
1. **PIDs that never wrap, proved in Chrome.** The kit keeps an `align: 'end'` cell on one line above `30em` and W1b stacks every PID
   table at `narrow` or wider (RED 14 holds the specs), but the K2 bin as described checks overflow and card heights, not "one line".
   Ask K2 for an optional single-line selector (`.kit-table__cell--end` in an unstacked table); without it, RED 14 is the proof.
2. **The E19 boundary row** (§ 3.12) belongs in PLAN_disk_hygiene_windows_E19.md, outside this specification's write scope: it is
   written in W1b-1's first change unless the coordinator writes it before the plan round.

