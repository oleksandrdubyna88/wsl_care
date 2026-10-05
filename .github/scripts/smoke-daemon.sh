#!/usr/bin/env bash
# The smoke of a PUBLISHED wsl-care binary (plan §15e #5; E4.S2) — ONE script, run by ci-daemon.yml on every pull
# request and by release.yml on every leg before its archive is packed, so the binary that ships is exercised exactly
# the way every pull request exercised its source (ReleaseWorkflowTests holds both workflows to calling this file).
#
#   smoke-daemon.sh <binary> <Linux|Windows> <temp-dir>
#
# <temp-dir> must be a path the binary itself can use: on a Windows runner pass $RUNNER_TEMP (D:\a\_temp), which is
# native, so the Windows binary is never handed an MSYS path. Run from the repository root (the captured procfs tree
# and src_daemon/version.txt are read from there). Under bash on every runner, Git Bash on Windows.
#
# Six parts, each USING a capability rather than only starting the binary (.agents/conventions/common/testing.md,
# "Starting is not working"); every one runs under a sandbox WSL_CARE_ROOT, so the runner's real profile, state and
# tools are never touched; nothing destructive runs — act is PREVIEW only:
#   1. --help lists --help and --version; --version prints src_daemon/version.txt
#   2. the configuration round trip: set, read back from the user layer, a refused set exits 2 with one line
#   3. status --json: schemaVersion 1, its verdicts and the productVersion --version prints (E5.S0), the running block and
#      the capabilities (E6.S0), and runs show of a stranger answering unknown; on Linux over the
#      captured procfs tree (its MemTotal), on Windows the host side
#   4. the full run: collect --json records (one history line naming a run detail), status names that run, doctor answers
#   5. act <every action --help names> --preview --json with root CLAIMED inside the sandbox; Windows refuses (exit 2)
#   6. preview --all --json with NO docker reachable (PATH = an empty folder): exit 0, the JSON parses (Python's json —
#      the runners all carry it), schemaVersion 1, every row `available: false` with its reason (never 0 GB), nothing
#      written — plan §15e #5 named this part; the E4 review found it missing
#
# Output is captured rather than piped into `grep -q`, which can SIGPIPE the binary under pipefail; `tr -d '\r'`
# because the Windows binary ends its lines with CRLF. A failure prints a GitHub `::error::` line and exits 1.
set -euo pipefail

[ "$#" -eq 3 ] || { echo "usage: smoke-daemon.sh <binary> <Linux|Windows> <temp-dir>" >&2; exit 2; }
bin="$1"
os="$2"
temp="$3"
case "$os" in
  Linux | Windows) ;;
  *) echo "smoke-daemon.sh: the side is Linux or Windows, not '$os'" >&2; exit 2 ;;
esac
[ -f "$bin" ] || { echo "smoke-daemon.sh: no binary at $bin" >&2; exit 2; }

readonly PROCFS_FIXTURE="src_daemon/tests/fixtures/procfs/ubuntu-2026-10-02"
readonly FIXTURE_MEMTOTAL_BYTES=48196374528

fail() {
  echo "::error::$*"
  exit 1
}

# A fresh sandbox root per part, so no part reads another's state.
sandbox() {
  WSL_CARE_ROOT="$temp/wsl-care-$1"
  export WSL_CARE_ROOT
  rm -rf "$WSL_CARE_ROOT"
  mkdir -p "$WSL_CARE_ROOT"
}

# The captured procfs / cgroup tree under the sandbox, on Linux, where the binary reads /proc.
with_procfs_fixture() {
  if [ "$os" = "Linux" ]; then
    cp -r "$PROCFS_FIXTURE/." "$WSL_CARE_ROOT/"
  fi
}

smoke_help_and_version() {
  local help expected version
  help="$("$bin" --help | tr -d '\r')"
  case "$help" in
    *"wsl-care --help"*"wsl-care --version"*) ;;
    *) printf '%s\n' "$help"; fail "$bin --help did not list --help and --version" ;;
  esac
  expected="$(tr -d '[:space:]' < src_daemon/version.txt)"
  version="$("$bin" --version | tr -d '\r')"
  case "$version" in
    "$expected" | "$expected+"*) ;;
    *) fail "$bin --version printed '$version', expected $expected from src_daemon/version.txt" ;;
  esac
  echo "smoke: --help and --version answer: $version"
}

expect_user_25() {
  local got
  got="$("$bin" config get volumes.anonymousMaxGb --json | tr -d '\r')"
  case "$got" in *'"value": 25'*) ;; *) printf '%s\n' "$got"; fail "config get did not answer 25 ($1)" ;; esac
  case "$got" in *'"layer": "user"'*) ;; *) printf '%s\n' "$got"; fail "config get did not name the user layer ($1)" ;; esac
}

