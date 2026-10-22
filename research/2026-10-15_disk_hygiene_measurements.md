# Disk hygiene measurements — M1, M2, M6 (2026-10-15)

> Read-only census of the owner's 321 real worktrees, run by the E19 coordinator on 2026-10-15; nothing was changed or deleted. It
> fixes the defaults of E19.S3's `worktrees.*` keys ([PLAN_disk_hygiene_windows_E19.md](../todo/PLAN_disk_hygiene_windows_E19.md) §4,
> [PLAN_disk_hygiene_windows.md](../todo/PLAN_disk_hygiene_windows.md) §3.3, §7).

## M1 — the cost of the walk

| figure | value |
|---|---|
| folders | 321 |
| files | 1 301 801 |
| logical size | 66.35 GB |
| wall time | 83 s |
| entries the enumeration could not open | 0 |

The budgets `worktrees.scanMaxEntries` (5 000 000) and `worktrees.scanMaxSeconds` (900) are about four times these.

## M2 — the idle signal

| rule | idle over 3 days |
|---|---|
| newest write outside build output + the admin files (the plan's combined rule) | 121 |
| newest write of any descendant | 130 |
| disagreements | 9 |
| locked worktrees | 97 |

All nine disagreements are folders whose admin files are younger than their files: the combined rule is the conservative one, so it stays
the default.

## M6 — what a build writes, and what it does not

The file extensions found inside real `bin`/`obj` folders, by count, fix the default of `worktrees.buildArtifactPatterns`: `.dll .exe .pdb
.json .cache .so .dylib .a .lib .obj .wasm .gz .br .cs .props .targets .editorconfig .txt .map .js .mjs .up2date .sn .rsp .def
.sourcelink .resources .xml .nupkg .snupkg .baml .ilk .exp .res .tlog .lastbuildstate .stamp`, plus EXTENSIONLESS files (9 842 in real
bins: native runtime executables such as `createdump`). A `.cs` counts only under `obj` (generated sources), never under `bin`.

Not artifacts — they retain their candidate and are named, because test runs write them into `bin` after the build: `.out .current .stat
.procs .log .golden .jsonl .ndjson .dat .md .sql .diag` and anything else unlisted. The time-skew check catches most of them too.

**The glob was wrong.** The never-artifact pattern `*.sqlite*` matched `Microsoft.Data.Sqlite.dll` in 17 real bins. The never-artifacts
are therefore matched by EXTENSION or exact name: `.db .sqlite .sqlite3 .db-wal .db-shm .mdf .ldf .pfx .key .pem`, and `.env` or a name
starting `.env.`. Test: `A_dll_named_like_sqlite_is_an_artifact`.

## M-DH2 — the compose label on real stopped containers (E21.S1, read-only)

**Question.** Does the product's inspect template, plus a `compose` field, answer on a real stopped compose container —
and what does the field look like when the label is absent?

**Machine and Docker.** WSL distro `Ubuntu`, Docker Desktop engine 29.6.1, the `docker` CLI of the distro, as the unprivileged
user. Read-only commands only (`ps`, `inspect`, `system df -v`); nothing was started, stopped or removed.

**Method.**

1. Find the exited compose containers:
   `docker ps -a --filter label=com.docker.compose.project --filter status=exited --format '{{.ID}}'` — **12 ids** (the same
   machine had 20 compose-labelled containers in 9 projects; 12 exited — A5 would take all 12 after 7 days and `docker rm -v`
   their anonymous volumes).
2. Pick one whose image is public and whose inspect line is already in the fixture — an exited `mysql:8.0` container of a
   compose project, created 2026-09-22, finished 2026-09-30, one named volume; it is `container-17` of
   `src_daemon/tests/fixtures/docker/ubuntu-2026-10-02` (the fixture was captured 2026-10-02 and held this container, whose
   line was taken by the older template).
3. Run exactly the product's argv for it:
   `docker container inspect --format '<DockerCommands.InspectTemplate>' <full id>` where the template is the constant with
   the new field between `keep` and `mounts`:
   `"compose":{{json (index .Config.Labels "com.docker.compose.project")}},`.
4. Redact with `node research/diagnostics/docker_fixture_redact.mjs --recapture-inspect <capture-dir> <fixture-dir>`.

**Result.**

- The template answers on a real container: one JSON line, `"compose"` is the plain project name (a string), every other
  field equal to the line captured on 2026-10-02 (the script refuses the replacement when any field but `compose` differs —
  the proof that it is the same container captured again).
- An absent label is JSON `null` (the lines of the 28 other containers of the fixture are the older template's, with no
  `compose` key at all; both read as an empty project).
- The raw `docker ps` row of the container carries ten `com.docker.compose.*` labels, among them `project.config_files` and
  `project.working_dir`, which are host paths: the template reads the project name ONLY.

**Redaction.** `com.docker.compose.project` joins the label allow-list; its value is mapped to `project-<n>` (the first
project is `project-1`) in every file the script writes. `--recapture-inspect` keeps the fixture's own container, volume
and (so far absent) project names, maps the project, then searches the output for the original container name, project and
volume name and fails when one survived (0 found). The fixture's `system-df-v.out`, `ps-a.out` and `system-df.out` are NOT
recaptured: their labels for `container-17` still lack the compose label (older capture), and nothing reads them.

**Not measured here.** No Testcontainers or Ryuk container exists on this machine today (DH-6's concern).
