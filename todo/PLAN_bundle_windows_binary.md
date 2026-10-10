# PLAN — the extension carries the attested Windows daemon (`bin/wsl-care.exe` in a `win32-x64` `.vsix`)

> Status: **plan only, 2026-10-10, nothing implemented yet.** Scope: `release-extension.yml`'s build and every job that names the
> `.vsix`, `ci-extension.yml`, `src_vs_code/scripts/check-vsix.mjs` and `vsix-files.txt` / `.vscodeignore`, the release scripts
> that name the package, `POST_DEPLOY.md` item 6, and the one module that will start the bundled exe.
>
> Related docs:
> - [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) — **E7.S5a** at `:2463`, the row this plan details; the coordinator calls it
>   "E7.S3". E7.S3 itself is the tag check of `:2500-2502`. Finding #13 is at `:810`: "`--target win32-x64` once E7 bundles the
>   probe".
> - [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) § S7b — design item 1, assumption A1, which the owner
>   confirmed on 2026-10-09.
> - [module_vs_code.md](../research/module_vs_code.md) and [architecture.md](../research/architecture.md) § *The package and its
>   leak checks* and § *`release-extension.yml`*.

## 1. Goal and why now

S7b's Windows stop and the panel's Windows numbers need the Windows build of the daemon on the owner's machine. Today the
extension never starts `wsl-care.exe` (`src_vs_code/src` has no `extensionPath`/`asAbsolutePath`; `fieldMap.ts:108` says the
Windows fields "arrive in E7.S3"). The daemon release already builds, attests and publishes it: `wsl-care-<v>-win-x64.zip`
holds exactly `wsl-care-<v>-win-x64/wsl-care.exe`. The zip is attested by `release.yml@refs/tags/daemon-v<v>`, and its
`.sha256` is published beside it.

**Goal:** the extension's `.vsix` carries that exact exe — the bytes the daemon release attested — and nothing in the
extension's release chain can ship a different one.

## 2. Measured first (2026-10-10, read-only, on the owner's machine)

The attested asset of `daemon-v0.3.0` was downloaded into a scratch folder; nothing was installed.
- `sha256sum -c` passed.
- `gh attestation verify --cert-identity …/release.yml@refs/tags/daemon-v0.3.0 --deny-self-hosted-runners` exited 0.
- The unzipped exe:
  - size 13,676,544 bytes;
  - Authenticode `NotSigned`;
  - no Mark of the Web;
  - **`wsl-care.exe --version` ran under Smart App Control**, which is enforced on this machine, and answered
    `0.3.0+609ba17…`.

  So an unsigned, attested release exe is not blocked here. A blocked start must still be reported, never swallowed.
- The exe's bytes contain `D:\a\` (the GitHub runner's build path) and `\wsl`. The `.vsix` text scan
  (`vsixCheck.ts:73-80`: drive paths, `/home/`, `\\wsl`, e-mail addresses) would therefore refuse it. The scan exists to keep
  THIS machine's data out of the package; a runner path is not that.
- vsce 4.0.0 names a targeted package `${name}-${target}-${version}.vsix` (`@vscode/vsce/out/package.js:1455-1464`).
  `--target win32-x64` therefore gives `ai-os-care-win32-x64-<v>.vsix`, and every place that spells `ai-os-care-<v>.vsix`
  changes with it.

## 3. Design

1. **Which exe.** The exe of the daemon release named by `installDaemon` in the committed `src_vs_code/min-daemon.json`, the
   same pin the extension's *Install daemon* types for the distro. The panel then talks to one daemon version on both sides.
   The guard already proves that release is published and non-draft (`release-extension-guard.sh`, output `install_daemon`).
