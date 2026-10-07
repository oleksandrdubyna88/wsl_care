# module_daemon — the `wsl-care` daemon / CLI (`src_daemon/`)

> The module overview of the daemon. The deep detail stays where each epic wrote it, in
> [architecture.md](architecture.md); the tests are in [module_tests.md](module_tests.md). This file is the map between
> them, in the same shape as [module_vs_code.md](module_vs_code.md). Added 2026-10-06 by the retro review of PR #7 (the
> review gate found no module document for the daemon, `common.knowledge-base`); the same finding on PR #5 was first
> rejected as a layout question and is answered by this file too.

## Purpose

Keep one developer workstation from degrading over the working day. A C# Native-AOT binary (`wsl-care`, `linux-x64`,
`linux-arm64`, `win-x64`) **measures** the machine — memory, processes, Docker, disk, health, AI-agent folders — and,
run by the root systemd timer inside WSL, **cleans** behind one switch per action: every cleanup has a preview, a
measured result, a run record and an `auto` switch; the never-lists are tested, not trusted. The VS Code extension is a
view over its CLI contract.

## Diagram

```mermaid
flowchart TB
    subgraph CLI["WslCare.Cli (the binary)"]
        program["Program.Main<br/>host → config → logger → verb"]
        verbs["Commands/<br/>status · preview · collect · act · doctor<br/>events follow · logs · runs · config · agents"]
        logging["Logging/<br/>AnsiConsoleSink (stderr) · DailyRunFileSink · LogRetention"]
    end
    subgraph Core["WslCare.Core (no packages)"]
        config["Config/<br/>default < machine < user · observe-only · trust"]
        collectors["Collectors/ · Docker/ · Health/ · Folders/ · Agents/<br/>read-only, every figure a Reading&lt;T&gt;"]
        collect["Collect/ · Records/<br/>full run · run detail → history line · reconcile · retention"]
        engine["Actions/Engine<br/>ActionEngine · lock · running.json · dry-run week · gates"]
        actions["Actions/*<br/>A1–A18 (ICleanupAction)"]
        policy["Processes/Policy<br/>CommandPolicy: declared templates only, never-list"]
        runner["Processes<br/>ProcessCommandRunner: argv, ceiling, tree kill"]
        files["Files/<br/>IFileSystem → DeletionPolicy → disk"]
        history["History/<br/>logs · runs"]
    end
    tools["docker · systemctl · journalctl · sysctl · runuser<br/>npm · pnpm · uv · pip · snap · fstrim · powershell.exe"]
    state[("/var/lib/wsl-care · /run/wsl-care.lock<br/>/var/log/wsl-care · /etc/wsl-care/config.json")]

    program --> verbs
    program --> logging
    verbs --> config
    verbs --> collectors
    verbs --> collect
    verbs --> engine
    verbs --> history
    collect --> collectors
    collect --> engine
    engine --> actions
    actions --> runner
    collectors --> runner
    runner --> policy
    policy --> tools
    actions --> files
    collect --> files
    files --> state
    logging --> files
```

## Core entities

| Entity | Where | What it is |
|---|---|---|
| `EffectiveConfig` / `ConfigLoadResult` | `Core/Config` | the three layers merged with each value's layer; `ObserveOnly` when a layer is invalid; `UserLayerSkipped` when root has no single target user |
| `Reading<T>` | `Core/Collectors` | a figure or the reason it is unavailable — never a silent zero |
| `RunRecord` / run detail | `Core/Records`, `Core/Collect` | one `history.jsonl` line per run, written AFTER its `runs/{day}/{runId}.json` detail |
| `ActionId`, `ICleanupAction`, `ActionPreview`, `ActionRun` | `Core/Actions` | an action's id (= its `auto.*` key), its live preview and its measured result |
| `CommandTemplate`, `CommandPolicy` | `Core/Processes/Policy` | the only argv shapes that may run; everything else is refused |
| `DeletionPolicy`, `ProtectedRoots` | `Core/Files/Deletion` | the one judge of every delete and move: agent folders, `~/git`, Claude's temp folder, memory, and their ancestors |
| `RunningFile`, `FirstTimerRun` | `Core/Actions/Engine` | `running.json` (pid + start + heartbeat) and the start of the timer's 7-day dry run |

## Entry points

The CLI verbs, as `CommandLine.Commands` spells them — the derived verb register in [module_tests.md](module_tests.md)
§ *The derived verb register* fails on a verb missing there. The systemd units (`src_daemon/systemd/`) start
`collect --timer` (the timer), `events follow` (the follower) and the `wsl-care-act@` template (detached runs).

## External dependencies

Read: `/proc`, cgroup v2, `/etc/passwd`, `/etc/wsl.conf`, `docker` (read verbs, each with a ceiling), `systemctl show`,
`journalctl`, `timedatectl`, `powershell.exe` (the Windows clock, one literal argv). Act: `docker` prunes and removals,
`sysctl -w` (two literal keys), `runuser -u <user> --` for user-scoped tools, `npm` / `pnpm` / `uv` / `pip` / `dotnet
nuget`, `snap`, `fstrim`, `chronyc` / `hwclock`, pidfd signals (A11, A18). Packages: Serilog only (CLI); `WslCare.Core`
has none.

## Where the detail is

| Area | architecture.md section |
|---|---|
| seams: config layers, `ICommandRunner`, `IFileSystem` + `DeletionPolicy`, `IHostPaths`, logging | *The seams (E1.S2)*, *Fail-closed resolution and the atomic write*, *The seams inside the binary* |
| collectors and `status` | *The collectors and `status` (E2.S1)* |
| Docker and `preview` | *The Docker collectors and `preview` (E2.S2)* |
| the full run, `doctor`, `events follow` | *The full run, `doctor` and the events follower (E2.S3)* |
| the engine, the command policy, `act` | *The action engine, the command policy and `act` (E3.S1)* |
| the irreversible deletions | *The irreversible deletions (E3.S2)* |
| memory / build-server / trim / clock actions, the timer pass, `logs` / `runs` | *The memory, build-server, trim and clock actions, the timer pass, `logs` / `runs` (E3.S3)*, *E3 review fixes (2026-10-03)* |
| installer and units; release | *The installer and the units (E4.S1)*, *The release pipeline (E4.S2)* |
| the read contract, detached runs | *The verdicts in `status`, `productVersion` and the golden contracts (E5.S0)*, *The daemon read contract (E6.S0)*, *Detached runs, the request, the stop (E6.S1)* |
| configuration trust, AI agents, A18, numbers | *The configuration trust (E7.S0, …)*, *The AI agents: catalogue, discovery, the walk (E7.S1, …)*, *A18 — orphaned AI-agent processes (E7.S2b, …)*, *Numbers are configuration (…)* |
| tests and the harness | [module_tests.md](module_tests.md), architecture.md *The scenario harness (E1.S3)* |
