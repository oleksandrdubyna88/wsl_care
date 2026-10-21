# PLAN — E18 stories: wsl_care consumes the kit, the full view, the slim side bar, Help, accessibility (#87)

> Status: **in progress (2026-10-20)** — the stories document of epic E18 of [PLAN_disk_health.md](PLAN_disk_health.md)
> (§3.4–§3.6 and its E18 table). E18.K1 is done in `dew_flow_vscode_kit` (kit #9 → `655df4de`; **0.2.0 released to
> GitHub Packages**, release PR #16, run 39481672054 green). This document holds **W0** and **W1a**; W1b, W2, W4, W3 and W5
> are added here, each before its own plan round. **W0 is MERGED (2026-10-21, PR #171 → `main` @ `4d051378`)** — § 6b is its code round, § 9 what shipped differently. **W1a is SPECIFIED (2026-10-21, the cornerstone section below; nothing of it built), its plan round next** — recommended as three pull requests in order (W1a § 9).
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
  DISTINCT target its entries follow and reads `runs show` / `runs` from the entry's distribution. The default is asked unless EVERY entry is a picked one
  (so a picked-only journal never touches the default distro — which is what makes RED 20(a) hold through the real follower); the observing poll stays the default's.
- **`ArchiveHost` does not re-read its (default) view after a picked write** — the view is the default's; a per-distro card is W1b's.
- **A picked full check or stop** is written for the picked name and needs no default `status`; `CleanupHost.stop(index, target)` reads the stoppable run from that
  target's snapshot (`snapshotOf`). `HeldPreview` and `PreviewContext` carry `target`.
- **RED 20(c) is a classification plus a scan, not 12 driven commands.** The enumeration drives the seven `distro` commands' flows (`refresh` as `verb:status`); the
  `windowsSide` / `opener` commands are classified in the test (a 20th command fails it) and the modules of the windowsSide ones are scanned to import
  — as values — no WSL client, root controller, cleanup flow or poller. Nothing executes them in a node test (they need VS Code); the host suite does not drive them either.
- **RED 21 lives in `test/host/suite.ts`** (`test:host`), not in `scenarios/clientFlows.test.ts` (a node test, which has no test API): three scenarios — a running Debian
  (`-l -v`, the running list, exactly three `-d Debian` calls, preview / doctor in either order, bar and rendered rows unchanged), a stopped one (no `-d`), a version-1 one (no
  `-d`, no running question of its own). "No `wouldStart` refusal in the fake's log" is asserted as the absence of any `-d` call (the call log holds argv, not exit codes).
- **Not red-first, said plainly:** `installDaemonPicked.test.ts` was green on arrival (the client road is W1a-1's; it pins it for the installer); the enumeration (RED 20) and the
  host scenarios (RED 21) were written after their product slice, their teeth are the break-its below; `statusBar` / `poller` wsl1 cases and the structure scan were red-first.
- Test support added: `support/manualClock.ts` (the clock `poller.test.ts` had inline), `support/distroNames.ts` (`listed` / `pick`), `NO_DISTRO_LIST` in `support/outcomes.ts`;
  `Poller` has a required `listDistros` option and the test API `listDistros()` / `readPicked(name, options?)` (the list first, an unlisted name throws).

**Break-its** (each restored byte for byte, SHA-256 compared, never a tripwire touched): the in-flight key without the target → `clientDistros` 2 red; the picked road through
`--list --quiet` → 6 red; the version check dropped → the 3 wsl1 tests red; `listDistros` not pruning (`knownVersion`, `knownContainers`) → 1 red each; a cast into `DistroName` in the client →
the structure test red; `wsl1` out of `TARGET_FAILURES` → the poller test red; `readPicked` writing through `set` → 9 red; `clear()` emptying the picked map → 3 red; the poller not calling
`dropUnlisted` → 10 red; the store accepting an unlisted name → 3 red; the confirm resolved through the default instead of the preview's target → the controller test red (the
enumeration stays green there: the `distroChanged` guard refuses the call, so nothing wrong is SENT); `rootTarget(picked)` routed through the default → the enumeration + 2 client tests red;
the archive flows ignoring the target → 3 red, the enumeration among them; the journal dropping `targetKind` → 8 red; the follower always asking the default → 1 red; a verb added to
`VERB_NAMES` or an op to `ROOT_OPS` without its row → the compile fails (`VERBS`, `TAILS`, the enumeration's typed tables); `askPicked` through `DEFAULT_TARGET` → all 3 host scenarios red.

**Final counts (extension):** typecheck and lint clean; `npm test` 1736 tests, 1735 pass, 0 fail, 1 skipped (the `clientFlows` / kill-tree timing tests flake under load and pass alone — they did
once in three full runs); `test:host` green on VS Code 1.85.0 and stable, fake and closed; `check:vsix` OK. Daemon classes that read `src_vs_code` (Release build, 0 errors): `ReleaseExtension` 74,
`Architecture` 1 + 26, `InteropModulePath` 8, `FullCheckLine` 22, `FixturePrivacy` 58, `ExtensionPackageName` 2, `ReleaseConfig` 11 — all pass. Family checks clean. The WSL 1 row shape is still Microsoft's documented
layout, not measured here (§ 7).
