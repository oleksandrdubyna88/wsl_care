#!/usr/bin/env bash
# A bounded retry for one command, for the release scripts that ask GitHub for something that may answer wrongly once
# ("release not found" for a release that exists: PR #124's CI leg, 2026-10-12). fetch-windows-daemon.sh sources it.
# wait-marketplace-served.sh keeps its own loop: it also bounds each attempt with a timeout and a kill.

# retry_setting <name> <default> <min> <max>: the value of the environment variable <name>, or <default> when it is unset or
# empty — a whole number from <min> to <max>, else one line on stderr and exit 2 (a usage error, before anything is asked).
retry_setting() {
  local name="$1" value="${!1:-$2}"
  if [[ "$value" =~ ^[0-9]+$ ]] && [ "$value" -ge "$3" ] && [ "$value" -le "$4" ]; then
    printf '%s\n' "$value"
    return 0
  fi
  echo "$name must be a whole number from $3 to $4, not '$value'" >&2
  exit 2
}

# with_retries <attempts> <backoff-seconds> <label> <limit-note> <command...>: runs the command up to <attempts> times. Each attempt
# is said on stderr as it STARTS, with <limit-note> (a stalled attempt is never silent — the coai code round), and again with its
# exit code when it fails, so a cancelled job still shows what it saw. Before attempt k + 1 it waits k x <backoff-seconds> (a
# growing backoff). Answers 0 on the first success, else the last attempt's exit code.
with_retries() {
  local attempts="$1" backoff="$2" label="$3" note="$4" attempt=1 code=0
  shift 4
  while true; do
    echo "$label attempt $attempt of $attempts started $note" >&2
    if "$@"; then
      return 0
    else
      code=$?
    fi
    if [ "$attempt" -ge "$attempts" ]; then
      echo "$label attempt $attempt of $attempts failed (exit $code); no attempts left" >&2
      return "$code"
    fi
    echo "$label attempt $attempt of $attempts failed (exit $code); trying again in $((attempt * backoff)) s" >&2
    sleep "$((attempt * backoff))"
    attempt=$((attempt + 1))
  done
}
