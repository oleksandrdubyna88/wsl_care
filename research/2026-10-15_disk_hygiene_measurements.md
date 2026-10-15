# Disk hygiene (Windows plan) — measurements, 2026-10-15

> Status: **record, one section per measurement.** One file for every measurement of
> [PLAN_disk_hygiene_windows.md](../todo/PLAN_disk_hygiene_windows.md) and its epics; each story adds its own section.
> Raw outputs stay outside the repository (they hold project, container and volume names of the machine); only what a
> fixture or a figure needs is kept here, redacted by `research/diagnostics/docker_fixture_redact.mjs`.

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
