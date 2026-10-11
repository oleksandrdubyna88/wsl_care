# Architecture — wsl_care: fixture privacy, the projects and CI

> Part of [architecture.md](architecture.md), which stays the entry point (system overview, module map, cross-cutting
> concerns). These sections were moved here unchanged on 2026-10-10 to keep `architecture.md` under the 256 KiB the
> conventions resolver (`.agents/conventions/tools/rules.mjs`) accepts for a required source; `.agents/PROJECT.md`
> requires each of the split files.

## Fixture privacy (E5 code round, 2026-10-04)

The repository is public, and the captured fixtures and the goldens built from them carried the owner's Linux and
Windows user names, home and profile paths, project and repository names, installed extension ids and versions, and a
Claude scratchpad path. Since the code round:

- **ONE identity list** — `WslCare.TestSupport/FixtureIdentity`: eight named rules (`tempFolder`, `linuxHome`,
  `windowsProfile`, `passwdAccount`, `accountToken`, `projectDirectory`, `extensionId`, `email`), each a SHAPE; the
  mappings (projects → `project-a`…, extension ids → `vendor.extension-a`…) are LEARNT from the corpus at run time, in
  ordinal order, so the file holds no original value and every file is rewritten the same way (a cwd in `links.txt` and
  an argv in `cmdline` still name the same project). Numbers, ids, sizes, times and structure are kept.
- **Applied twice, by the same code** — to the captured fixtures by `FixtureAnonymisationTests` (it fails while any text
  fixture would still change; `WSL_CARE_ANONYMISE_FIXTURES=1` rewrites), and as part 4 of the golden writer's list.
- **Detected apart** — `FixturePrivacyTests` (Scenarios, every OS) walks every `fixtures` / `golden` / `goldens` directory
  of the repository (build output, `node_modules`, the editor downloads and the shared-rules submodule excluded) and fails
  on a `/home/<name>` or a Windows profile path whose name is not `user`, an e-mail address (a systemd template instance
  is not one) and the user name of the machine RUNNING it (read from the environment and the home folder, like
  check-vsix, never stored); a finding names file, line and rule, never the value. Each rule has a planted instance, and a
  known file proves the walk still reaches the trees.
- **Widened to the whole repository** — `FixturePrivacyTests.No_tracked_text_file…` applies the same rules to EVERY tracked
  text file (`git ls-files`; a walk skipping build output, dependencies and the editor downloads when there is no `.git`),
  admitting besides `user` only the commented list of invented test accounts (`FixturePrivacy.SyntheticNames`: `me`,
  `ann`, `sam`, `alice`, …) and, for e-mail, `noreply@anthropic.com` and the RFC 2606 / 6761 example domains. The
  machine-name rule leaves out a service account (`FixturePrivacy.ServiceAccounts`: `runner` and `runneradmin` — the
  GitHub-hosted runners' accounts —, `root`, `vscode`,
  `codespace`, `user`; check-vsix's `SERVICE_ACCOUNTS` is the same list, and
  `FixturePrivacyTests.The_service_accounts_and_the_name_floor_are_the_vsix_checks_own` reads it out of `vsixCheck.ts` and
  holds the two EQUAL), a synthetic name and a name shorter than 3 characters (check-vsix's floor), and matches the rest
  as a whole word: `runner` is an ordinary word here, and CI run 37202261532 reported 541 findings per Linux leg. The research notes,
  the cleanup scripts and two test sources were anonymised by it (2026-10-04).
- **The daemon's own sentences** (2026-10-11) — a fifth FIXED rule finds a target user's name where the daemon writes it in
  prose: the suspect previews' basis "… relay(s) / process(es) / MCP server(s) of <name> kept", `act --preview`'s "target user:
  <name> (…)", config's "the user layer of <name>" / "run them as <name>, not as root", "… is not in <name>'s bin folders" —
  unless the name is `user` or a synthetic one. A copied A21 basis had carried the owner's login name into `POST_DEPLOY.md` and a
  research note, and only the machine-name rule saw it, on the owner's machine and never in CI (the runner's account is
  `runner`); `FixturePrivacyTests.No_tracked_text_file_carries_a_daemon_sentence_naming_a_real_user_whatever_the_machine_is_called`
  runs the fixed rules alone, as CI does. Older commits still hold the name; rewriting history is the owner's decision.
- **Not undone by this**: the data before the code round remains in git history (main and the pull-request branches);
  removing it needs a history rewrite and a force-push — the owner's decision.

## Projects and CI

