# Changelog

## [0.2.0](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.1.2...daemon-v0.2.0) (2026-10-07)


### Features

* **daemon:** MCP server instances of the AI agents in status and every full run (E7.S2d) ([#38](https://github.com/oleksandrdubyna88/wsl_care/issues/38)) ([c5b6542](https://github.com/oleksandrdubyna88/wsl_care/commit/c5b6542d31fd882e65155f6525df501bac78ba2a))


### Bug Fixes

* **cli:** doctor's versions line is printable, the act parser neither mutates nor copies per key ([#36](https://github.com/oleksandrdubyna88/wsl_care/issues/36)) ([664e464](https://github.com/oleksandrdubyna88/wsl_care/commit/664e464ead7a607dcf667d51c4ff37d8171ea3ab))
* **daemon:** collect says it is measuring before its slow phases start ([#32](https://github.com/oleksandrdubyna88/wsl_care/issues/32)) ([8fd2ee2](https://github.com/oleksandrdubyna88/wsl_care/commit/8fd2ee279e5d1877ad7dc58541e4ce20cba59411))
* **daemon:** the PR [#10](https://github.com/oleksandrdubyna88/wsl_care/issues/10) retro round — interop on any entry, the launch instant, read errors as reasons, raw super options ([#42](https://github.com/oleksandrdubyna88/wsl_care/issues/42)) ([cb4e9ea](https://github.com/oleksandrdubyna88/wsl_care/commit/cb4e9ea2a01934581be2f8c540d13e84b914018e))
* **daemon:** the PR [#11](https://github.com/oleksandrdubyna88/wsl_care/issues/11) retro round — accepted runs wait, traces kept, derived success exits ([#43](https://github.com/oleksandrdubyna88/wsl_care/issues/43)) ([7e65e16](https://github.com/oleksandrdubyna88/wsl_care/commit/7e65e16cf16823c1af29dcb5251c6822f9263723))
* **daemon:** the PR [#16](https://github.com/oleksandrdubyna88/wsl_care/issues/16) retro round — an unknown kind is read as unknown, never guessed ([#44](https://github.com/oleksandrdubyna88/wsl_care/issues/44)) ([05ec294](https://github.com/oleksandrdubyna88/wsl_care/commit/05ec2944796e891abfccc29815d7633787f6010e))
* **daemon:** the timer runs no action while the user layer cannot be read ([#35](https://github.com/oleksandrdubyna88/wsl_care/issues/35)) ([abcdb9f](https://github.com/oleksandrdubyna88/wsl_care/commit/abcdb9f6303e9d0d4a8d16d8a85980360581456b))
* **install:** the PR [#8](https://github.com/oleksandrdubyna88/wsl_care/issues/8) retro round — purge under the run lock, wall-clock ceilings, a calendar systemd accepts for a daily timer ([#34](https://github.com/oleksandrdubyna88/wsl_care/issues/34)) ([4237537](https://github.com/oleksandrdubyna88/wsl_care/commit/4237537d12d1b128bdf5bf6bdf19891919497ed4))

## [0.1.2](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.1.1...daemon-v0.1.2) (2026-10-06)


### Bug Fixes

* **daemon:** E1 retro gate — lossy config repair turns the timer dry, protected roots' ancestors never deleted ([#26](https://github.com/oleksandrdubyna88/wsl_care/issues/26)) ([a1cf8ae](https://github.com/oleksandrdubyna88/wsl_care/commit/a1cf8aefe3d92378644d5578d86841dde47491da))
* **daemon:** the release workflow fetches the conventions checker its scenario suite reads ([54ea332](https://github.com/oleksandrdubyna88/wsl_care/commit/54ea332e16f39a50210e26a9863997d624e51c28))

## [0.1.1](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.1.0...daemon-v0.1.1) (2026-10-06)


### Bug Fixes

* **daemon:** act unit's CollectMode belongs in [Unit] ([136b60d](https://github.com/oleksandrdubyna88/wsl_care/commit/136b60d30b87f5df75188e4c4c174c5525bb02da))

## 0.1.0 (2026-10-06)


### Features

* **daemon:** A1, A2 with its event, A3, A15, A16, the timer's action pass and logs / runs (E3.S3) ([220a3af](https://github.com/oleksandrdubyna88/wsl_care/commit/220a3afe8a35ebcf2725ea5127678616b3155465))
* **daemon:** Docker collectors, volume-seen.json, hygiene audit, preview --all --json and the live contract (E2.S2) ([b36b0ef](https://github.com/oleksandrdubyna88/wsl_care/commit/b36b0efbfcbf169b6b3398b0ae05954d061c43e5))
* **daemon:** E6.S0 + E6.S1 — the daemon read contract and detached runs ([#11](https://github.com/oleksandrdubyna88/wsl_care/issues/11)) ([7434800](https://github.com/oleksandrdubyna88/wsl_care/commit/7434800374163abf7ede12701525547acb2ae954))
* **daemon:** E7 daemon half — configuration trust, AI agents, A18, every number configurable ([#17](https://github.com/oleksandrdubyna88/wsl_care/issues/17)) ([4782ccf](https://github.com/oleksandrdubyna88/wsl_care/commit/4782ccf6cbac372e295125082d387b6d9b403be9))
* **daemon:** foundation seams, config layering with observe-only, config verbs (E1.S2) ([08fe810](https://github.com/oleksandrdubyna88/wsl_care/commit/08fe810bdb2d7294d4a1aa7a25b169adc20ad3de))
* **daemon:** memory, process, container and disk collectors and status --json (E2.S1) ([5d05e49](https://github.com/oleksandrdubyna88/wsl_care/commit/5d05e4947918218633ce9940fed30161a0903d82))
* **daemon:** skeleton, root build files and daemon CI (E1.S1) ([876da14](https://github.com/oleksandrdubyna88/wsl_care/commit/876da14512bfec7526fee4802842a614d9e9feef))
* **daemon:** status --json carries the threshold verdicts and the product version; golden contracts (E5.S0) ([89be8fa](https://github.com/oleksandrdubyna88/wsl_care/commit/89be8fa23f3507bb5849e89510f8c997b475f314))
* **daemon:** the action engine, the command policy and a root-only act (E3.S1) ([297c955](https://github.com/oleksandrdubyna88/wsl_care/commit/297c955a0d610553efa5227529c4fe222292830a))
* **daemon:** the full run, doctor and the container-start follower (E2.S3) ([0603b15](https://github.com/oleksandrdubyna88/wsl_care/commit/0603b15530f932032ba2a32babe484d222dee2f9))
* **daemon:** the irreversible deletions A4-A9, A11, A12, A14, A17 (E3.S2) ([1583b23](https://github.com/oleksandrdubyna88/wsl_care/commit/1583b23ac3a6edbb2c801c34f9d33b5762fbf615))
* **install:** install.sh with checksum and attestation, the three units, the empty machine layer (E4.S1) ([c72229c](https://github.com/oleksandrdubyna88/wsl_care/commit/c72229cf8fc6df7d1e2e598a5d412ee01010413b))


### Bug Fixes

* **daemon:** a full check's history line names itself by kind (§15o) ([#16](https://github.com/oleksandrdubyna88/wsl_care/issues/16)) ([b83622e](https://github.com/oleksandrdubyna88/wsl_care/commit/b83622e2e326f48350e4c60a52e2a4a0d23e5b5d))
* **daemon:** a reader never makes a run lose its history line; wall-clock runner tests run alone ([3a5febd](https://github.com/oleksandrdubyna88/wsl_care/commit/3a5febd5bfa0a59043734845e42cf035709f91fd))
* **daemon:** fail closed on unresolvable paths, revalidate the atomic write, keep stderr printable ([84b8e66](https://github.com/oleksandrdubyna88/wsl_care/commit/84b8e66d115be41ae5f8a3eab0772784d3cc7105))
* **daemon:** find powershell.exe on the mounted Windows system drive under systemd ([#10](https://github.com/oleksandrdubyna88/wsl_care/issues/10)) ([65f4360](https://github.com/oleksandrdubyna88/wsl_care/commit/65f4360475289ec4112de19ed9c628196a009299))
* **daemon:** list directories in ordinal order, and leave CI accounts out of the privacy scan ([be6b9d4](https://github.com/oleksandrdubyna88/wsl_care/commit/be6b9d4c5f232e87cb42ee5d4f55307761cea90b))
* **daemon:** start a tool by the full path found on PATH, never by the OS search ([01afc3c](https://github.com/oleksandrdubyna88/wsl_care/commit/01afc3cabd1051f4f65645948ef7792b8e9580c4))
* **daemon:** the E2 code-round findings [#0](https://github.com/oleksandrdubyna88/wsl_care/issues/0)-[#11](https://github.com/oleksandrdubyna88/wsl_care/issues/11) (gate session 714367be) ([e135f82](https://github.com/oleksandrdubyna88/wsl_care/commit/e135f823f9cbd4f4db8129444faddbbf87f238b3))
* **daemon:** the E3 gate code round (session a90e342d) — findings [#0](https://github.com/oleksandrdubyna88/wsl_care/issues/0)-[#4](https://github.com/oleksandrdubyna88/wsl_care/issues/4), [#6](https://github.com/oleksandrdubyna88/wsl_care/issues/6)-[#11](https://github.com/oleksandrdubyna88/wsl_care/issues/11) ([33fee61](https://github.com/oleksandrdubyna88/wsl_care/commit/33fee61a216272e122c73a1f6c50c88695256bb4))
* **daemon:** the E3 independent review — anonymous by label, failed deletions counted, safe re-checks ([7b371bb](https://github.com/oleksandrdubyna88/wsl_care/commit/7b371bb2247fa6ae76f560de0599c5eaac20b206))
* **daemon:** the timer says so with --timer; INVOCATION_ID is never read ([9dce137](https://github.com/oleksandrdubyna88/wsl_care/commit/9dce1379f274804fc7f5e425b71b62e6a0e0d455))
* **extension:** the E5 code round — a signing job of its own, the minimum daemon as an artefact, safe re-runs, and five client fixes ([b21aa77](https://github.com/oleksandrdubyna88/wsl_care/commit/b21aa77aa6df3eea984063221502744cc28eb611))
* **install:** pin the attestation to release.yml at the tag, verified by root from a bundle ([cb8324a](https://github.com/oleksandrdubyna88/wsl_care/commit/cb8324a076b08bfe5fa06f3812b7fa6a672f7e42))
* **install:** repeat the pinned tag and version in every re-run line, the skip line a loud last resort ([7ea976a](https://github.com/oleksandrdubyna88/wsl_care/commit/7ea976a96b6b849ed0ade9114404ecb75b4f70a7))
* **release:** native archive path, Windows packaging on every pull request, honest release-please config ([54739aa](https://github.com/oleksandrdubyna88/wsl_care/commit/54739aaa1aad020dbb8eb5724b6a77f268913f8b))
