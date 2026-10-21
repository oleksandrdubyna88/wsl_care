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
