# The user's `%TEMP%` on Windows — measured 2026-10-11

> What [PLAN_windows_runner.md](../todo/PLAN_windows_runner.md) §3.12 (the Windows `%TEMP%` cleanup, W-A2) rests on. Every step
> was **read-only** on the owner's machine, from an unelevated shell, except one disposable file the test below created and
> deleted inside the session's own scratch folder. Nothing in `%TEMP%` was changed.

## 1. What is there

`Get-ChildItem` over the top level of `[IO.Path]::GetTempPath()` (the user's own `%TEMP%`):

| item | count |
|---|---|
| top-level entries | 165 579 |
| folders / files | 156 943 / 8 636 |
| reparse points at the top level | 0 |
| folders older than 7 days (top-level `LastWriteTime`) | 138 790 |
| files older than 7 days | 256 |

The biggest groups of folders by name shape (`<hex>` = a run of hex digits, `<n>` = digits):

| shape | folders | older than 7 days | newest | oldest |
|---|---|---|---|---|
| `stage-<hex>` | 12 148 | 12 148 | 51 d | 76 d |
| `mindex-idx-<hex>` | 5 328 | 5 328 | 51 d | 69 d |
| `mindex-marker-<hex>` | 2 070 | 2 070 | 51 d | 75 d |
| `git-commit-<hex>` | 993 | 993 | 58 d | 86 d |
| `editstage-<hex>` | 662 | 662 | 58 d | 86 d |
| `docrag-device-<hex>` | 605 | 605 | 51 d | 76 d |
| `creds-endpoints-<hex>` | 433 | 280 | 0 d | 39 d |
| `git-branches-<hex>` | 329 | 329 | 58 d | 86 d |
| `wsl-care-test-archive-run-<hex>` | 282 | 0 | 0 d | 2 d |
| `wsl-care-test-archive-lease-<hex>` | 238 | 0 | 0 d | 4 d |
| `wsl-care-test-archive-bounded-base-<hex>` | 212 | 0 | 0 d | 2 d |
| `bge-seed-{skip,repair,missing,full}-<n>` | 4 × 100 | 4 × 99 | 0.3 d | 73 d |
| `coai-<word>-…` (all prefixes) | ≈ 4 600 | 0 | 0 d | 0 d |
| `<GUID>` | 94 | 75 | — | 76 d |

So the old bulk is many tools' scratch, not coai's. The `coai-*` folders seen earlier the same day (≈ 20 300, from
2026-09-25 on) were down to ≈ 4 600, all of that day, at this read. This repository's own tests leave `wsl-care-test-*`
folders behind: a test-hygiene defect of its own.

## 2. Does a sharing violation prove "in use"?

A disposable file in the session's scratch folder, opened by .NET and then deleted while the handle was still open:

| how it was held | `File.Delete` |
|---|---|
| `FileShare.ReadWrite \| FileShare.Delete` | **succeeded** — the name was gone at once |
| `FileShare.ReadWrite` (no delete share) | refused |

So a program that opens its files with share-delete can lose them to a cleanup that relies on "a locked file is skipped". W-A2's
"locked = in use = skipped" is not enough on its own; the plan's guards are the age of the newest descendant, an allow-list, and
budgets with "unknown = retained".