smoke_config_round_trip() {
  local code messages
  sandbox smoke
  "$bin" config set volumes.anonymousMaxGb 25 > /dev/null
  expect_user_25 "after set"
  code=0
  "$bin" config set volumes.anonymousMaxGb 100001 > "$WSL_CARE_ROOT.refused.out" 2> "$WSL_CARE_ROOT.refused.err" || code=$?
  [ "$code" -eq 2 ] || fail "a refused config set exited $code, expected 2"
  [ ! -s "$WSL_CARE_ROOT.refused.out" ] || fail "a refused config set wrote to stdout"
  messages="$(grep -c '^wsl-care: ' "$WSL_CARE_ROOT.refused.err" || true)"
  [ "$messages" -eq 1 ] || { cat "$WSL_CARE_ROOT.refused.err"; fail "a refused config set wrote $messages wsl-care: lines, expected 1"; }
  expect_user_25 "after the refused set"
  echo "smoke: config set, get and a refused set round-trip under $WSL_CARE_ROOT"
}

smoke_status() {
  local status version py shown
  sandbox status
  with_procfs_fixture
  status="$("$bin" status --json | tr -d '\r')"
  case "$status" in *'"schemaVersion": 1'*) ;; *) printf '%s\n' "$status"; fail "status --json carries no schemaVersion 1" ;; esac
  case "$status" in *'"verdicts": ['*) ;; *) printf '%s\n' "$status" | head -c 4000; fail "status --json carries no verdicts (plan §15g B1)" ;; esac
  # Parsed, not grepped: the encoder writes the + of 0.1.0+<sha> as +.
  py="$(json_python)" || fail "no python3 / python to parse status --json with"
  version="$("$bin" --version | tr -d '\r')"
  printf '%s' "$status" | WSL_CARE_SMOKE_VERSION="$version" "$py" -c '
import json, os, sys
answer = json.load(sys.stdin)
ids = [verdict["id"] for verdict in answer["verdicts"]]
assert "memory.available" in ids and "clock.jumps" in ids, "verdicts lack the sample or the full-run thresholds: %s" % ids
assert all(verdict["level"] in ("ok", "warn", "critical", "unknown") for verdict in answer["verdicts"]), "a verdict level outside the four"
assert answer["productVersion"] == os.environ["WSL_CARE_SMOKE_VERSION"], "productVersion %r is not what --version prints" % answer["productVersion"]
assert answer["running"]["state"] == "none", "the running block of an empty sandbox is %r, not none" % answer["running"]
assert "running.block" in answer["capabilities"] and isinstance(answer["actions"], list), "status lacks its capabilities or its actions (plan 15j)"
' || { printf '%s\n' "$status" | head -c 4000; fail "status --json: its verdicts, productVersion, running block or capabilities are not what --version, the thresholds and E6.S0 say"; }
  # runs show through the published binary's own JSON code (plan §15j M3): a run nothing names answers unknown, exit 0.
  shown="$("$bin" runs show 20000101T000000Z-1 --json | tr -d '\r')"
  case "$shown" in *'"state": "unknown"'*) ;; *) printf '%s\n' "$shown"; fail "runs show of a run nothing names is not unknown (plan §15j M3)" ;; esac
  if [ "$os" = "Linux" ]; then
    case "$status" in *"\"bytes\": $FIXTURE_MEMTOTAL_BYTES"*) ;; *) printf '%s\n' "$status"; fail "status --json did not report the fixture's MemTotal" ;; esac
  else
    case "$status" in *'"side": "windows"'*) ;; *) printf '%s\n' "$status"; fail "status --json did not answer for the Windows side" ;; esac
  fi
  echo "smoke: status --json answers"
}

