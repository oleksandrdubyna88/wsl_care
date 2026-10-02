# One-time WSL cleanup — 2026-10-02

> Status: **done 2026-10-02 (~09:35–10:00 CEST)**, Phase 0.2 of
> [PLAN_wsl_care_daemon.md](../todo/PLAN_wsl_care_daemon.md), extended by the user's explicit choices.
> Not done yet (user: "later"): `.wslconfig` changes, `wsl --shutdown`, vhdx compaction (Phase 0.1 / 0.3).

## Starting point

WSL had just restarted with Windows, so memory was fresh (MemAvailable 40.6 of 46 GB) — this cleanup is
about disk only. Docker Server 29.6.1 (≥ 23, so `docker volume prune` removes anonymous volumes only).

Finding that changed the plan: **none** of the 70 exited containers carried `org.testcontainers=true` —
all were created by hand or by scripts (`as-test-*`, `es-*-mysql`, `qln-*`, Aspire `pgadmin-*`, …). Plan
action A4 ("remove exited Testcontainers containers") would have found nothing; the leftovers come from
named `docker run` containers instead. The daemon's A4/A10 split needs revisiting with that in mind.

## What was removed

| Step | Command | Approval | Freed |
|---|---|---|---|
| Unattached anonymous volumes (387; `com.docker.volume.anonymous`, typically ~270 MB MySQL/Postgres init dirs from 2026-08-21 on) | `docker volume prune -f` | safe set + user ("delete everything unnamed") | **59.6 GB** |
| Build cache older than 7 days | `docker builder prune --filter until=168h` | safe set | 0.02 GB (all cache was < 7 days old) |
| Dangling images | `docker image prune -f` | safe set | ~0 |
| apt cache | `apt-get clean` | safe set | 0.27 GB |
| Disabled snap revisions (`core22` 2437, `snapd` 27710) | `snap remove --revision` | safe set | ~0.13 GB |
| 58 stopped/created containers idle ≥ 7 days, with their anonymous volumes | `docker rm -v` | user | ~3.5 GB (incl. 3.1 GB anonymous volumes) |
| All build cache | `docker builder prune -af` | user | **34.6 GB** |
| Images not referenced by any container | `docker image prune -af` | user | **31.5 GB** |
| npm cache | `npm cache clean --force` | user | 5.3 GB (7.0 → 1.7 GB) |
| **Total** | | | **≈ 135 GB** |

## Before / after

| | before | after |
|---|---|---|
| Images | 99, 41.1 GB | 13, 9.6 GB |
| Containers | 86 (10 running) | 28 (14 active at the time of the reading — another session was starting stacks) |
| Volumes | 474, 104.9 GB | 44, 42.2 GB |
| Build cache | 2 190 entries, 34.6 GB | 0 |
| Ubuntu `/` used | 122 GB | 117 GB |
| `docker_data.vhdx` / Ubuntu `ext4.vhdx` on `C:` | 123.3 / 142.0 GB | unchanged — files shrink only after compaction (Phase 0.3, pending) |

## Deliberately kept

- **Named volumes** — all of them, attached or not. Unattached named volumes now total ~16 GB
  (`mindex_qdrant_data` 10.3 GB, `llm-jira-estimates_jiraestimate-qdrant` 1.1 GB, `v2-*`,
  `access-server_redis_data`, `controll_redis_data`, `alert-center_opensearch_data`, …) — their containers
  were removed above, the data stays until someone decides per volume.
- The 18 containers stopped < 7 days (`qln-*`, `email-service-database-1`, `as-test-*-redesign*`, …).
- `~/git/_wt` (41 GB worktrees), NuGet packages, Playwright browsers, `~/.vscode-server`.

## Commands

Scripts as run: `clean_safe.sh` (root; docker as `jinx`) and `clean_more.sh` (`jinx`) — kept in
[diagnostics/](diagnostics/).
