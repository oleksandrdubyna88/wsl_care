# Changelog

## [0.3.0](https://github.com/oleksandrdubyna88/wsl_care/compare/extension-v0.2.0...extension-v0.3.0) (2026-10-09)


### Features

* **daemon:** A19 stops idle MCP servers - a button and the timer, by pid and start (E14 S2a) ([#55](https://github.com/oleksandrdubyna88/wsl_care/issues/55)) ([bd85fd3](https://github.com/oleksandrdubyna88/wsl_care/commit/bd85fd368f91d298f812704028b82231fad07e1c))
* **daemon:** E9 — the AI-session archive (E9.S0–E9.S5) ([#69](https://github.com/oleksandrdubyna88/wsl_care/issues/69)) ([eb498eb](https://github.com/oleksandrdubyna88/wsl_care/commit/eb498eb6571986ad42ba84edc068fb92ffffd5f9))
* **daemon:** memory and swap before the evening - a report (E14 S5) ([#63](https://github.com/oleksandrdubyna88/wsl_care/issues/63)) ([9544809](https://github.com/oleksandrdubyna88/wsl_care/commit/954480969a4840f08c7d70aa456e335dcd5d0459))
* **daemon:** the machine-busy signal - the `wsl-care` daemon's `busy` command and the pressure.cpu/io verdicts (E14 S6) ([#62](https://github.com/oleksandrdubyna88/wsl_care/issues/62)) ([513238a](https://github.com/oleksandrdubyna88/wsl_care/commit/513238a6fecfd44981c73ae3792ef2705019183f))
* **extension:** the Windows Time guard — one SYSTEM scheduled task that restarts w32time and resyncs ([#56](https://github.com/oleksandrdubyna88/wsl_care/issues/56)) ([a7df216](https://github.com/oleksandrdubyna88/wsl_care/commit/a7df21696974e550985dd5a0eadaba36a4542c1f))
* **extension:** the Windows Time guard's fifth trigger - a start-type change (SCM 7040 for W32Time) ([#59](https://github.com/oleksandrdubyna88/wsl_care/issues/59)) ([98dc374](https://github.com/oleksandrdubyna88/wsl_care/commit/98dc374873dcf75735a928ad6d24fe8021ef8cbd))


### Bug Fixes

* the Windows Time guard — which clock is wrong, A16 never steps to a wrong host, Start Windows Time ([#53](https://github.com/oleksandrdubyna88/wsl_care/issues/53)) ([fc8efff](https://github.com/oleksandrdubyna88/wsl_care/commit/fc8efff814cbf48b7deccc8ff13756f2c30a6d42))

## [0.2.0](https://github.com/oleksandrdubyna88/wsl_care/compare/extension-v0.1.0...extension-v0.2.0) (2026-10-08)


### Features

* **extension:** A4's shown-list cap follows the daemon's published maxShownNames ([1afbb64](https://github.com/oleksandrdubyna88/wsl_care/commit/1afbb648547d18dcc74784209097c71cdc09bfc3))
* **extension:** every E6 number is an application-scoped setting with its range and default ([a15a7d1](https://github.com/oleksandrdubyna88/wsl_care/commit/a15a7d18b7af81fa988b3c25618347d131a513ba))
* **extension:** how many past results Last cleanup lists is a setting - wslCare.cleanup.resultsKept ([a7ba6bf](https://github.com/oleksandrdubyna88/wsl_care/commit/a7ba6bf82f5a62d97e924c13390b924b14baf251))
* **extension:** mirrored daemon values read from the daemon; the follower's internals and the list ceiling as settings ([ad35ded](https://github.com/oleksandrdubyna88/wsl_care/commit/ad35ded9eba885986f7ed8eab2821f2457cb231b))
* **extension:** take in E7's daemon half ([#17](https://github.com/oleksandrdubyna88/wsl_care/issues/17)) - status.limits from its contract, exit 81, A18 button-only ([832c11f](https://github.com/oleksandrdubyna88/wsl_care/commit/832c11f1fa217d438655b5723214308a4a457306))
* **extension:** the cleanup buttons in the panel, their state from the daemon (E6.S3) ([b6120c4](https://github.com/oleksandrdubyna88/wsl_care/commit/b6120c4386729f37bdce3d018378bca4147ec59d))
* **extension:** the cleanup host transaction - modals, expiry, one confirm, retry (E6.S3) ([7ac163d](https://github.com/oleksandrdubyna88/wsl_care/commit/7ac163dd45144d20e6a4add7e0592da008ae439f))
* **extension:** the E6 strings follow the rename to AI OS Care ([29d16be](https://github.com/oleksandrdubyna88/wsl_care/commit/29d16bed86de03da5aec9d3939a23a5f23b434bd))
* **extension:** the Logs page's host side and its WebviewPanel - persisted period, closed reads, Logs buttons (E6.S4) ([a8a6e38](https://github.com/oleksandrdubyna88/wsl_care/commit/a8a6e38863e9d24c596a86f5a8f2f50637a8cf9b))
* **extension:** the Logs page's periods, its closed message set and the shared format module (E6.S4) ([6334aa5](https://github.com/oleksandrdubyna88/wsl_care/commit/6334aa5c3f155520af7eec8d05e7289d8928c5c0))
* **extension:** the Logs page's view and page script - every block read from the answers, nothing computed (E6.S4) ([43683b6](https://github.com/oleksandrdubyna88/wsl_care/commit/43683b65b72065db56c4e69343f7493f8f5d4cc3))
* **extension:** the logs read - logs --from --to --json, unprivileged, through the client and the strict fake (E6.S4) ([a753be8](https://github.com/oleksandrdubyna88/wsl_care/commit/a753be856c7d367923536854e6a6b70b48a0a2dc))
* **extension:** the root boundary - one module spells root argv, one controller holds it ([f0b40c8](https://github.com/oleksandrdubyna88/wsl_care/commit/f0b40c870311243d49d2eb5137c938b88e881b28))
* **extension:** the run reads, the cleanup journal and the durable poll (E6.S3) ([f9f4e5d](https://github.com/oleksandrdubyna88/wsl_care/commit/f9f4e5d12e04be7cd2a037c0544f7143d46a45da))
* **extension:** the runner seam takes stdin, and every daemon exit code has a name ([22f149b](https://github.com/oleksandrdubyna88/wsl_care/commit/22f149bda1955f1c2023a32a498c758eec7e8181))
* **release:** the actions minimum, and the first public extension kept root-free by its tags ([e6d2efa](https://github.com/oleksandrdubyna88/wsl_care/commit/e6d2efa54d0f6b31531ad1930c9720a515be3d14))


### Bug Fixes

* **daemon:** the PR [#11](https://github.com/oleksandrdubyna88/wsl_care/issues/11) retro round — accepted runs wait, traces kept, derived success exits ([#43](https://github.com/oleksandrdubyna88/wsl_care/issues/43)) ([7e65e16](https://github.com/oleksandrdubyna88/wsl_care/commit/7e65e16cf16823c1af29dcb5251c6822f9263723))
* **daemon:** the PR [#16](https://github.com/oleksandrdubyna88/wsl_care/issues/16) retro round — an unknown kind is read as unknown, never guessed ([#44](https://github.com/oleksandrdubyna88/wsl_care/issues/44)) ([05ec294](https://github.com/oleksandrdubyna88/wsl_care/commit/05ec2944796e891abfccc29815d7633787f6010e))
* **extension:** a run read asked after a distro switch never joins the previous distribution's ([9cadb1f](https://github.com/oleksandrdubyna88/wsl_care/commit/9cadb1fc2335c3d5d646eb973f7eac8a4ae88b98))
* **extension:** after rebasing onto main (§15o, [#16](https://github.com/oleksandrdubyna88/wsl_care/issues/16)) - the fallback prefixes are the contract's; kind wins over a prefix ([de10ef6](https://github.com/oleksandrdubyna88/wsl_care/commit/de10ef659647f50c509d6c9f38c21a949c46abd3))
* **extension:** E6.S2 review round - certain exits, who follows, one confirm per preview, no WSLENV for root ([cda1439](https://github.com/oleksandrdubyna88/wsl_care/commit/cda1439ebb6f6409c72566a961950c50f83ef4e9))
* **extension:** E6.S3 review - notifications cannot carry a command link; one row per Clean; the modal is the confirm's ids ([1443043](https://github.com/oleksandrdubyna88/wsl_care/commit/1443043f1fa9267cf592199673607cdee4cb7952))
* **extension:** E6.S3 review - the cleanup journal shared by windows: merge, tombstones, refusal when full, real instants ([428c1d4](https://github.com/oleksandrdubyna88/wsl_care/commit/428c1d4df8a9487907b5917f9f0ad161b50beded))
* **extension:** E6.S3 review - the durable poll ends nothing on no evidence, matches the daemon's real lines, never sticks ([38aaf9e](https://github.com/oleksandrdubyna88/wsl_care/commit/38aaf9e22cbff44c4f575f283be36bf46dbdac7a))
* **extension:** E6.S4 review - run reads name their CLI verb in a table; an act line is never a full check's (K2, C7) ([43abc35](https://github.com/oleksandrdubyna88/wsl_care/commit/43abc35447ded991688d1d199c5ab57953279cfd))
* **extension:** E6.S4 review - the Logs page shows no unreadable history as fact, keeps its window, inputs and one panel ([ca2e560](https://github.com/oleksandrdubyna88/wsl_care/commit/ca2e56040589b412299c2fdb8b5ca85f1eca1e0e))
* **extension:** every host ceiling above the daemon's worst case for its call (plan §15q N-1-N-3) ([c73d096](https://github.com/oleksandrdubyna88/wsl_care/commit/c73d0968aee7fbafcd4dfb0b129729590eccf69b))
* **extension:** Install daemon installs daemon 0.1.2, apart from the 0.1.0 render minimum ([26b360b](https://github.com/oleksandrdubyna88/wsl_care/commit/26b360b0fd3a2ec46fdefcb6b3d5ffff8bcad8d6))
* **extension:** Install daemon keeps installing 0.1.2 beside the E6 actions minimum ([ea4b323](https://github.com/oleksandrdubyna88/wsl_care/commit/ea4b3234650fabc85065296ae66b1d6ca9d9639a))
* **extension:** match a full check's history line by kind first (plan §15o, the extension half) ([6484136](https://github.com/oleksandrdubyna88/wsl_care/commit/6484136c910dfb0021776b3a8c73c0199bb0852d))
* **extension:** mirror daemon [#38](https://github.com/oleksandrdubyna88/wsl_care/issues/38)'s MCP limits; status's worst case waits the MCP CPU window and log listing ([cb9fb45](https://github.com/oleksandrdubyna88/wsl_care/commit/cb9fb459b433a5480695d809114d6d0ff12c9100))
* **extension:** the .vsix leak check leaves a CI service account out instead of reading it as a person ([#51](https://github.com/oleksandrdubyna88/wsl_care/issues/51)) ([36a34fc](https://github.com/oleksandrdubyna88/wsl_care/commit/36a34fc3fdc9cd36e213c65bc466ae7a4306e0c7))
* **extension:** the cleanup follower's status is stamped with the current target's round ([8a4596e](https://github.com/oleksandrdubyna88/wsl_care/commit/8a4596e5ae4a85e8f8eea855e1ee05382678ec22))
* **release:** the actions minimum may never be below the render minimum (coai E6.S2 code round [#0](https://github.com/oleksandrdubyna88/wsl_care/issues/0)) ([735dec6](https://github.com/oleksandrdubyna88/wsl_care/commit/735dec6dd1dc865f25ddf3dc099232079560eb9e))

## 0.1.0 (2026-10-06)


### Features

* **extension:** Install daemon, the universal .vsix with its leak checks, Marketplace metadata (E5.S3) ([8b972f3](https://github.com/oleksandrdubyna88/wsl_care/commit/8b972f3bbf1f6efbe2d275ce956bf79780b9d43c))
* **extension:** measure wsl.exe, then the extension skeleton, its client, a strict fake and CI (E5.S1) ([fa3ffb4](https://github.com/oleksandrdubyna88/wsl_care/commit/fa3ffb472586bbd958aa5bd4ec1d0d48cbe480b9))
* **extension:** rename to AI OS Care (id ai-os-care) ([c61ec98](https://github.com/oleksandrdubyna88/wsl_care/commit/c61ec988b75aedd7ab5a18b19346a2eeb144f35c))
* **extension:** status bar, a read-only panel from one field map, focused-window polling, page harness and test-electron (E5.S2) ([62ae508](https://github.com/oleksandrdubyna88/wsl_care/commit/62ae5089854499528dd7ade906a7cc514c52022e))


### Bug Fixes

* **extension:** a wslCare.distro switch never shows one distribution's answers under another (retro of [#9](https://github.com/oleksandrdubyna88/wsl_care/issues/9)) ([#23](https://github.com/oleksandrdubyna88/wsl_care/issues/23)) ([7696d80](https://github.com/oleksandrdubyna88/wsl_care/commit/7696d80c7c6a4834a259cdef71e861612b120ec0))
* **extension:** the E5 code round — a signing job of its own, the minimum daemon as an artefact, safe re-runs, and five client fixes ([b21aa77](https://github.com/oleksandrdubyna88/wsl_care/commit/b21aa77aa6df3eea984063221502744cc28eb611))