smoke_full_run() {
  local collect history status id doctor
  sandbox collect
  with_procfs_fixture
  collect="$("$bin" collect --json | tr -d '\r')"
  case "$collect" in *'"recording": "recorded"'*) ;; *) printf '%s\n' "$collect" | head -c 4000; fail "collect --json did not record the run" ;; esac
  history="$(find "$WSL_CARE_ROOT" -name history.jsonl | head -n 1)"
  [ -n "$history" ] || fail "collect wrote no history.jsonl"
  [ "$(grep -c '"detail":"runs/' "$history")" -eq 1 ] || { cat "$history"; fail "history.jsonl does not hold exactly one line naming a run detail"; }
  [ -n "$(find "$WSL_CARE_ROOT" -path '*runs*' -name '*.json')" ] || fail "collect wrote no run detail under runs/"
  status="$("$bin" status --json | tr -d '\r')"
  id="$(sed -n 's/.*"runId":"\([^"]*\)".*/\1/p' "$history" | head -n 1)"
  [ -n "$id" ] || fail "the history line carries no runId"
  case "$status" in *"$id"*) ;; *) printf '%s\n' "$status" | head -c 4000; fail "status --json does not name the run $id its slow parts came from" ;; esac
  doctor="$("$bin" doctor --json | tr -d '\r')"
  case "$doctor" in *'"healthy": '*) ;; *) printf '%s\n' "$doctor"; fail "doctor --json carries no verdict" ;; esac
  echo "smoke: a full run is recorded, read back, and doctor answers"
}

smoke_act_preview() {
  local ids code out id
  sandbox act
  WSL_CARE_SANDBOX_PRIVILEGED=1
  export WSL_CARE_SANDBOX_PRIVILEGED
  ids="$("$bin" --help | tr -d '\r' | sed -n 's/.*"act" holds these actions: \(.*\)\.$/\1/p' | tr -d ' ')"
  [ -n "$ids" ] || fail "--help names no action that act holds"
  code=0
  out="$("$bin" act "$ids" --preview --json | tr -d '\r')" || code=$?
  if [ "$os" = "Windows" ]; then
    [ "$code" -eq 2 ] || fail "the Windows binary previewed the distro's actions (exit $code, expected 2)"
  else
    [ "$code" -eq 0 ] || { printf '%s\n' "$out" | head -c 4000; fail "act --preview exited $code"; }
    case "$out" in *'"result": "previewed"'*) ;; *) printf '%s\n' "$out" | head -c 4000; fail "act --preview did not answer previewed" ;; esac
    for id in $(printf '%s' "$ids" | tr ',' ' '); do
      case "$out" in *"\"id\": \"$id\""*) ;; *) fail "act --preview did not answer for $id" ;; esac
    done
  fi
  [ -z "$(find "$WSL_CARE_ROOT" -name 'history.jsonl' -o -name 'running.json' -o -name 'volume-seen.json')" ] || fail "a preview wrote state"
  unset WSL_CARE_SANDBOX_PRIVILEGED
  if [ "$os" = "Windows" ]; then
    echo "smoke: the Windows binary refuses the distro's actions naming the side, and writes nothing"
  else
    echo "smoke: every action ($ids) previews and nothing is written"
  fi
}

# The Python that parses JSON here: python3, else python (a Windows runner's Git Bash may answer python3 with the
# Store's alias, which does not run — so each candidate must import json).
json_python() {
  local candidate
  for candidate in python3 python; do
    if command -v "$candidate" > /dev/null 2>&1 && "$candidate" -c 'import json' > /dev/null 2>&1; then
      printf '%s' "$candidate"
      return 0
    fi
  done
  return 1
}

smoke_preview_without_docker() {
  local empty out code py rows
  sandbox preview
  py="$(json_python)" || fail "no python3 / python to parse preview --all --json with"
  # A POSIX path from mktemp, so Git Bash spells it correctly in the Windows binary's PATH.
  empty="$(mktemp -d)"
  code=0
  out="$(PATH="$empty" "$bin" preview --all --json | tr -d '\r')" || code=$?
  rmdir "$empty"
  [ "$code" -eq 0 ] || { printf '%s\n' "$out" | head -c 4000; fail "preview --all --json exited $code with no docker"; }
  rows="$(printf '%s' "$out" | "$py" -c '
import json, sys
answer = json.load(sys.stdin)
assert answer["schemaVersion"] == 1, "schemaVersion is %r" % answer["schemaVersion"]
rows = answer["rows"]
assert rows, "no rows"
shown = [row["id"] for row in rows if row.get("available") is not False or not row.get("reason")]
assert not shown, "rows shown as available, or without a reason, while docker is unreachable: %s" % shown
print(len(rows))
')" || { printf '%s\n' "$out" | head -c 4000; fail "preview --all --json did not parse as schemaVersion 1 with every row unavailable"; }
  [ -z "$(find "$WSL_CARE_ROOT" -name 'history.jsonl' -o -name 'volume-seen.json' -o -name 'running.json')" ] || fail "a preview wrote state"
  echo "smoke: preview --all --json without docker: $rows rows, each available: false with its reason, nothing written"
}

smoke_help_and_version
smoke_config_round_trip
smoke_status
smoke_full_run
smoke_act_preview
smoke_preview_without_docker
echo "smoke-daemon.sh: the published $os binary $bin passed all six parts"
