# Changelog

## [0.6.0](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.5.0...daemon-v0.6.0) (2026-10-17)


### Features

* **daemon:** A23 windowsTemp — the Windows %TEMP% cleanup, the engine's first Windows action (WR-T1) ([#155](https://github.com/oleksandrdubyna88/wsl_care/issues/155)) ([c74af46](https://github.com/oleksandrdubyna88/wsl_care/commit/c74af4641df177d0f3595af8abf02cbd0b4d0932))
* **daemon:** Windows memory pressure — host.commit, host.compression, host.vmmem, one rule on both roads (E15.S5) ([#150](https://github.com/oleksandrdubyna88/wsl_care/issues/150)) ([6d68f00](https://github.com/oleksandrdubyna88/wsl_care/commit/6d68f00990f33a7e4120b9317429ec7b2d2e7a69))
* **health:** the `wsl-care` daemon's disk script runs under the verified MSI PowerShell 7, else 5.1 (E15.S1b) ([#151](https://github.com/oleksandrdubyna88/wsl_care/issues/151)) ([a432e97](https://github.com/oleksandrdubyna88/wsl_care/commit/a432e9708aedf07168a63f80cab01da8c5d5b6e4))


### Bug Fixes

* **extension:** the vsix e-mail scan does not flag a systemd unit name (getty@tty1.service) ([#164](https://github.com/oleksandrdubyna88/wsl_care/issues/164)) ([11553fe](https://github.com/oleksandrdubyna88/wsl_care/commit/11553feb79d1d5d227a67d59b3de09b6cb7c3fbe))

## [0.5.0](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.4.0...daemon-v0.5.0) (2026-10-15)


### Features

* **daemon:** A13 runs despite the load after archive.maxDeferHours ([#134](https://github.com/oleksandrdubyna88/wsl_care/issues/134)) ([ac70fa6](https://github.com/oleksandrdubyna88/wsl_care/commit/ac70fa6361837249150c1371e4ad2bb844e3c23e))
* **daemon:** tasks render, the windowsTasks keys and DryRunWindow.Peek (WR-D1) ([#124](https://github.com/oleksandrdubyna88/wsl_care/issues/124)) ([0f0b921](https://github.com/oleksandrdubyna88/wsl_care/commit/0f0b9219f64599ed97bd498537059001c7e7b34d))
* **daemon:** tasks run, the Job Object supervisor and exit 84 TaskEnded (WR-D3) ([#133](https://github.com/oleksandrdubyna88/wsl_care/issues/133)) ([281dd59](https://github.com/oleksandrdubyna88/wsl_care/commit/281dd59b816a64027e9e097d79ab00eea4aca31c))
* **daemon:** tasks stage and tasks stage --remove, the staged copy and its sweep (WR-D2) ([#137](https://github.com/oleksandrdubyna88/wsl_care/issues/137)) ([fce407f](https://github.com/oleksandrdubyna88/wsl_care/commit/fce407f80af04e44145aee0bb77932ade27ea746))
* **daemon:** the archive body of tasks run archive — dry previews, real runs, no run lock (WR-D5) ([#147](https://github.com/oleksandrdubyna88/wsl_care/issues/147)) ([3386660](https://github.com/oleksandrdubyna88/wsl_care/commit/3386660cec397c36c1c1f071196eb62688162bf6))
* **daemon:** the failed-units verdict ignores a configured list, default getty@tty1.service ([#128](https://github.com/oleksandrdubyna88/wsl_care/issues/128)) ([6ec9623](https://github.com/oleksandrdubyna88/wsl_care/commit/6ec96235e3cb418e0a14e6caa3f4a496ddbfa8d0))
* **daemon:** the virtual disks and the hosting volume — gap, growth, free space (E15.S2) ([#111](https://github.com/oleksandrdubyna88/wsl_care/issues/111)) ([dd543b7](https://github.com/oleksandrdubyna88/wsl_care/commit/dd543b75b3c9881f57700540c52afe33bc6010d7))
* **daemon:** the Windows disk script, a second fixed interop script read by the full run (E15.S1) ([#99](https://github.com/oleksandrdubyna88/wsl_care/issues/99)) ([1087861](https://github.com/oleksandrdubyna88/wsl_care/commit/1087861f3844a782ff17e9e992a653845cf070c8))
* **daemon:** the Windows watch, the ledger lock and the retention sweep (WR-D4) ([#144](https://github.com/oleksandrdubyna88/wsl_care/issues/144)) ([b741a93](https://github.com/oleksandrdubyna88/wsl_care/commit/b741a9342ff42724016a68de067a1d30f9d94fd4))
* **extension:** E10.S2b — Restore from the Archive page through the root road, over the `wsl-care` daemon's `act A20` ([#105](https://github.com/oleksandrdubyna88/wsl_care/issues/105)) ([28343e0](https://github.com/oleksandrdubyna88/wsl_care/commit/28343e07298267b0ddc0c60361aa6e74fc560575))
* **extension:** E10.S2c — the Logs page names the archive runs, tested against run goldens the daemon's engine writes ([#114](https://github.com/oleksandrdubyna88/wsl_care/issues/114)) ([befb57a](https://github.com/oleksandrdubyna88/wsl_care/commit/befb57a07ece7ed9c068a821e7fe1a35eae784a1))
* **extension:** Stop client-gone relays, the panel's A21 button (E14 S7b.2b) ([#100](https://github.com/oleksandrdubyna88/wsl_care/issues/100)) ([8ccece6](https://github.com/oleksandrdubyna88/wsl_care/commit/8ccece645dd00b72b0b28226232884222aa4a560))
* **install:** record the distro name through a validated --distro-name argument (E15.S2b) ([#126](https://github.com/oleksandrdubyna88/wsl_care/issues/126)) ([6314311](https://github.com/oleksandrdubyna88/wsl_care/commit/631431155e2952039dcc532034bd04f7c4c0b133))
* **tests:** local test runs take at most 8 workers, at lowered CPU priority ([#102](https://github.com/oleksandrdubyna88/wsl_care/issues/102)) ([baab38e](https://github.com/oleksandrdubyna88/wsl_care/commit/baab38e36ad71c9566e1cde6a79d7eaa5a36f9c6))


### Bug Fixes

* **daemon:** A20 believes only the entries it asked for ([#118](https://github.com/oleksandrdubyna88/wsl_care/issues/118)) ([b76e1b2](https://github.com/oleksandrdubyna88/wsl_care/commit/b76e1b26e337c77dec5d1d09f12957766e3bf260))
* **daemon:** the disk gap advice never recommends a sparse VHD ([#115](https://github.com/oleksandrdubyna88/wsl_care/issues/115)) ([c0ef932](https://github.com/oleksandrdubyna88/wsl_care/commit/c0ef9321f7192aab4f8c84f5b939e53c8ed295e8))
* **health:** the clock verdicts say what was measured — journal.history from the reading, clock.fight's three writers, clock.jumps' stepper floor ([#136](https://github.com/oleksandrdubyna88/wsl_care/issues/136)) ([0d8a6e5](https://github.com/oleksandrdubyna88/wsl_care/commit/0d8a6e54f1eaa695adfe4af1023d1879b7e6dd31))
* **health:** the wsl-care daemon's interop scripts pin the PowerShell module path first ([#112](https://github.com/oleksandrdubyna88/wsl_care/issues/112)) ([b7c78d2](https://github.com/oleksandrdubyna88/wsl_care/commit/b7c78d20edf515c91b95109d360fadc98ecae13c))
* **privacy:** take the owner's login name out of tracked text, and find the daemon's user sentences in CI too ([#109](https://github.com/oleksandrdubyna88/wsl_care/issues/109)) ([fa7c9a3](https://github.com/oleksandrdubyna88/wsl_care/commit/fa7c9a33ed6ded406bf252c9d1835991037759ad))
* **release:** fetch-windows-daemon retries the download, bounded, timed out per attempt and logged ([#125](https://github.com/oleksandrdubyna88/wsl_care/issues/125)) ([3b9b4ff](https://github.com/oleksandrdubyna88/wsl_care/commit/3b9b4ff7eb4e840b51a1574c1d745c675deea2ea))
* **tests:** TempRoot sweeps the wsl-care-test-* roots earlier test processes left in %TEMP% ([#122](https://github.com/oleksandrdubyna88/wsl_care/issues/122)) ([dadb8dd](https://github.com/oleksandrdubyna88/wsl_care/commit/dadb8ddcc79983c4de17fa8871578c2ee9f8bfee))

## [0.4.0](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.3.0...daemon-v0.4.0) (2026-10-10)


### Features

* **daemon:** a timer run settles first — after a boot, and while the machine is busy ([#82](https://github.com/oleksandrdubyna88/wsl_care/issues/82)) ([912eed2](https://github.com/oleksandrdubyna88/wsl_care/commit/912eed21810f0a54b207d8e08d130757ca3b21ea))
* **daemon:** A11 on by default, with language-servers in its default families ([#75](https://github.com/oleksandrdubyna88/wsl_care/issues/75)) ([f7dbb46](https://github.com/oleksandrdubyna88/wsl_care/commit/f7dbb460ce1a8ebc5f7c9b29cdfa4bd3adc152bb))
* **daemon:** A21 ends client-gone interop relays with SIGTERM only (E14 S7b.2) ([#91](https://github.com/oleksandrdubyna88/wsl_care/issues/91)) ([9d23dc1](https://github.com/oleksandrdubyna88/wsl_care/commit/9d23dc16d82e77ae259f2604c686c2c26f8d7bbf))
* **daemon:** E10.S0 — A20's entries on stdin, the restore ceiling, none lost to the window ([#73](https://github.com/oleksandrdubyna88/wsl_care/issues/73)) ([7b1dad7](https://github.com/oleksandrdubyna88/wsl_care/commit/7b1dad73c9337b1bb7b9e5cf07c06d66ed297f10))
* **daemon:** E9 — the AI-session archive (E9.S0–E9.S5) ([#69](https://github.com/oleksandrdubyna88/wsl_care/issues/69)) ([eb498eb](https://github.com/oleksandrdubyna88/wsl_care/commit/eb498eb6571986ad42ba84edc068fb92ffffd5f9))
* **daemon:** the Windows side's MCP CPU ledger (E14 S7b.1), and the S7b plan ([#84](https://github.com/oleksandrdubyna88/wsl_care/issues/84)) ([c1e9249](https://github.com/oleksandrdubyna88/wsl_care/commit/c1e92497702eee90a5ae0ab83c69aa5381be8056))
* **extension:** E10.S1a — the archive folder, one user-layer writer, the panel's archive section ([#88](https://github.com/oleksandrdubyna88/wsl_care/issues/88)) ([6c5f148](https://github.com/oleksandrdubyna88/wsl_care/commit/6c5f148f3df096059fc756cb47901c441773ca62))
* **extension:** E10.S1b — Archive now, the daemon's A13 through the cleanup controller from a panel button ([#92](https://github.com/oleksandrdubyna88/wsl_care/issues/92)) ([7198f25](https://github.com/oleksandrdubyna88/wsl_care/commit/7198f252e556db9535c0b132096a2190afc12ba1))


### Bug Fixes

* **ci:** the Marketplace wait outlasts a 20-minute propagation and logs every attempt ([#70](https://github.com/oleksandrdubyna88/wsl_care/issues/70)) ([c002c00](https://github.com/oleksandrdubyna88/wsl_care/commit/c002c008c01b6fb5d787564c2b49f3977b707fea))
* **daemon:** a base on a network share — the mapped drive is its share, SMB's missing folder flush, the lease says why ([#80](https://github.com/oleksandrdubyna88/wsl_care/issues/80)) ([7efa0fb](https://github.com/oleksandrdubyna88/wsl_care/commit/7efa0fb83287a54a8378456dd129ca61f1b2ae55))
* **daemon:** the Windows idle rule — while Claude Code runs, a session idle for archive.windowsIdleDays may move ([#78](https://github.com/oleksandrdubyna88/wsl_care/issues/78)) ([463fc24](https://github.com/oleksandrdubyna88/wsl_care/commit/463fc2479b7299c9a3a825e0ce13d289f8450f5e))
* **daemon:** two fail-closed guards — a session on a live Claude command line stays, an Offline Files share is not trusted ([#85](https://github.com/oleksandrdubyna88/wsl_care/issues/85)) ([4499849](https://github.com/oleksandrdubyna88/wsl_care/commit/44998494325738727a5c0ea9b4a2bb13899cc00d))

## [0.3.0](https://github.com/oleksandrdubyna88/wsl_care/compare/daemon-v0.2.0...daemon-v0.3.0) (2026-10-09)


### Features

* **daemon:** A19 stops idle MCP servers - a button and the timer, by pid and start (E14 S2a) ([#55](https://github.com/oleksandrdubyna88/wsl_care/issues/55)) ([bd85fd3](https://github.com/oleksandrdubyna88/wsl_care/commit/bd85fd368f91d298f812704028b82231fad07e1c))
* **daemon:** A3's timer waits until every build server is idle; language servers get their own family (E14 S3) ([#60](https://github.com/oleksandrdubyna88/wsl_care/issues/60)) ([94bbfc5](https://github.com/oleksandrdubyna88/wsl_care/commit/94bbfc50e156acc2573e6328f7479d0d3347c338))
* **daemon:** memory and swap before the evening - a report (E14 S5) ([#63](https://github.com/oleksandrdubyna88/wsl_care/issues/63)) ([9544809](https://github.com/oleksandrdubyna88/wsl_care/commit/954480969a4840f08c7d70aa456e335dcd5d0459))
* **daemon:** playwright-mcp, an interpreter-run MCP server, in the catalogue (E14 S2d) ([#61](https://github.com/oleksandrdubyna88/wsl_care/issues/61)) ([0d98c4b](https://github.com/oleksandrdubyna88/wsl_care/commit/0d98c4ba5e9451355e1c10c43eadc185de1f640d))
* **daemon:** the machine-busy signal - wsl-care busy and the pressure.cpu/io verdicts (E14 S6) ([#62](https://github.com/oleksandrdubyna88/wsl_care/issues/62)) ([513238a](https://github.com/oleksandrdubyna88/wsl_care/commit/513238a6fecfd44981c73ae3792ef2705019183f))
* **daemon:** the watch timer and A19's busy half (E14 S2b) ([#65](https://github.com/oleksandrdubyna88/wsl_care/issues/65)) ([b313ce8](https://github.com/oleksandrdubyna88/wsl_care/commit/b313ce85d96ee6e8c4d33e383a224bf2508db196))
* **daemon:** the Windows side's MCP servers, read-only, and the vmmem advice (E14 S7a) ([#64](https://github.com/oleksandrdubyna88/wsl_care/issues/64)) ([583e060](https://github.com/oleksandrdubyna88/wsl_care/commit/583e0603090400893ca65d82a23255daede7c73f))
* **daemon:** users add their own MCP programs to the watched list (E14 S2c) ([#58](https://github.com/oleksandrdubyna88/wsl_care/issues/58)) ([773522b](https://github.com/oleksandrdubyna88/wsl_care/commit/773522b230d9c0338f414aeb986d07d324fcc296))


### Bug Fixes

* **daemon:** MCP server CPU over the interval since the previous sample (E14 S1) + the twenty-sessions plan ([#48](https://github.com/oleksandrdubyna88/wsl_care/issues/48)) ([47e1d98](https://github.com/oleksandrdubyna88/wsl_care/commit/47e1d984cf13568ecb3cd67ea1872493b5546891))
* **extension:** the .vsix leak check leaves a CI service account out instead of reading it as a person ([#51](https://github.com/oleksandrdubyna88/wsl_care/issues/51)) ([36a34fc](https://github.com/oleksandrdubyna88/wsl_care/commit/36a34fc3fdc9cd36e213c65bc466ae7a4306e0c7))
* the Windows Time guard — which clock is wrong, A16 never steps to a wrong host, Start Windows Time ([#53](https://github.com/oleksandrdubyna88/wsl_care/issues/53)) ([fc8efff](https://github.com/oleksandrdubyna88/wsl_care/commit/fc8efff814cbf48b7deccc8ff13756f2c30a6d42))

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