2. **One fetch script**, `.github/scripts/fetch-windows-daemon.sh <version> <out-dir>`. Release, PR CI and a developer all use
   it (reuse-first). It runs these steps:
   1. `gh release download daemon-v<v> -p 'wsl-care-<v>-win-x64.zip*'`;
   2. `sha256sum -c`;
   3. `gh attestation verify <zip> --repo oleksandrdubyna88/wsl_care --cert-identity
      https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v<v>
      --source-ref refs/tags/daemon-v<v> --deny-self-hosted-runners`. The identity is EXACT; there is never a
      `--signer-workflow` prefix (module_tests O4).
   4. The zip must hold exactly one member, `wsl-care-<v>-win-x64/wsl-care.exe`. Anything else is refused.
   5. Extract it to `<out-dir>/bin/wsl-care.exe` and write its SHA-256 to `<out-dir>/bin/wsl-care.exe.sha256`. This file is
      the digest the later checks compare against, computed in the same job from the attested member.

   Any failure exits non-zero, before packaging.
3. **The package: one `win32-x64` `.vsix`** (owner decision A1, 2026-10-09). `npm run package` gains `--target win32-x64`.
   - **One naming function.** The package name is spelled in ONE place per language, and every consumer reads it from there:
     - shell: a `vsix_name <version>` function in `.github/scripts/lib/`, used by the workflow, `verify-extension-assets.sh`,
       `compare-installed-extension.sh` callers and POST_DEPLOY item 6;
     - C#: `ReleaseFiles.ExtensionVsix`;
     - Node: `check-vsix.mjs`'s default.

     The extension's install folder for item 6 is `<publisher>.<name>-<v>-win32-x64`. That must be checked against a real
     install of the targeted package, not assumed — §6 Q-B2.
   - **Other platforms.** A targeted-only package is not offered on Linux, macOS or Windows on Arm. The extension's
     `extensionKind` is `["ui"]` and it already says "Windows + WSL only" off Windows, so x64 Windows is the platform that
     matters. A universal fallback package beside it would double the release chain. **Assumption:** no universal package;
     owner question Q-B1.
4. **The `.vsix` checks** (`check-vsix.mjs`, `vsixCheck.ts`):
   - `vsix-files.txt` gains `bin/wsl-care.exe`, and `.vscodeignore` gains `!bin/wsl-care.exe`. The exact-allowlist check
     (`vsixCheck.ts:63-71`) still refuses anything else.
   - **The exe is exempt from the TEXT scan, and held by its DIGEST instead.** The member's SHA-256 must equal
     `bin/wsl-care.exe.sha256` from step 2. The attestation proves those bytes were built by `release.yml` on a GitHub-hosted
     runner, from the tagged source, and not on this machine. The rule is narrow: exactly that one path, exactly that digest.
     Any other binary member is still scanned as text, or refused by the allowlist.
   - `--release` additionally requires the digest file to be present: a release can never package without the attested exe.
5. **The workflows:**
   - `release-extension.yml`'s build runs the fetch script before `npm run package`, then `check-vsix --release` as today. The
     attest job attests the targeted `.vsix` by its new name.
   - github-draft, publish-marketplace and github-public read the name through the one function.
   - `ci-extension.yml` (PRs, both legs) runs the same fetch with the same pin, so the package and its allowlist are checked
     on every PR. It needs network and `gh`, which both CI images have.
   - The tests that pin the workflows change with them (`ReleaseExtensionWorkflowTests`, `ReleaseConfigTests`,
     `ReleaseExtensionScriptFlows`), RED first.
6. **Developers.** `npm test` reads every allowlisted file (`vsixCheck.test.ts:25-30`, `productName.test.ts:39,46-51`).
   - A missing `bin/wsl-care.exe` makes those readers SKIP that one entry, with the reason "fetch it with
     `npm run fetch:windows-daemon`". The suite never fails offline for want of a release asset.
   - CI never skips: it fetched the exe first.
   - `bin/` is git-ignored.
7. **Starting it — the minimal client, not S7b's features:**
   - `src_vs_code/src/client/WindowsCareClient.ts` is the only module that starts the bundled exe, mirroring
     `WslCareClient.ts:16`'s rule for `wsl.exe`.
   - It starts the exe by absolute path, `vscode.Uri.joinPath(context.extensionUri, 'bin', 'wsl-care.exe')`, through the one
     runner (`runner.ts:20`; `structure.test.ts:59-61` keeps `child_process` there), with `CREATE_NO_WINDOW` and the runner's
     ceilings. Never `PATH`.
   - This story uses it ONCE: a `--version` read shown on the panel's Windows row. That proves the bundle runs on the
     machine, and a start refused by Windows (e.g. Smart App Control `0x800711C7`) is shown by its code.
   - S7b's watch, stop and button are S7b's, not here.