```mermaid
flowchart LR
    subgraph root["repository root"]
        props["Directory.Build.props<br/>Directory.Packages.props<br/>Directory.Build.rsp<br/>global.json · nuget.config"]
        slnx["wsl_care.slnx"]
    end

    subgraph daemon["src_daemon/"]
        ver["version.txt"]
        dprops["Directory.Build.props<br/>(imports root, stamps Version)"]
        core["WslCare.Core<br/>class library · IsAotCompatible · 0 packages"]
        cli["WslCare.Cli<br/>exe wsl-care · PublishAot · Serilog"]
        support["WslCare.TestSupport<br/>doubles · ChildProcess launcher"]
        coreT["WslCare.Core.Tests<br/>xUnit v3 MTP exe"]
        cliT["WslCare.Cli.Tests<br/>xUnit v3 MTP exe"]
        fake["WslCare.FakeTool<br/>exe wsl-care-fake-tool"]
        scn["WslCare.Scenarios<br/>xUnit v3 MTP exe · fixtures/"]
        live["WslCare.LiveContract<br/>xUnit v3 MTP exe · real tools · not in CI"]
    end

    subgraph ci[".github/workflows"]
        ciD["ci-daemon.yml<br/>linux-x64 · linux-arm64 · win-x64"]
        ciE["ci-extension.yml<br/>windows-latest · ubuntu-24.04 (E5.S1)"]
        ciW["ci-workflows.yml<br/>actionlint + shellcheck · shellcheck install.sh"]
        ciF["family-checks.yml<br/>plans · pin · adapter · build flags"]
        prT["pr-title.yml"]
        rp["release-please.yml<br/>App token · proposes, cuts daemon-v* + draft"]
        rel["release.yml<br/>daemon-v* tag · guard · 3 RID legs · publish"]
        son["sonarcloud.yml<br/>skips loudly without SONAR_TOKEN"]
        cr["coderabbit-review.yml"]
    end

    subgraph scripts[".github/scripts (E4.S2)"]
        smoke["smoke-daemon.sh"]
        pack["package-daemon.sh"]
        guardS["release-guard.sh · verify-release-assets.sh"]
        contract["lib/daemon-assets.sh<br/>RIDs · version pattern · archive names"]
    end

    rules[".github/rulesets/*.json<br/>owner-applied (docs/repo-settings.md)"]

    vsc["src_vs_code/<br/>TypeScript · esbuild · node:test (E5.S1)"]
    golden["contracts/golden/head/<br/>(E5.S0)"]

    conv[".agents/conventions<br/>submodule, tools/*.mjs"]

    subgraph ship["what a release carries besides the binary (E4.S1)"]
        inst["install.sh<br/>POSIX sh"]
        unitsF["src_daemon/systemd/<br/>wsl-care.service · .timer · wsl-care-events.service"]
        machine["src_daemon/config/machine.json<br/>the empty machine layer"]
    end

    props --> dprops
    ver --> dprops
    dprops --> core
    dprops --> cli
    slnx --> core
    slnx --> cli
    slnx --> support
    slnx --> coreT
    slnx --> cliT
    slnx --> fake
    slnx --> scn
    slnx --> live
    cli -->|ProjectReference| core
    support -->|ProjectReference| core
    coreT -->|ProjectReference| core
    coreT -->|ProjectReference| support
    cliT -->|ProjectReference| cli
    cliT -->|ProjectReference| support
    scn -->|"ProjectReference: apphost + CommandLine.Commands"| cli
    scn -->|"ProjectReference: apphost + protocol"| fake
    scn -->|ProjectReference| support
    live -->|ProjectReference| core
    ciD -->|format · build · run 3 test exes| slnx
    ciD -->|"publish -r RID, smoke --help --version, config round trip, status --json"| cli
    ciF -->|node| conv
    inst -->|installs| unitsF
    inst -->|"installs when absent"| machine
    scn -->|"Install*Flows: runs under /bin/sh over a prefix"| inst
    ciW -->|shellcheck| inst
    ciD -->|"systemd-analyze verify"| unitsF
    ciD -->|"smoke the published binary"| smoke
    rel -->|"the same smoke"| smoke
    rel --> pack
    rel --> guardS
    pack --> contract
    guardS --> contract
    pack -->|"packs"| unitsF
    pack -->|"packs"| machine
    rp -->|"tag push starts"| rel
    scn -->|"PackageFlows · ReleaseScriptFlows: run under bash"| pack
    scn -->|"ReleaseWorkflowTests · ReleaseConfigTests: read"| rel
    scn -->|"ReleaseConfigTests: read"| rules
    ciW -->|shellcheck| scripts
    ciE -->|"tsc · eslint · npm test"| vsc
    vsc -->|"client tests replay"| golden
    scn -->|"GoldenContracts writes"| golden
    scn -->|"ReleaseConfigTests: the ci-extension contexts"| ciE
```
