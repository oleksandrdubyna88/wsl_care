# PLAN — the extension carries the attested Windows daemon (`bin/wsl-care.exe` in a `win32-x64` `.vsix`)

> Status: **IMPLEMENTED, 2026-10-11 (the code, PR #108).** Open, and the owner's: the first `win32-x64` extension release,
> POST_DEPLOY item 6 against the real install folder, and the panel's Windows row on the owner's machine (§8 items 2–4).
> What shipped differently is in §10 (the as-built record).
>
> **Scope:**
> - `release-extension.yml`: the build, the attest job, and every job that names the `.vsix`;
> - `ci-extension.yml`;
> - `src_vs_code/scripts/check-vsix.mjs`, `vsix-files.txt`, `.vscodeignore`, `min-daemon.json`;
> - the release scripts that name the package or the daemon versions;
> - `POST_DEPLOY.md` item 6;
> - the one module that will start the bundled exe.
>
> **Related docs:**
> - [PLAN_wsl_care_daemon.md](../todo/PLAN_wsl_care_daemon.md), **E7.S5a** at `:2463` — the row this plan details. The coordinator calls it
>   "E7.S3". E7.S3 itself is the tag check of `:2500-2502`. Finding #13 is at `:810`; the S5 test is at `:2487`.
> - [PLAN_twenty_sessions_all_day.md](../todo/PLAN_twenty_sessions_all_day.md) § S7b — design item 1, the owner's decision.
> - [module_vs_code.md](module_vs_code.md) and [architecture.md](architecture.md) § *The package and its
>   leak checks*, § *`release-extension.yml`*.
> - The measurement: [2026-10-10_windows_binary_bundle_facts.md](2026-10-10_windows_binary_bundle_facts.md).

## 1. Goal and why now

S7b's Windows side and the panel's Windows numbers need the Windows build of the daemon on the owner's machine. Today the
extension never starts `wsl-care.exe`: `src_vs_code/src` has no `extensionPath`/`asAbsolutePath`, and `fieldMap.ts:108` says the
Windows fields "arrive in E7.S3".

The daemon release already builds, attests and publishes it:
- `wsl-care-<v>-win-x64.zip` holds `wsl-care-<v>-win-x64/wsl-care.exe`;
- the ZIP is attested by `release.yml@refs/tags/daemon-v<v>`;
- its `.sha256` is published beside it.

**Goal:** the extension's `.vsix` carries exactly the bytes of that attested member, and no step of the extension's release can
ship different bytes unnoticed.

## 2. Measured first (2026-10-10, read-only, on the owner's machine — [the record](2026-10-10_windows_binary_bundle_facts.md))

- **The asset verifies.** The `daemon-v0.3.0` asset passed `sha256sum -c`, and `gh attestation verify --cert-identity
  …/release.yml@refs/tags/daemon-v0.3.0 --deny-self-hosted-runners`, and with `--source-ref refs/tags/daemon-v0.3.0` too (another
  tag is refused).
- **It runs here.** The exe is 13,676,544 bytes, Authenticode `NotSigned`, with no Mark of the Web, and its **`--version` ran under
  the enforced Smart App Control.**
  - That is a fact about THESE bytes, not a property of the design: every release's exe is new bytes, and SAC's reputation
    check is per binary.
  - Authenticode signing would be the durable fix, and it is out of scope. So a refused start must always be shown (§3.7).
- **The leak scan would refuse it.** The exe carries `D:\a\` (the runner's build path) and `\wsl`, so the `.vsix` text scan
  (`vsixCheck.ts:73-80`) would refuse it.
- **The package name changes.** vsce 4.0.0 names a targeted package `${name}-${target}-${version}.vsix`
  (`@vscode/vsce/out/package.js:1455-1464`).

## 3. Design

1. **Which exe: a fourth daemon pin, `bundledDaemon`** (own Fable review, finding 2).
   - It lives in the committed `src_vs_code/min-daemon.json`, beside the render, actions and install minima.
   - **Why not reuse `installDaemon`.** It is 0.1.2 (`min-daemon.json:4`, `handshake.ts:26`), older than the Windows features this
     bundle serves. Raising it would also change what *Install daemon* types for the distro, and it is tied to POST_DEPLOY's
     WSL-side stamp (`release-extension-guard.sh:149-152, 190`).
   - **What the guard checks.** `bundledDaemon` must be a published, non-draft release with a `win-x64` asset, and the guard
     emits `bundled_daemon`. There is no WSL-side stamp rule: the Windows binary is verified after install by POST_DEPLOY
     item 6 (§3.8).
   - **What changes with it:** the three-member test (`ReleaseExtensionWorkflowTests:303-312`), the guard, `check-vsix`'s
     version findings, and `bundle.mjs:52`.
   - **No "one version on both sides".** The WSL side runs whatever the owner installed, and the panel names each side's
     version.
2. **One fetch script**, `.github/scripts/fetch-windows-daemon.sh <version> <out-dir>`, in POSIX sh plus python3.
   - **Who uses it:** the release build, PR CI, and developers (reuse-first).
   - **Its steps:**
     1. `gh release download daemon-v<v> -p 'wsl-care-<v>-win-x64.zip' -p 'wsl-care-<v>-win-x64.zip.sha256'`. The download must be
        exactly those two files, and the `.sha256` line must name that zip (`verify-release-assets.sh`'s rule).
     2. `sha256sum -c`.
     3. `gh attestation verify <zip> --repo oleksandrdubyna88/wsl_care --cert-identity
        https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v<v>
        --source-ref refs/tags/daemon-v<v> --deny-self-hosted-runners`.
        - The identity is EXACT, never a `--signer-workflow` prefix (module_tests O4).
        - **`--source-ref` is kept.** The workflow identity alone does not say which source revision the provenance records
          (coai code round `52122128`). The Fable review's worry that it was unmeasured is answered by a measurement: with gh
          2.97.0, the right tag exits 0 and another tag is refused (exit 1), recorded in the research note.
     4. The zip is read by python3 `zipfile`, as `compare-installed-extension.sh:22-48` does. The rules:
        - it holds exactly ONE file entry, named exactly `wsl-care-<v>-win-x64/wsl-care.exe`;
        - a directory entry beside it is allowed, because `package-daemon.sh:82`'s `7z a -tzip <folder>` writes one;
        - any other entry, a symlink entry, an absolute or `..` name, or a case variant is refused;
        - the entry's bytes are read by its exact name and written to `<out-dir>/bin/wsl-care.exe`. Nothing is ever extracted by
          an archive-supplied path.
     5. Its SHA-256 is written to `<out-dir>/bin/wsl-care.exe.sha256`.
   - **On the windows-latest leg**, paths handed to `gh` (a Windows program) use the E4 B1 spelling: relative, or `cygpath -m`
     (`package-daemon.sh:101-104`).
   - **Any failure** exits non-zero and leaves nothing in `bin/`.
   - **`GH_TOKEN`** goes on the fetch step's own `env:` only, as the guard's does — never job-level in the build job, where
     dependency scripts run.
3. **One `win32-x64` package** (owner decision A1; Q-B1 answered 2026-10-10: no universal package). `npm run package` gains
   `--target win32-x64`.
   - **"Targeted only" means:** on Linux, macOS and Windows on Arm, the Marketplace keeps serving the last compatible version —
     universal 0.3.1 — FOREVER. Those platforms are frozen there.
   - **One naming function per language:**
     - a POSIX `vsix_name <version>` in `.github/scripts/lib/`, sourced by the workflow, `verify-extension-assets.sh`, and
       POST_DEPLOY item 6 (which runs under `sh`, `versions.sh:5`);
     - `ReleaseFiles.ExtensionVsix` in C#;
     - `check-vsix.mjs`'s default in Node, printable with `--print-default-name`.

     **One cross-language test** (coai plan round `52122128`) asks all three for the same version and requires the same name.
   - **Every consumer moves with it:**
     - `release-extension.yml`: `test -f`, `mv`, `--packagePath`, `cmp`, and the attest job's `subject-path`. That last one is a
       YAML input, so a prior step's `$GITHUB_OUTPUT` feeds it — never retyped.
     - `vsixCheck.test.ts:209`.
     - `verify-extension-assets.sh`. From this release on it REFUSES the old universal name, and the rollback text says the name
       is universal before this release and targeted from it.
     - The docs: `README.md:888, :981`, `docs/repo-settings.md:194-210, :352-353`, `architecture.md:2131`,
       `module_tests.md:793, :813, :3304`, and `ci-extension.yml:17, 27, 96` ("one universal .vsix").
   - **`marketplace-served.cjs`** (the skip's and the wait's reader) counts a version as served only when the entry's
     `targetPlatform` is `win32-x64`. Until now it ignored the target (`:20-24`). So a universal version uploaded by hand does
     not read as the targeted one.
   - **`check-vsix --release`** asserts that `extension.vsixmanifest` carries `TargetPlatform="win32-x64"` — the artefact's truth,
     not the file name.
   - **The install folder** for item 6 is `<publisher>.<name>-<v>-win32-x64`. It is checked against the first real install
     (Q-B2).
4. **The `.vsix` checks in the build — an EARLY, friendly failure only** (§3.5 holds the real check):
   - `vsix-files.txt` gains `bin/wsl-care.exe`, and `.vscodeignore` gains `!bin/wsl-care.exe`. The exact allowlist
     (`vsixCheck.ts:63-71`) still refuses anything else.
   - The member at exactly `extension/bin/wsl-care.exe` is exempt from the TEXT scan, and only when its SHA-256 equals
     `bin/wsl-care.exe.sha256`.
   - `.exe` is NOT added to `BINARY` (`vsixCheck.ts:40`), because that would exempt any exe anywhere. Any other binary-looking
     entry is still scanned, or refused by the allowlist.
   - `productName.test.ts:39` excludes that exact path from its old-name read.
   - `--release` requires the digest file.
5. **The REAL check: the attest job re-derives the bytes before it signs** (own Fable review, finding 1, Blocking).
   - **Why.** The build job is the untrusted domain by design: `npm ci` with install scripts, `npm test`, a downloaded VS Code,
     and `vsce package` → `vscode:prepublish` → esbuild. Any of them can rewrite BOTH `bin/wsl-care.exe` and its digest file
     after the fetch, so the build's own compare proves nothing alone.
   - **Where.** The attest job is the only job with no npm and no node, on a sparse `.github/scripts` checkout
     (`release-extension.yml:193-222`).
   - **What it does,** before `attest-build-provenance`:
     1. runs the fetch script's verification for `bundled_daemon` itself: download, `sha256sum -c`, the exact attestation;
     2. reads the one member with python3;
     3. compares it byte for byte with `extension/bin/wsl-care.exe` inside `from-build/<vsix>`.

     Any difference fails the job, and nothing is signed.
   - **Its test.** `ReleaseExtensionWorkflowTests.The_attest_job_signs_the_downloaded_build_and_runs_nothing_else` (`:252-270`) is
     widened RED-first: gh and python3, still no npm and no node.
   - github-public repeats the compare beside its existing `cmp` (`:388-396`).
6. **The workflows and the developer skip:**
   - The build runs the fetch before `npm test`. The order test (`ReleaseExtensionWorkflowTests.The_build_packages_once…`,
     `:220-248`) gains it, and `ci-extension.yml` does the same with the same pin.
   - The pin has ONE reader per context: the guard's `bundled_daemon` in the release, and `min-daemon.json` read as JSON in CI.
     Never a regex over TypeScript.
   - `npm test` reads every allowlisted file (`vsixCheck.test.ts:25-30`, `productName.test.ts:39,46-51`).
     - Off CI, a missing exe is SKIPPED, naming `npm run fetch:windows-daemon`.
     - With `CI` or `GITHUB_ACTIONS` set, a missing exe FAILS, naming the fetch. A misordered workflow can never go green with
       the entry dropped (own review, finding 3).
     - The skip also drops the entry from the expected list, so the allowlist test never passes on a partial archive.
   - `bin/` is git-ignored.
   - Build-order steps 4 and 5 land TOGETHER, so no release-please tag meets a half-renamed chain.
7. **Starting it: the minimal client, not S7b's features.**
   - **Which host.** The extension is `extensionKind: ["ui"]` (`package.json:40-42`, pinned by `manifest.test.ts`). So VS Code runs
     it in the LOCAL Windows UI host, even for a WSL workspace (coai plan round `52122128`).
     `WindowsCareClient` still refuses unless `process.platform === 'win32'` and the URI scheme is `file`.
   - **One module names the exe:** `src_vs_code/src/client/WindowsCareClient.ts`, held by a structure test with a planted
     companion.
     - It starts `vscode.Uri.joinPath(context.extensionUri, 'bin', 'wsl-care.exe')` by absolute path, with `CREATE_NO_WINDOW`
       and the runner's ceilings.
     - It uses the SAME seam as `WslCareClient` (`extension.ts:112-115`: the Test-mode fake, or the closed runner), never the
       Windows-Time `runnerFor({ kind: 'real' })` (`:177, :203`). So `test:host` never starts the real exe, and `suite.ts:227`
       and `:245` stay true. The strict fake learns the bundled exe path and answers `--version`.
     - It takes `VERBS.version`; only `verbs.ts` spells `--version` (`structure.test.ts:26, 88-90`).
   - **When it runs:** on the panel's first render or refresh, never at activation (`onStartupFinished`), because nothing is
     started unasked.
   - **Its one use here:** the panel's Windows row shows the bundled version, compared with `bundledDaemon`; a mismatch is shown.
   - **A refused start.** Node's `spawn` does not surface the Win32 code: the refusal reaches `runner.ts:93-94` as
     `error.message`. The row shows that message as Windows gave it, never a guessed `0x800711C7`.
8. **POST_DEPLOY item 6** compares the installed extension with the attested `.vsix` file by file, and the exe is one more member
   compared byte for byte. It ALSO asserts:
   - the installed `bin/wsl-care.exe` equals the daemon release's attested member;
   - `--version` prints `<bundledDaemon>+…`.

   Only the names change (§3.3).

## 4. Never

- Never package an exe the fetch did not verify, and never sign a `.vsix` the attest job did not re-check byte for byte.
- Never take the exe from `PATH`, a developer's build, or a release other than the `bundledDaemon` pin.
- Never exempt any member but `extension/bin/wsl-care.exe` from the text scan, and never that one without its digest.
- Never extract a zip by an archive-supplied name.
- Never start the exe from anywhere but the extension's own `bin/`, by absolute path, through the client's seam.
- **Never run `bin/wsl-care.exe` elevated, from a scheduled task, or as SYSTEM, and never register its path outside the extension**
  (own review, finding 5).
  - The extension folder is user-writable, and the repository already installs a SYSTEM task, the Windows Time guard.
  - An elevated or service-run daemon is installed into a protected folder by its own installer (E11).
  - A structure test holds that the exe path never reaches the `windowsTime/` or `root/` modules.
- Never replace an asset on a release, and never publish a second package per release.

**A forward note for S7b:** a long-lived child running from the extension folder locks its image on Windows and blocks the
extension's update or uninstall. This story's short `--version` start is fine; S7b's watch is not, and must copy the exe or run
briefly. S7b must also own the exe's whole process tree (a Job Object), so a timeout ends every child it starts; `--version`
starts none (§10).

## 5. Tests (RED first)

- **`fetch-windows-daemon.sh`** (the Linux legs and the windows-latest leg; a fake `gh`; 7z-shaped zip fixtures):
  - refused: a `.sha256` mismatch, a `.sha256` naming another file, a failed attestation, a symlink / absolute / `..` /
    case-variant member, a second file, a missing member, a stray third asset in the download, a pinned release without the
    win-x64 asset;
  - accepted: the 7z directory entry;
  - nothing is left in `bin/` on a refusal, and the digest file equals the member's.
- **The plan of record's S5 test** `The_bundled_exe_is_the_attested_asset_byte_for_byte`, as an attest-job flow with the fake
  `gh`: a `.vsix` whose exe differs from the attested member is refused before signing.
- **`check-vsix`:**
  - the exe outside the allowlist is refused;
  - a wrong digest is refused;
  - the exemption covers only the exact path, with its digest;
  - another `.exe` is still scanned or refused;
  - `--release` without the digest is refused;
  - the vsixmanifest must carry `TargetPlatform="win32-x64"`.
- **The skip:** refused under `CI` / `GITHUB_ACTIONS`; off CI it is allowed only with the entry dropped from the expected list.
- **The workflow tests:**
  - the build fetches before `npm test`;
  - one `vsce package --target win32-x64`;
  - every job names the package through the function;
  - the attest subject comes from a step output;
  - the attest job's compare runs before `attest-build-provenance`, and the job still runs no npm or node;
  - `GH_TOKEN` is set per step;
  - CI fetches with the same pin.
- **The cross-language name test:** three implementations, one name. `verify-extension-assets.sh` accepts the targeted name and
  REFUSES the old universal one.
- **`marketplace-served.cjs`:** a `win32-x64` entry is served; a universal entry of the same version is not.
- **Extension, unit and host tier:**
  - `WindowsCareClient` starts only `<extension>/bin/wsl-care.exe`, by absolute path;
  - off Windows, or with a non-`file` URI, nothing starts;
  - the closed runner starts nothing;
  - the fake answers `--version` and records the exact path;
  - the panel shows the bundled version, and a mismatch with `bundledDaemon`;
  - a refused start shows its message;
  - structure: `--version` is spelled only in `verbs.ts`; only `WindowsCareClient` names the exe (with a planted companion);
    the exe path never reaches `windowsTime/` or `root/`;
  - nothing is started at activation.
- **POST_DEPLOY item 6:** the installed exe equals the attested member, and `--version` prints the pin.
- **Break-it on product code**, each red and then restored:
  - the attest-job compare removed;
  - the build's digest compare off;
  - the identity loosened to a prefix;
  - the member-name check loosened to `endsWith('wsl-care.exe')`;
  - the CI-skip guard removed;
  - the vsixmanifest target check removed.

## 6. Owner questions

- **Q-B1 — answered 2026-10-10: no universal package.**
- **Q-B2:** the installed folder of the targeted package. It is checked on the first install, and POST_DEPLOY item 6 follows the
  real name.

## 7. Build order

1. The fetch script and its flows.
2. `bundledDaemon` in `min-daemon.json`, the guard and `check-vsix`.
3. `check-vsix`'s exe rules, the allowlist, and the CI-guarded skip.
4. The naming function, every consumer, `marketplace-served.cjs`, and the vsixmanifest check.
5. `release-extension.yml` (with the attest job's re-derivation) and `ci-extension.yml`.
6. `WindowsCareClient` and the panel's version row.
7. Docs: `module_vs_code.md`, `architecture.md`, `module_tests.md`, `docs/repo-settings.md`, `README.md`.

Steps 4 and 5 land in ONE PR. The extension release that carries it is the first `win32-x64` one.

## 8. Definition of Done

- [x] Every RED test above seen red for the right reason, then green; break-it on product code red, then restored.
- [ ] An extension release whose `.vsix` holds `bin/wsl-care.exe` byte-equal to the attested `daemon-v<bundledDaemon>` member, the
      attest job's compare green, published through the Environment's approval.
- [ ] POST_DEPLOY item 6 green against the real install folder, the exe compared and its version read.
- [ ] The panel's Windows row shows the bundled version on the owner's machine.
- [x] Docs updated; the own Fable review of the code finds no way to ship another exe AS bin/wsl-care.exe (its one way, a
      twin entry or a DLL beside the exe, is closed — §11). The JavaScript the build writes is NOT re-derived; see §11, scope.

## 9. Plan round (coai session `52122128`, 2026-10-10) and the own Fable review

- **coai: proceed.** Two findings, both accepted:
  - the host placement (`extensionKind ["ui"]`, plus the win32 and `file` guards);
  - the cross-language name test.
- **The own Fable review: "revise".** Thirteen findings, all accepted and built into §3–§5:
  - the attest job re-derives the bytes (the blocking finding);
  - a fourth pin, `bundledDaemon`;
  - the CI-guarded skip;
  - the client's seam and `VERBS.version`;
  - never elevated, and never registered;
  - `--source-ref`, which Fable asked to drop as unmeasured: the coai code round asked to keep it, so it was measured and kept;
  - the 7z zip semantics;
  - the full consumer list;
  - the frozen other platforms, `targetPlatform` and `TargetPlatform`;
  - `--version` on first render, with Node's error message;
  - the exact-path text-scan exemption;
  - `GH_TOKEN` per step, and steps 4 and 5 together;
  - the RED additions.
- **One choice differs from the review's alternative.** The review offered to give `bundledDaemon` the same WSL-side stamp
  check. It gets none here, because the Windows binary is verified on the Windows side (item 6), not by a distro install.

## 10. As built (2026-10-11, PR #108)

The seven steps of §7 shipped in that order, steps 4 and 5 in one commit. What differs from §3–§5:

- **Zip member names are read from `orig_filename`.** Python's `zipfile` turns `\` into `/` on Windows, so a member named
  with a backslash passed the name check there. The backslash test was red on the Windows leg for that reason, then green.
- **`attestations: read`** is on the build job, on `ci-extension.yml`'s job and on the github-public job. `gh attestation verify`
  needs it; §3 named only `GH_TOKEN`.
- **The tests that need the exe** read it through `src_vs_code/src/test/support/bundledExe.ts` (`shippedAllowlist`): under `CI` a
  missing `bin/wsl-care.exe` fails; off CI the entry is dropped from the expected list. The client, fake and structure tests
  use a synthetic exe; the `check-vsix` tests use the real one when it was fetched.
- **The Windows row** is a line of the panel's *Health* section (`p[data-windows-daemon]`), read on the panel's refresh only,
  never at activation (`BundledHost`); `buildPanelView` takes it as a seventh parameter.
- **No new timeout key.** The `--version` start uses the existing `wslCare.timeouts.versionSeconds`.
- **The Test-mode fake** has two scenario fields, `bundledExe` and `bundledVersion`, and records the exact path it was asked for.
- **`ReleaseExtensionScriptFlows`' actions-minimum flow** has its own fake `gh`; it needed the asset column for the bundled
  pin's one query. The Linux leg found it.
- **POST_DEPLOY item 6** sources `lib/extension-package.sh`, keeps the `win32-x64` entry, runs `verify-bundled-exe.sh` and reads
  the installed exe's `--version`. It fails by design until the first `win32-x64` release exists.
- **The coai round of this branch** (session `a938a988`) said proceed with one finding: kill the whole process tree on a
  timeout. Rejected for this story with evidence: `wsl-care.exe --version` answers at `src_daemon/src/WslCare.Cli/Program.cs:151`
  (`Output.Answer(stdout, VersionText)`) and starts no child, so the runner's kill of the one process is complete. The
  requirement moves to S7b, whose watch does start work (§4, the forward note).

**Still open** — none of it is code; all of it waits for the owner's release:

- the first `win32-x64` extension release through the Environment's approval (§8 item 2);
- POST_DEPLOY item 6 green against the real install folder, which also answers Q-B2 (§8 item 3);
- the panel's Windows row on the owner's machine (§8 item 4).

## 11. The own Fable review of the code (supply chain, 2026-10-11)

One major finding, seven minor, seven notes. Each was checked against the code; none was rejected.

**Scope of the guarantee (coai cadence consultation a8a5731c, 2026-10-11).** The attest job proves that the package's
`bin/wsl-care.exe` — and no twin of it, and no other `.exe` or `.dll` — is the attested daemon member. It does NOT prove what
the build's JavaScript (`dist/extension.js`, `package.json`) starts: extension code can start any program, and the build
writes that code after `npm ci`. That is the same trust every extension release had before this story; bundling the exe does
not widen it. Closing it needs an independent second build whose bundle is compared outside the signing job — a separate
story, not this one. The consultant also confirmed POST_DEPLOY item 6's source: the released tag's pin is the expected
value, and the package's own claim must not replace it.

- **1 (major) — the attest job looked at one entry only.** VS Code's installer writes `<ext>/<name>` on case-insensitive NTFS
  and the last of two colliding entries wins, so a package with the attested `extension/bin/wsl-care.exe` AND a twin
  (`WSL-CARE.EXE`, `BIN/…`, `bin/./…`, `bin//…`, a backslash) passed `verify-bundled-exe.sh` and installed the twin; a DLL
  beside the exe was not refused either. The build's allowlist catches both, but the build is the untrusted domain. Fixed:
  the attest job now refuses an unsafe or symlink name, two names that are one file case-insensitively, anything but the
  exe under `extension/bin/`, and any other `.exe` or `.dll`. Eight new refusal shapes, seven red before the fix (exit 0);
  break-its (the code-beside rule off, the case fold off) red. The whole allowlist was NOT copied into the attest job: its
  names pass through vsce's renames table in `vsixCheck.ts`, and a second copy of that table is the drift reuse-first forbids.
- **2 — python without `-I`.** Every python of the fetch, the re-derivation and `compare-installed-extension.sh` now runs
  isolated.
- **3 — `native` and `python_cmd` copied into two scripts.** Moved to `.github/scripts/lib/windows-tools.sh`. A third
  `cygpath` spelling in `package-daemon.sh` is not this story's code and is left as it is.
- **4 — `ci-extension.yml` still said "universal".** Fixed.
- **5 — POST_DEPLOY item 6** read the CHECKOUT's pin and matched `--version` with a regex built from it. It now reads
  `min-daemon.json` at the released tag (`gh api …/contents/…?ref=$t`) and compares the part before `+` exactly; the workflow
  test was red first.
- **6 — the release target was a substring of the whole vsixmanifest.** It is now the `TargetPlatform` attribute of the one
  `<Metadata><Identity>`, read with the strict `xmlTree` reader (a comment fails loudly); checked against a real targeted
  package. Red first with the text in a description, in another element and in a comment.
- **7 — the fetch's last moves were outside `refuse`.** The digest is written in the work folder first and both moves into
  `bin/` are under `refuse`. No test: a failed write after a verified fetch (a full disk) is not reachable through the flows'
  seams.
- **8 — the host tier never showed the Windows row's happy path.** A host scenario now sets `bundledExe` to the exact path
  under the extension folder and asserts the row; the Test-mode API gained `bundled()`. It runs on CI's legs (the host tier
  downloads VS Code, which is not done on this machine).
- **Notes:** 9 the guard reads `(.assets // [])`, so a release without assets gets the right refusal; 10 the reason bound
  is a named `MAX_REASON` (display bounds are named constants, as `TEXT_MAX` is beside them, not config keys); 11 the
  extension folder must start with a drive letter — a share and a root without a drive start nothing (red first); 12 an
  explicit `src_vs_code/bin/` ignore line; 14 `verify-bundled-exe.sh` says it trusts what the tag serves at its run time;
  15 the `.mjs` header says a developer's `node_modules/.bin` comes first on PATH. 13 was the uncommitted promotion, now
  committed.