8. **POST_DEPLOY item 6** compares the installed extension with the attested `.vsix` file by file. The bundled exe is one more
   member compared byte for byte. Only the package and folder names change (step 3).

## 4. Never

- Never package an exe the fetch script did not verify: `sha256sum` and the exact attestation, both passing.
- Never take the exe from `PATH`, a developer's build, or a release other than the `installDaemon` pin.
- Never exempt any member but `bin/wsl-care.exe` from the text scan, and never that one without its digest matching.
- Never start the exe from anywhere but the extension's own `bin/`, by absolute path, through the runner.
- Never replace an asset on a release (the existing rule), and never a second package per release in this story.

## 5. Tests (RED first)

- **`fetch-windows-daemon.sh`** (Linux legs, fake `gh`/`sha256sum`/zip fixtures):
  - an exact identity is passed;
  - a `.sha256` mismatch, a failed attestation, a second zip member and a missing member each exit non-zero, and nothing is
    left in `bin/`;
  - the digest file equals the member's.
- **`check-vsix`:**
  - the exe outside the allowlist is refused;
  - the exe with a wrong digest is refused;
  - the exe is exempt from the text scan only with its digest;
  - any other binary is still scanned;
  - `--release` without the digest file is refused.
- **The workflow tests:**
  - the build fetches before it packages;
  - one `vsce package --target win32-x64`;
  - every job names the package through the one function;
  - the attest subject is the targeted name;
  - CI fetches with the same pin.
- **`verify-extension-assets.sh`** accepts `ai-os-care-win32-x64-<v>.vsix` and its `.sha256`, and still refuses two packages
  or a stray file.
- **Extension:**
  - `WindowsCareClient` starts only `<extension>/bin/wsl-care.exe` by absolute path;
  - a blocked start is reported by its code;
  - the panel shows the bundled version;
  - `structure.test.ts` holds that `WindowsCareClient` is the one module naming the exe.
- **Break-it on product code:**
  - the digest compare off;
  - the exact identity loosened to a prefix;
  - the member-count check off.

  Each must turn its test red, and each is restored.

## 6. Owner questions (non-blocking; the assumptions stand until answered)

- **Q-B1.** Should a universal package (without the exe) be published beside the `win32-x64` one, for Windows on Arm, Linux and
  macOS VS Code? **Assumed: no.**
- **Q-B2.** Is the installed folder of the targeted package `remsoftdev.ai-os-care-<v>-win32-x64`? This is checked on the first
  install of the targeted release, and POST_DEPLOY item 6 follows the real name.

## 7. Build order

1. The fetch script and its flows.
2. `check-vsix`'s digest rule and the allowlist, with the developer skip.
3. The naming function and every consumer.
4. `ci-extension.yml`.
5. `release-extension.yml` — only after extension 0.3.1 is public; no release-extension edit before.
6. `WindowsCareClient` and the panel's version row.
7. Docs: `module_vs_code.md`, `architecture.md`, `module_tests.md`, `docs/repo-settings.md`.

It ships with the next extension release, whose release-please PR must not merge before this lands, or after it with a red
check.

## 8. Definition of Done

- [ ] Every RED test above seen red for the right reason, then green; break-it on product code red, restored.
- [ ] An extension release whose `.vsix` holds `bin/wsl-care.exe` byte-equal to the attested `daemon-v<installDaemon>` member,
      published through the Environment's approval.
- [ ] POST_DEPLOY item 6 green against the real install folder.
- [ ] The panel's Windows row shows the bundled version on the owner's machine.
- [ ] Docs updated; the own Fable review of the supply chain finds no way to ship another exe.
