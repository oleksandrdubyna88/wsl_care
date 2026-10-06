#!/bin/sh
# Install wsl-care — the daemon that keeps a WSL distro from degrading over the working day — into the
# WSL distro this runs in.
#
#   curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/main/install.sh | sudo sh
#   curl -fsSL …/install.sh | sudo sh -s -- --dry-run                   # every step printed, nothing changed
#   curl -fsSL …/install.sh | sudo sh -s -- --uninstall [--purge]
#
# WHAT IT CHANGES ON THIS MACHINE, and nothing else:
#   /opt/wsl-care/bin/wsl-care                  the binary (0755), linked from /usr/local/bin/wsl-care
#   /etc/systemd/system/wsl-care.service        the timer's full run (`collect --timer`)
#   /etc/systemd/system/wsl-care.timer          every 4 hours, enabled and started
#   /etc/systemd/system/wsl-care-events.service the container-start follower, enabled and started
#   /etc/systemd/system/<unit>.d/50-wsl-care-config.conf  each unit's values from the machine configuration
#                                               (the timer's period, Nice, MemoryMax, TimeoutStopSec, RestartSec),
#                                               rendered by the installed binary: `wsl-care units dropin <unit>`
#   /etc/wsl-care/config.json                   the machine configuration layer — ONLY when none exists
#   /var/lib/wsl-care, /var/log/wsl-care        its state and run logs (root-owned, 0755)
#   sysstat and atop                            installed with apt when missing, collection switched on
#                                               (debconf sysstat/enable) and their services enabled
#   /etc/wsl.conf                               ONLY with --set-default-user <name>, and only when it
#                                               names no default user yet: `[user] default=<name>` is added
# Then it runs one full `collect` (it measures and records, it never cleans), and verifies every side
# effect: sar and atop on PATH, both units active, `wsl-care doctor --json` healthy. A failed step exits
# non-zero naming the step.
#
# WHAT IT NEVER DOES, whatever the arguments (plan §3, Phase 0 — those stay with the owner, by hand):
# touch .wslconfig, wsl-pro.service, the clock services, snapd, any worktree; run `git worktree prune`,
# `wsl --shutdown` or sudo; rewrite an existing machine configuration or an existing `[user] default=`;
# eval anything it downloaded.
#
# WHAT IT VERIFIES, AND WHAT EACH CHECK PROVES. The archive's .sha256 is checked first: it proves the bytes
# were not corrupted on the way — NOT who made them, because whoever can replace the archive can replace
# its .sha256 beside it. Authenticity is the build-provenance attestation: it proves that THIS repository's
# release workflow, run for THE TAG of the version installed (`release.yml@refs/tags/daemon-v<version>`, an
# exact certificate identity) on a GitHub-hosted runner, built exactly these bytes. A build of release.yml
# from any other ref — a pushed branch that edits the workflow and keeps its attest step — is refused.
#
# HOW THE ATTESTATION IS VERIFIED, and why that way (measured 2026-10-03, research/module_tests.md):
#   1. root fetches it ITSELF, unauthenticated, from GitHub's attestation API for the archive's digest. The
#      API answers each attestation by a `bundle_url` only (snappy-compressed JSON); the bundle is fetched and
#      decompressed here (awk over od: POSIX tools, no jq, no Python).
#   2. root runs `gh attestation verify --bundle <that file>` ITSELF, with gh's configuration, cache and home
#      inside this run's temporary folder and no token: a bundle needs no login, so nobody's gh login, config
#      or Sigstore cache — not the person who ran sudo, not root's — has a say in the verdict. gh still fetches
#      Sigstore's trusted root over the network (TUF, from tuf-repo-cdn.sigstore.dev, unauthenticated).
#   3. gh must be 2.56.0 or newer: older releases cannot read today's public-good trusted root (gh 2.49.0 to
#      2.55.0 fail with "unsupported tlog public key type: PKIX_ED25519"), and Ubuntu 24.04's own gh is
#      2.45.0, which has no `gh attestation` at all. Checked BEFORE anything is downloaded.
# Without a usable `gh` the installer stops before downloading anything; `--skip-attestation` proceeds
# knowingly, says so loudly, and still checks the .sha256.
#
# `sh`, not `bash`: POSIX only, no eval, every expansion quoted. It needs root and says so; it never calls
# sudo itself.
set -eu
umask 022

readonly REPO="oleksandrdubyna88/wsl_care"
# The release workflow that signs the archives (plan §15 #12, E4.S2). The identity an attestation must carry is
# this workflow AT the release tag (SIGNER_IDENTITY, set once the version is known), matched EXACTLY with
# `--cert-identity`. Never `--signer-workflow`: gh matches that as a PREFIX of the identity, so release.yml run
# from any branch would pass (observed with gh 2.97.0: `--signer-workflow …/deploy` accepted `…/deployment.yml@
# refs/heads/trunk`).
readonly SIGNER_WORKFLOW="$REPO/.github/workflows/release.yml"
# The oldest gh whose `attestation verify --bundle` verifies a public-good attestation today (bisected over the
# gh releases on 2026-10-03: 2.55.0 fails on the trusted root's Ed25519 tlog key, 2.56.0 verifies).
readonly GH_MIN_VERSION="2.56.0"
# The REST API version the attestation request asks for (2022-11-28 answers with a deprecation date).
readonly GITHUB_API_VERSION="2026-03-10"
# Bounds on what an attestation download may be: bundles per archive, compressed and decompressed bytes.
readonly MAX_BUNDLES=10
readonly MAX_BUNDLE_BYTES=1048576
readonly MAX_BUNDLE_JSON_BYTES=4194304

readonly BIN_DIR="/opt/wsl-care/bin"
readonly BIN_PATH="$BIN_DIR/wsl-care"
readonly LINK_PATH="/usr/local/bin/wsl-care"
readonly UNIT_DIR="/etc/systemd/system"
readonly UNITS="wsl-care.service wsl-care.timer wsl-care-events.service wsl-care-act@.service"
readonly DROPIN_NAME="50-wsl-care-config.conf"
readonly CONFIG_DIR="/etc/wsl-care"
readonly CONFIG_FILE="$CONFIG_DIR/config.json"
readonly STATE_DIR="/var/lib/wsl-care"
readonly LOG_DIR="/var/log/wsl-care"
readonly LOCK_FILE="/run/wsl-care.lock"
readonly WSL_CONF="/etc/wsl.conf"
readonly SYSSTAT_DEFAULT="/etc/default/sysstat"

# Every path this script WRITES or READS as a file sits under this prefix. Empty — the real machine — unless
# a test sets it: the installer's tests run it over a temporary tree with fake systemctl / apt-get / gh / curl
# on PATH, and the prefix is what keeps /etc, /opt and /usr/local untouched there.
ROOT="${WSL_CARE_INSTALL_ROOT:-}"
# How long the final `doctor --json` may take to turn healthy (the follower's first marker, a first sample).
DOCTOR_SECONDS="${WSL_CARE_INSTALL_DOCTOR_SECONDS:-120}"
# How long an upgrade waits for a live or queued run to end before it refuses (plan §15k #16: 10 minutes).
RUN_WAIT_SECONDS="${WSL_CARE_INSTALL_RUN_WAIT_SECONDS:-600}"
# How often the wait says it is still waiting (coai E6 code round #4), and the escape for an installed binary that cannot answer.
PROGRESS_SECONDS="${WSL_CARE_INSTALL_PROGRESS_SECONDS:-30}"
SKIP_RUN_WAIT="${WSL_CARE_INSTALL_SKIP_RUN_WAIT:-0}"

export DEBIAN_FRONTEND=noninteractive

WORK=""
DRY_RUN=0
CHANGED=0

say() { printf 'wsl-care-install: %s\n' "$*"; }
warn() { printf 'wsl-care-install: %s\n' "$*" >&2; }
have() { command -v "$1" >/dev/null 2>&1; }

# Anything typed by a person or read from a download, made safe to print: every byte outside the
# printable set (a newline, an escape sequence) becomes "?".
printable() { printf '%s' "$1" | tr -c '[:print:]' '?'; }

# $1 the step, the rest the reason. Once something was written, the person also learns how to get back.
fail() {
  failed_step=$1
  shift
  printf 'wsl-care-install: FAILED at step "%s": %s\n' "$failed_step" "$*" >&2
  if [ "$CHANGED" = 1 ]; then
    printf 'wsl-care-install: part of the installation is in place; re-run the installer, or remove it with: sh -s -- --uninstall\n' >&2
  fi
  exit 1
}

# Runs a command that changes the machine, or — under --dry-run — only says it would.
run() {
  if [ "$DRY_RUN" = 1 ]; then
    say "would run: $*"
    return 0
  fi
  CHANGED=1
  "$@"
}

cleanup() {
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then
    rm -rf -- "$WORK"
  fi
}
trap cleanup EXIT
trap 'cleanup; exit 130' INT
trap 'cleanup; exit 143' TERM

usage() {
  cat <<'EOF'
Install wsl-care into this WSL distro (run as root).

  curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/main/install.sh | sudo sh -s -- [options]

  --version <x.y.z>           install this daemon release instead of the newest daemon-v* release
  --skip-attestation          do not verify the build-provenance attestation (no `gh` needed — otherwise
                              gh 2.56.0 or newer, no login); printed loudly — the .sha256 integrity check
                              still applies
  --set-default-user <name>   add `[user] default=<name>` to /etc/wsl.conf, only when it names no default
                              user yet — the user wsl-care's per-user cleanups act for (WSL also logs in
                              as that user from the next distro start)
  --dry-run                   print every step and change nothing (downloads and verifies into a temporary
                              folder that is removed); root is not needed
  --uninstall                 stop and remove the units, the binary and its link; KEEP the history
                              (/var/lib/wsl-care), the run logs (/var/log/wsl-care) and the machine
                              configuration (/etc/wsl-care)
  --purge                     with --uninstall: remove those three as well
  -h, --help                  this text
EOF
}

usage_fail() {
  printf 'wsl-care-install: %s (see --help)\n' "$1" >&2
  exit 2
}

# --- arguments -----------------------------------------------------------------------------------
VERSION=""
SKIP_ATTESTATION=0
UNINSTALL=0
PURGE=0
DEFAULT_USER=""
ARGS_SHOWN=$(printable "$*")

while [ $# -gt 0 ]; do
  case "$1" in
    --version)
      [ $# -ge 2 ] || usage_fail "--version needs a value"
      VERSION=$2
      shift 2
      ;;
    --skip-attestation) SKIP_ATTESTATION=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    --purge) PURGE=1; shift ;;
    --set-default-user)
      [ $# -ge 2 ] || usage_fail "--set-default-user needs a user name"
      DEFAULT_USER=$2
      shift 2
      ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) usage_fail "unknown argument: $(printable "$1")" ;;
  esac
done

# $1 a value, $2 an extended regular expression it must match whole. The `case` first refuses any byte
# outside the safe set — a newline included — so grep sees exactly one line.
matches() {
  case "$1" in
    '' | *[!0-9A-Za-z._-]*) return 1 ;;
  esac
  printf '%s\n' "$1" | grep -Eq "$2"
}

readonly VERSION_PATTERN='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$'
readonly USER_PATTERN='^[a-z_][a-z0-9_-]{0,31}$'

if [ "$PURGE" = 1 ] && [ "$UNINSTALL" = 0 ]; then
  usage_fail "--purge goes with --uninstall"
fi
if [ "$UNINSTALL" = 1 ] && { [ -n "$VERSION" ] || [ "$SKIP_ATTESTATION" = 1 ] || [ -n "$DEFAULT_USER" ]; }; then
  usage_fail "--uninstall takes only --purge and --dry-run"
fi
if [ -n "$VERSION" ] && ! matches "$VERSION" "$VERSION_PATTERN"; then
  usage_fail "--version must look like 1.2.3 (got \"$(printable "$VERSION")\")"
fi
if [ -n "$DEFAULT_USER" ] && ! matches "$DEFAULT_USER" "$USER_PATTERN"; then
  usage_fail "--set-default-user takes a Linux user name (got \"$(printable "$DEFAULT_USER")\")"
fi
case "$ROOT" in
  '') ;;
  /*[!A-Za-z0-9._/-]* | *..*) usage_fail "WSL_CARE_INSTALL_ROOT must be a plain absolute path" ;;
  /*) say "every file goes under $ROOT (WSL_CARE_INSTALL_ROOT) — a test prefix, not this machine" ;;
  *) usage_fail "WSL_CARE_INSTALL_ROOT must be an absolute path" ;;
esac
case "$DOCTOR_SECONDS" in
  '' | *[!0-9]*) usage_fail "WSL_CARE_INSTALL_DOCTOR_SECONDS must be a number of seconds" ;;
esac

# --- reading this machine --------------------------------------------------------------------------

# The `default` key of the [user] section of /etc/wsl.conf, read the way the daemon reads it
# (WslCare.Core TargetUserDiscovery.DefaultUser; a test runs both over the same files): trimmed lines, `#` and `;`
# comments, section names and keys case-insensitive, the value trimmed of spaces and quotes, the FIRST
# `default` of the section wins. Empty when there is none.
wsl_conf_default() {
  [ -f "$ROOT$WSL_CONF" ] || return 0
  awk '
    { sub(/\r$/, ""); line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
    line ~ /^\[.*\]$/ { section = tolower(substr(line, 2, length(line) - 2)); gsub(/^[ \t]+|[ \t]+$/, "", section); next }
    line ~ /^[#;]/ { next }
    {
      eq = index(line, "=")
      if (eq <= 1 || section != "user") next
      key = tolower(substr(line, 1, eq - 1)); gsub(/^[ \t]+|[ \t]+$/, "", key)
      if (key != "default") next
      value = substr(line, eq + 1); gsub(/^[ \t]+|[ \t]+$/, "", value); gsub(/^["\047]+|["\047]+$/, "", value)
      print value
      exit
    }
  ' "$ROOT$WSL_CONF"
}

# Whether /etc/wsl.conf has a [user] section at all.
wsl_conf_has_user_section() {
  [ -f "$ROOT$WSL_CONF" ] || return 1
  awk '
    { sub(/\r$/, ""); line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
    line ~ /^\[.*\]$/ { s = tolower(substr(line, 2, length(line) - 2)); gsub(/^[ \t]+|[ \t]+$/, "", s); if (s == "user") { found = 1; exit } }
    END { exit found ? 0 : 1 }
  ' "$ROOT$WSL_CONF"
}

user_exists() {
  [ -f "$ROOT/etc/passwd" ] && awk -F: -v u="$1" '$1 == u { found = 1 } END { exit found ? 0 : 1 }' "$ROOT/etc/passwd"
}

sysstat_enabled() {
  [ -f "$ROOT$SYSSTAT_DEFAULT" ] && grep -Eq '^ENABLED="?true"?[[:space:]]*$' "$ROOT$SYSSTAT_DEFAULT"
}

# $1 url, $2 destination file, $3 ceiling in seconds, then any extra curl arguments (request headers). HTTPS
# only, redirects included (a release asset redirects to GitHub's object store); a ceiling on every transfer.
fetch() {
  url=$1
  out=$2
  ceiling=$3
  shift 3
  curl --url "$url" --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    --tlsv1.2 --retry 3 --connect-timeout 15 --max-time "$ceiling" --output "$out" "$@"
}

# $1 >= $2, both x.y.z — numerically, field by field.
version_at_least() {
  case "$1" in '' | *[!0-9.]*) return 1 ;; esac
  [ "$(printf '%s\n%s\n' "$2" "$1" | sort -t. -k1,1n -k2,2n -k3,3n | head -n 1)" = "$2" ]
}

# Runs gh as THIS process (root, or the dry-run's user) with its configuration, cache, data, state and home inside
# this run's temporary folder and every token variable removed: nobody's login, config or Sigstore cache takes part
# in the verdict. A ceiling on every call.
gh_isolated() {
  (
    unset GH_TOKEN GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN GH_HOST GH_REPO
    HOME="$WORK/gh/home"
    GH_CONFIG_DIR="$WORK/gh/config"
    XDG_CONFIG_HOME="$WORK/gh/config"
    XDG_CACHE_HOME="$WORK/gh/cache"
    XDG_DATA_HOME="$WORK/gh/data"
    XDG_STATE_HOME="$WORK/gh/state"
    GH_NO_UPDATE_NOTIFIER=1
    GH_PROMPT_DISABLED=1
    export HOME GH_CONFIG_DIR XDG_CONFIG_HOME XDG_CACHE_HOME XDG_DATA_HOME XDG_STATE_HOME GH_NO_UPDATE_NOTIFIER GH_PROMPT_DISABLED
    exec timeout 180 gh "$@"
  )
}

# $1 a file in snappy's raw block format (what GitHub serves an attestation bundle as: a varint length, then
# literal and copy elements), $2 where the decompressed bytes go. awk over `od`, POSIX tools only; LC_ALL=C so
# every awk writes each value as ONE byte (mawk and gawk alike — a bundle holds bytes above 127). Refuses a stream
# that ends early, a copy that reaches before the output's start, a length that does not match, a NUL byte, and a
# declared length above MAX_BUNDLE_JSON_BYTES. A wrong decoding cannot pass anything: gh verifies what comes out.
unsnappy() {
  od -An -v -tu1 "$1" | LC_ALL=C awk -v max="$MAX_BUNDLE_JSON_BYTES" '
    function bad(why) { printf "snappy: %s\n", why > "/dev/stderr"; failed = 1; exit 1 }
    function need(count) { if (p + count > n) bad("the stream ends inside an element") }
    { for (i = 1; i <= NF; i++) b[n++] = $i + 0 }
    END {
      if (failed) exit 1
      len = 0; mul = 1
      do {
        need(1); c = b[p++]; len += (c % 128) * mul; mul *= 128
        if (mul > 34359738368) bad("the length does not end")
      } while (c >= 128)
      if (len > max) bad("the declared length " len " is above " max " bytes")
      o = 0
      while (p < n) {
        tag = b[p++]; kind = tag % 4
        if (kind == 0) {
          l = int(tag / 4)
          if (l >= 60) { k = l - 59; need(k); l = 0; m = 1; for (j = 0; j < k; j++) { l += b[p++] * m; m *= 256 } }
          l++
          need(l)
          if (o + l > len) bad("a literal runs past the declared length")
          for (j = 0; j < l; j++) out[o++] = b[p++]
          continue
        }
        if (kind == 1) { need(1); l = int(tag / 4) % 8 + 4; off = int(tag / 32) * 256 + b[p++] }
        else if (kind == 2) { need(2); l = int(tag / 4) + 1; off = b[p] + b[p + 1] * 256; p += 2 }
        else { need(4); l = int(tag / 4) + 1; off = b[p] + b[p + 1] * 256 + b[p + 2] * 65536 + b[p + 3] * 16777216; p += 4 }
        if (off < 1 || off > o) bad("a copy reaches before the start of the output")
        if (o + l > len) bad("a copy runs past the declared length")
        for (j = 0; j < l; j++) { out[o] = out[o - off]; o++ }
      }
      if (o != len) bad("the stream holds " o " bytes, its header says " len)
      for (j = 0; j < o; j++) {
        if (out[j] == 0) bad("a NUL byte - not a JSON bundle")
        printf "%c", out[j]
      }
    }' > "$2"
}

# --- preflight, shared -------------------------------------------------------------------------------

# The command that starts THIS installer again: from the ref it is pinned to — the tag of --version, spelt
# refs/tags/daemon-v<version> so no branch of that name can be served instead; main when no version was given — with
# the arguments it was given (printable) plus $1, when there is one. Every "re-run" line this script prints is this one.
rerun_command() {
  if [ -n "$VERSION" ]; then
    rerun_ref="refs/tags/daemon-v$VERSION"
  else
    rerun_ref="main"
  fi
  rerun_args="$ARGS_SHOWN${1:+ $1}"
  printf 'curl -fsSL https://raw.githubusercontent.com/%s/%s/install.sh | sudo sh -s -- %s' "$REPO" "$rerun_ref" "${rerun_args# }"
}

preflight_common() {
  os=$(uname -s)
  [ "$os" = Linux ] || fail preflight "this installs into a WSL Linux distro; this is $(printable "$os")"
  if [ "$DRY_RUN" = 1 ]; then
    say "dry run: nothing will be changed, and root is not needed for it"
  elif [ "$(id -u)" != 0 ]; then
    fail preflight "run it as root — it installs units and packages. It never calls sudo itself; re-run:
  $(rerun_command)"
  fi
  # sd_booted(3): systemd is PID 1 exactly when this directory exists.
  [ -d "$ROOT/run/systemd/system" ] || fail preflight "systemd is not running in this distro. Add
  [boot]
  systemd=true
to /etc/wsl.conf yourself, restart the distro from Windows (wsl --terminate <distro>), and run this again."
  for tool in systemctl awk sed grep tr; do
    have "$tool" || fail preflight "$tool is not installed"
  done
}

# --- uninstall -----------------------------------------------------------------------------------------
uninstall() {
  preflight_common
  say "uninstalling wsl-care"
  enabled=""
  for unit in wsl-care.timer wsl-care-events.service; do
    if [ -f "$ROOT$UNIT_DIR/$unit" ]; then enabled="$enabled $unit"; fi
  done
  if [ -n "$enabled" ]; then
    # shellcheck disable=SC2086 # a list of fixed unit names, split on purpose
    run systemctl disable --now $enabled || fail units "systemctl disable --now$enabled failed"
  fi
  if [ -f "$ROOT$UNIT_DIR/wsl-care.service" ]; then
    run systemctl stop wsl-care.service || fail units "systemctl stop wsl-care.service failed"
  fi
  if [ -f "$ROOT$UNIT_DIR/wsl-care-act@.service" ]; then
    # Every detached run still loaded (E6.S1): its unit file is about to go. systemctl matches the pattern against loaded
    # units only, so none loaded is no error. Quoted: the pattern is systemctl's, never the shell's.
    run systemctl stop 'wsl-care-act@*.service' || fail units "systemctl stop wsl-care-act@*.service failed"
  fi
  for unit in $UNITS; do
    if [ -f "$ROOT$UNIT_DIR/$unit" ]; then run rm -f -- "$ROOT$UNIT_DIR/$unit"; fi
    if [ -f "$ROOT$UNIT_DIR/$unit.d/$DROPIN_NAME" ]; then run rm -f -- "$ROOT$UNIT_DIR/$unit.d/$DROPIN_NAME"; fi
    if [ -d "$ROOT$UNIT_DIR/$unit.d" ] && [ -z "$(ls -A "$ROOT$UNIT_DIR/$unit.d")" ]; then run rmdir -- "$ROOT$UNIT_DIR/$unit.d"; fi
  done
  run systemctl daemon-reload || fail units "systemctl daemon-reload failed"

  if [ -h "$ROOT$LINK_PATH" ] && [ "$(readlink "$ROOT$LINK_PATH")" = "$BIN_PATH" ]; then
    run rm -f -- "$ROOT$LINK_PATH"
  elif [ -e "$ROOT$LINK_PATH" ] || [ -h "$ROOT$LINK_PATH" ]; then
    warn "left $LINK_PATH alone: it is not the link this installer makes"
  fi
  if [ -e "$ROOT$BIN_PATH" ]; then run rm -f -- "$ROOT$BIN_PATH"; fi
  # A .new an interrupted install left beside it (E6.S1 review).
  if [ -e "$ROOT$BIN_PATH.new" ] || [ -h "$ROOT$BIN_PATH.new" ]; then run rm -f -- "$ROOT$BIN_PATH.new"; fi
  for dir in "$BIN_DIR" /opt/wsl-care; do
    if [ -d "$ROOT$dir" ] && [ -z "$(ls -A "$ROOT$dir")" ]; then run rmdir -- "$ROOT$dir"; fi
  done

  if [ "$PURGE" = 1 ]; then
    say "--purge: removing exactly these:"
    say "  $STATE_DIR   (history.jsonl, run details, container starts, volume-seen.json, running.json)"
    say "  $LOG_DIR   (the run logs)"
    say "  $CONFIG_DIR   (the machine configuration layer)"
    say "  $LOCK_FILE   (the run lock)"
    for dir in "$STATE_DIR" "$LOG_DIR" "$CONFIG_DIR"; do
      if [ -e "$ROOT$dir" ]; then run rm -rf -- "$ROOT$dir"; fi
    done
    if [ -e "$ROOT$LOCK_FILE" ]; then run rm -f -- "$ROOT$LOCK_FILE"; fi
  else
    say "kept: $STATE_DIR (history, run details, container starts), $LOG_DIR (run logs),"
    say "      $CONFIG_DIR (machine configuration) — --uninstall --purge removes them"
  fi
  say "never removed: sysstat and atop (other tools may use them), /etc/wsl.conf, every user's ~/.config/wsl-care"

  if [ "$DRY_RUN" = 1 ]; then
    say "dry run: nothing was changed"
    return 0
  fi
  for unit in $UNITS; do
    [ ! -e "$ROOT$UNIT_DIR/$unit" ] || fail "verify: units removed" "$UNIT_DIR/$unit is still there"
  done
  if systemctl is-active --quiet wsl-care.timer; then
    fail "verify: timer stopped" "wsl-care.timer is still active"
  fi
  [ ! -e "$ROOT$BIN_PATH" ] || fail "verify: binary removed" "$BIN_PATH is still there"
  say "uninstalled"
}

# --- install ---------------------------------------------------------------------------------------

install_preflight() {
  preflight_common
  case $(uname -m) in
    x86_64 | amd64) RID=linux-x64 ;;
    aarch64 | arm64) RID=linux-arm64 ;;
    *) fail preflight "unsupported architecture $(printable "$(uname -m)"): releases exist for x86_64 and aarch64" ;;
  esac
  for tool in curl sha256sum tar gzip install ln mktemp timeout cut; do
    have "$tool" || fail preflight "$tool is not installed"
  done
  if ! have sar || ! have atop || ! sysstat_enabled; then
    for tool in apt-get debconf-set-selections dpkg-reconfigure; do
      have "$tool" || fail preflight "$tool is not installed, and sysstat / atop need installing or switching on"
    done
  fi
  for tool in od sort head tail wc; do
    have "$tool" || fail preflight "$tool is not installed"
  done
  if [ -e "$ROOT$LINK_PATH" ] || [ -h "$ROOT$LINK_PATH" ]; then
    if [ ! -h "$ROOT$LINK_PATH" ] || [ "$(readlink "$ROOT$LINK_PATH")" != "$BIN_PATH" ]; then
      fail preflight "$LINK_PATH exists and is not this installer's link to $BIN_PATH — remove it yourself, then run this again"
    fi
  fi
  wsl_conf_preflight
}

# Decides what happens to /etc/wsl.conf BEFORE anything is installed, so a request that cannot be honoured
# refuses with nothing changed. WSL_CONF_ACTION: keep | write | advise.
wsl_conf_preflight() {
  CURRENT_DEFAULT=$(wsl_conf_default)
  if [ -n "$CURRENT_DEFAULT" ]; then
    WSL_CONF_ACTION="keep"
  elif [ -z "$DEFAULT_USER" ]; then
    WSL_CONF_ACTION="advise"
  else
    user_exists "$DEFAULT_USER" || fail preflight "--set-default-user: there is no user \"$DEFAULT_USER\" in /etc/passwd"
    [ ! -h "$ROOT$WSL_CONF" ] || fail preflight "$WSL_CONF is a symbolic link; it is never rewritten — add [user] default=$DEFAULT_USER yourself"
    if wsl_conf_has_user_section; then
      fail preflight "$WSL_CONF has a [user] section without default= — add default=$DEFAULT_USER to it yourself; an existing section is never rewritten"
    fi
    WSL_CONF_ACTION="write"
  fi
}

# How to get a gh that can verify, said once for every refusal below: GitHub's own apt repository — Ubuntu's
# package (2.45.0 on 24.04) is the one that is too old. The skip line is the LAST resort and names the SAME installer
# and release this run was started with (E5 code round, security #3): an install pinned with --version — what the
# extension's *Install daemon* types — is never pointed at main's installer and the newest daemon, unverified.
gh_advice() {
  printf '%s' "Nothing was installed. Install gh $GH_MIN_VERSION or newer from GitHub's apt repository
(https://cli.github.com/packages; the steps: https://github.com/cli/cli/blob/trunk/docs/install_linux.md) —
Ubuntu's own gh package is older than that — and run this again; no gh login is needed.
LAST RESORT, knowingly WITHOUT the attestation (the .sha256 integrity check still applies) — the same installer and
release you started, with --skip-attestation added:
  $(rerun_command --skip-attestation)"
}

# BEFORE anything is downloaded: a gh that can verify a bundle against today's trusted root, with the flags the
# verification uses. Its version alone is not enough (gh 2.49 to 2.55 have the command and cannot verify), nor is
# the command's presence alone (Ubuntu's 2.45.0 lacks it): both are asked.
gh_preflight() {
  [ "$SKIP_ATTESTATION" = 0 ] || return 0
  have gh || fail preflight "the GitHub CLI (gh) is not installed, and it is what verifies that this
repository's release workflow built the archive (its build-provenance attestation). $(gh_advice)"
  mkdir -m 0700 "$WORK/gh" "$WORK/gh/home" "$WORK/gh/config" "$WORK/gh/cache" "$WORK/gh/data" "$WORK/gh/state"
  gh_version=$(gh_isolated --version 2>/dev/null | sed -n '1s/^gh version \([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/p') || gh_version=""
  version_at_least "$gh_version" "$GH_MIN_VERSION" || fail preflight "gh ${gh_version:-of an unknown version} is older than $GH_MIN_VERSION,
the oldest that verifies a release attestation today. $(gh_advice)"
  gh_help=$(gh_isolated attestation verify --help 2>&1) || fail preflight "this gh ($gh_version) has no working \`gh attestation verify\`. $(gh_advice)"
  for flag in --bundle --cert-identity --deny-self-hosted-runners --repo; do
    case "$gh_help" in
      *"$flag "*) ;;
      *) fail preflight "this gh ($gh_version) offers no $flag for \`gh attestation verify\`. $(gh_advice)" ;;
    esac
  done
}

resolve_version() {
  if [ -n "$VERSION" ]; then
    return 0
  fi
  # The newest `daemon-v*` release, NOT releases/latest: this repository releases the daemon and the
  # VS Code extension, and "latest" is usually the extension's .vsix. Read WITHOUT jq and without trusting the
  # answer's layout: JSON holds no raw newline inside a string, so the answer is joined into one line and every
  # `"tag_name": "daemon-v<x.y.z>"` is taken from it (a pre-release such as -rc.1 does not match), then the HIGHEST
  # version wins, compared number by number — not the first one listed, and not one line of pretty-printed output.
  api="https://api.github.com/repos/$REPO/releases?per_page=100"
  fetch "$api" "$WORK/releases.json" 60 || fail resolve-release "could not read $api"
  tr -d '\r\n' < "$WORK/releases.json" | grep -o '"tag_name"[[:space:]]*:[[:space:]]*"daemon-v[0-9][0-9.]*"' \
    | sed 's/.*"daemon-v\([0-9.]*\)"$/\1/' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' \
    | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1 > "$WORK/daemon-version" || true
  candidate=$(cat "$WORK/daemon-version")
  if [ -n "$candidate" ] && matches "$candidate" "$VERSION_PATTERN"; then
    VERSION=$candidate
    return 0
  fi
  fail resolve-release "no daemon-v* release found at $api — pass --version <x.y.z>"
}

download_and_verify() {
  NAME="wsl-care-$VERSION-$RID"
  ARCHIVE="$NAME.tar.gz"
  base="https://github.com/$REPO/releases/download/daemon-v$VERSION"
  say "release daemon-v$VERSION, $RID: $ARCHIVE"
  fetch "$base/$ARCHIVE" "$WORK/$ARCHIVE" 600 || fail download "could not download $base/$ARCHIVE"
  fetch "$base/$ARCHIVE.sha256" "$WORK/$ARCHIVE.sha256" 60 \
    || fail download "could not download $base/$ARCHIVE.sha256 — without it the archive cannot be checked, so nothing is installed"

  expected=$(sed -n '1s/^\([0-9A-Fa-f]\{64\}\)\([[:space:]].*\)\{0,1\}$/\1/p' "$WORK/$ARCHIVE.sha256" | tr 'A-F' 'a-f')
  [ -n "$expected" ] || fail checksum "$ARCHIVE.sha256 does not start with a SHA-256 — nothing is installed"
  actual=$(sha256sum "$WORK/$ARCHIVE" | cut -d' ' -f1)
  [ "$expected" = "$actual" ] || fail checksum "the archive does not match its .sha256 — nothing is installed.
  expected $expected
  actual   $actual"
  say "checksum ok: $actual (integrity: the bytes arrived as published)"

  if [ "$SKIP_ATTESTATION" = 1 ]; then
    {
      echo "=================================================================================="
      echo "wsl-care-install: ATTESTATION NOT VERIFIED (--skip-attestation)."
      echo "  The archive matches its .sha256 — it was not corrupted on the way. That does NOT"
      echo "  prove who built it: whoever can replace the archive can replace its .sha256 too."
      echo "=================================================================================="
    } >&2
  else
    verify_attestation
  fi
}

verify_attestation() {
  SIGNER_IDENTITY="https://github.com/$SIGNER_WORKFLOW@refs/tags/daemon-v$VERSION"
  api="https://api.github.com/repos/$REPO/attestations/sha256:$actual"
  fetch "$api" "$WORK/attestations.json" 60 --header 'Accept: application/vnd.github+json' --header "X-GitHub-Api-Version: $GITHUB_API_VERSION" \
    || fail attestation "could not read $api — nothing is installed"
  # Every bundle URL of the answer, whatever its whitespace: JSON holds no raw newline inside a string, so the
  # answer is joined into one line first. A JSON encoder may write & as \u0026 and / as \/.
  tr -d '\r\n' < "$WORK/attestations.json" | grep -o '"bundle_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | sed -e 's/^"bundle_url"[[:space:]]*:[[:space:]]*"//' -e 's/"$//' -e 's/\\u0026/\&/g' -e 's/\\\//\//g' \
    | head -n "$MAX_BUNDLES" > "$WORK/bundle-urls" || true
  [ -s "$WORK/bundle-urls" ] || fail attestation "GitHub has no attestation for $ARCHIVE (sha256:$actual) — nothing is installed"
  number=0
  while IFS= read -r url; do
    number=$((number + 1))
    if verify_bundle "$url" "$WORK/bundle-$number"; then
      say "attestation ok: built by $SIGNER_IDENTITY on a GitHub-hosted runner (authenticity)"
      return 0
    fi
  done < "$WORK/bundle-urls"
  fail attestation "no attestation of $ARCHIVE was made by $SIGNER_IDENTITY on a GitHub-hosted runner (gh said why above) — nothing is installed"
}

# $1 a bundle URL from the attestation API, $2 the path stem to keep it under. 0 when gh verifies that bundle
# against the pinned identity; anything that stops one bundle (a download, a bad stream, gh's refusal) says why and
# leaves the next bundle its turn.
verify_bundle() {
  case "$1" in
    https://*) ;;
    *) warn "skipped an attestation whose bundle URL is not https: $(printable "$1")"; return 1 ;;
  esac
  if ! fetch "$1" "$2.json.sn" 60 || [ "$(wc -c < "$2.json.sn")" -gt "$MAX_BUNDLE_BYTES" ]; then
    warn "an attestation bundle could not be read (download failed or larger than $MAX_BUNDLE_BYTES bytes)"
    return 1
  fi
  if ! unsnappy "$2.json.sn" "$2.json"; then
    warn "an attestation bundle could not be read (not a snappy stream of a JSON bundle)"
    return 1
  fi
  gh_isolated attestation verify "$WORK/$ARCHIVE" --bundle "$2.json" --repo "$REPO" \
    --cert-identity "$SIGNER_IDENTITY" --deny-self-hosted-runners
}

# The archive must hold exactly regular files and folders under one top folder: no absolute name, no
# "..", no link or device — a link member would let a later member be written through it.
unpack() {
  tar -tzf "$WORK/$ARCHIVE" > "$WORK/members" 2>/dev/null || fail unpack "$ARCHIVE is not a gzip tar archive"
  while IFS= read -r member; do
    case "$member" in
      /* | .. | ../* | */../* | */..) fail unpack "a member leaves the archive's folder: $(printable "$member")" ;;
      "$NAME" | "$NAME"/*) ;;
      *) fail unpack "a member outside $NAME/: $(printable "$member")" ;;
    esac
  done < "$WORK/members"
  if tar -tvzf "$WORK/$ARCHIVE" | cut -c1 | grep -qv '^[-d]$'; then
    fail unpack "$ARCHIVE holds a link or a special file; only regular files and folders are accepted"
  fi
  mkdir "$WORK/x"
  tar -xzf "$WORK/$ARCHIVE" -C "$WORK/x" --no-same-owner --no-same-permissions || fail unpack "could not extract $ARCHIVE"
  SRC="$WORK/x/$NAME"
  for file in wsl-care systemd/wsl-care.service systemd/wsl-care.timer systemd/wsl-care-events.service systemd/wsl-care-act@.service config/machine.json; do
    [ -f "$SRC/$file" ] && [ ! -h "$SRC/$file" ] || fail unpack "$ARCHIVE has no $NAME/$file"
  done
}

# The running block of the INSTALLED binary's `status --json` (E6.S0), read without depending on its layout (coai E6 code
# round #1): the answer is flattened and the first "state" / "runId" after the "running" key is taken. Sets STATE and RUN_ID;
# fails (returns 1) when there is no answer at all — a status that crashed, timed out or printed nothing. An answer with no
# running block (a binary older than E6.S0) leaves STATE empty; a running block whose state cannot be read is "unparsed".
running_state() {
  STATE=""
  RUN_ID=""
  answer=$(timeout 30 "$ROOT$BIN_PATH" status --json 2>/dev/null) || return 1
  [ -n "$answer" ] || return 1
  flat=$(printf '%s' "$answer" | tr -d '\r\n')
  printf '%s' "$flat" | grep -q '"running"[[:space:]]*:[[:space:]]*{' || return 0
  block=$(printf '%s' "$flat" | sed 's/.*"running"[[:space:]]*:[[:space:]]*{//')
  STATE=$(printf '%s' "$block" | sed -n 's/^[^}]*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
  RUN_ID=$(printf '%s' "$block" | sed -n 's/^[^}]*"runId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
  [ -n "$STATE" ] || STATE="unparsed"
  return 0
}

# Whether a run is in flight — failing CLOSED (E6.S1 review S4, coai E6 code round #1): in flight UNLESS the running block's
# state is "none" or "dead", or there is no running block at all (an older binary); a state this installer does not know is in
# flight too, so a new state can never silently switch the wait off. No answer counts as in flight (NO_ANSWER=1). FLIGHT says why.
run_in_flight() {
  NO_ANSWER=0
  [ -x "$ROOT$BIN_PATH" ] || return 1
  if ! running_state; then
    NO_ANSWER=1
    FLIGHT="the installed binary gave no status answer"
    return 0
  fi
  case "$STATE" in
    "" | none | dead) return 1 ;;
  esac
  FLIGHT="a wsl-care run is $STATE${RUN_ID:+ ($RUN_ID)}"
  return 0
}

# An upgrade never replaces the daemon under a run in flight (plan §15k #16): it waits, bounded on the WALL clock (coai E6 code
# round #8 — counting the sleeps let a hung status stretch the ceiling to over an hour), says so every PROGRESS_SECONDS (#4),
# and then REFUSES naming why — with the manual escape when the installed binary itself cannot answer (#5).
wait_for_runs() {
  [ "$DRY_RUN" = 1 ] && return 0
  if [ "$SKIP_RUN_WAIT" = 1 ]; then
    warn "WSL_CARE_INSTALL_SKIP_RUN_WAIT=1: not checking for a run in flight"
    return 0
  fi
  started=$(date +%s)
  noted=""
  while run_in_flight; do
    now=$(date +%s)
    elapsed=$((now - started))
    if [ "$elapsed" -ge "$RUN_WAIT_SECONDS" ]; then
      if [ "$NO_ANSWER" = 1 ]; then
        fail upgrade-wait "$FLIGHT for ${elapsed}s, so whether a run is in flight cannot be told; nothing was replaced. If no wsl-care run is in flight, remove $STATE_DIR/running.json and $STATE_DIR/requests/*.json by hand, or run this again with WSL_CARE_INSTALL_SKIP_RUN_WAIT=1 to skip this wait"
      fi
      fail upgrade-wait "$FLIGHT, still after ${elapsed}s (see: $BIN_PATH status); nothing was replaced - try again when it ends (a request a stopped distro left behind is swept by: sudo wsl-care collect)"
    fi
    if [ -z "$noted" ]; then
      say "$FLIGHT; waiting (at most ${RUN_WAIT_SECONDS}s)"
      noted=$now
    elif [ $((now - noted)) -ge "$PROGRESS_SECONDS" ]; then
      say "still waiting: ${STATE:-no answer}${RUN_ID:+ $RUN_ID}, ${elapsed}s of ${RUN_WAIT_SECONDS}s"
      noted=$now
    fi
    sleep 5
  done
}

install_files() {
  UPGRADE=0
  if [ -e "$ROOT$BIN_PATH" ]; then UPGRADE=1; fi
  if [ "$UPGRADE" = 1 ]; then wait_for_runs; fi
  run install -d -m 0755 "$ROOT/opt/wsl-care" "$ROOT$BIN_DIR" "$ROOT/usr/local/bin" \
    || fail install-binary "could not create $BIN_DIR"
  # Never over the running binary (plan §15k #16): a run in flight keeps its file, a new one starts the new file — the binary
  # goes in beside it and is RENAMED over it, one atomic step.
  run install -m 0755 "$SRC/wsl-care" "$ROOT$BIN_PATH.new" || fail install-binary "could not install $BIN_PATH.new"
  if ! run mv -f "$ROOT$BIN_PATH.new" "$ROOT$BIN_PATH"; then
    rm -f -- "$ROOT$BIN_PATH.new"
    fail install-binary "could not rename $BIN_PATH.new over $BIN_PATH"
  fi
  if [ ! -h "$ROOT$LINK_PATH" ]; then
    run ln -s "$BIN_PATH" "$ROOT$LINK_PATH" || fail install-binary "could not link $LINK_PATH"
  fi
  say "binary: $BIN_PATH, linked from $LINK_PATH"

  run install -d -m 0755 "$ROOT$UNIT_DIR" || fail install-units "could not create $UNIT_DIR"
  for unit in $UNITS; do
    run install -m 0644 "$SRC/systemd/$unit" "$ROOT$UNIT_DIR/$unit" || fail install-units "could not install $UNIT_DIR/$unit"
  done
  say "units: $UNITS in $UNIT_DIR"

  if [ -e "$ROOT$CONFIG_FILE" ] || [ -h "$ROOT$CONFIG_FILE" ]; then
    say "kept $CONFIG_FILE: an existing machine configuration is never overwritten"
  else
    run install -d -m 0755 "$ROOT$CONFIG_DIR" || fail machine-config "could not create $CONFIG_DIR"
    run install -m 0644 "$SRC/config/machine.json" "$ROOT$CONFIG_FILE" || fail machine-config "could not write $CONFIG_FILE"
    say "machine configuration: $CONFIG_FILE (empty: every value is the binary's default)"
  fi
  run install -d -m 0755 "$ROOT$STATE_DIR" "$ROOT$LOG_DIR" || fail install-binary "could not create $STATE_DIR and $LOG_DIR"
}

# E7.S2c: each unit's values that are configuration (the timer's period and run limit, the services' Nice, MemoryMax and
# TimeoutStopSec, the follower's RestartSec) go into a drop-in the INSTALLED binary renders from the machine layer — one
# definition, the key; this script never parses the configuration. Written on every install and upgrade, so running
# install.sh again after changing /etc/wsl-care/config.json applies it; `wsl-care doctor` names a drop-in that no longer
# matches. E7.S2b/S2c review C-M8: a binary that does not know the verb (an older release, `--version`; it answers 2) or that
# refuses an invalid machine configuration (78) gets no drop-in, said, and the stale one goes: the units keep their own values.
write_dropins() {
  for unit in $UNITS; do
    dir="$UNIT_DIR/$unit.d"
    if [ "$DRY_RUN" = 1 ]; then
      say "would write $dir/$DROPIN_NAME from: $BIN_PATH units dropin $unit"
      continue
    fi
    code=0
    timeout 60 "$ROOT$BIN_PATH" units dropin "$unit" > "$WORK/$DROPIN_NAME" || code=$?
    # coai E7 code round #6: an empty answer is no drop-in — never installed silently.
    if [ "$code" = 0 ] && [ ! -s "$WORK/$DROPIN_NAME" ]; then code=empty; fi
    case "$code" in
      0)
        install -d -m 0755 "$ROOT$dir" || fail install-units "could not create $dir"
        install -m 0644 "$WORK/$DROPIN_NAME" "$ROOT$dir/$DROPIN_NAME" || fail install-units "could not install $dir/$DROPIN_NAME"
        ;;
      empty)
        warn "no drop-in for $unit: $BIN_PATH units dropin $unit answered nothing; the unit keeps its own values"
        if [ -f "$ROOT$dir/$DROPIN_NAME" ]; then rm -f -- "$ROOT$dir/$DROPIN_NAME" || fail install-units "could not remove the stale $dir/$DROPIN_NAME"; fi
        ;;
      2 | 78)
        warn "no drop-in for $unit: $BIN_PATH units dropin $unit answered $code ($( [ "$code" = 2 ] && echo "a release before unit drop-ins" || echo "the machine configuration is in error")); the unit keeps its own values"
        if [ -f "$ROOT$dir/$DROPIN_NAME" ]; then rm -f -- "$ROOT$dir/$DROPIN_NAME" || fail install-units "could not remove the stale $dir/$DROPIN_NAME"; fi
        ;;
      *) fail install-units "$BIN_PATH units dropin $unit failed (exit $code)" ;;
    esac
  done
  say "unit drop-ins: $DROPIN_NAME for $UNITS, from the machine configuration"
}

apply_wsl_conf() {
  case "$WSL_CONF_ACTION" in
    keep)
      say "$WSL_CONF names the default user \"$(printable "$CURRENT_DEFAULT")\": wsl-care's per-user cleanups act for that user"
      if [ -n "$DEFAULT_USER" ] && [ "$DEFAULT_USER" != "$CURRENT_DEFAULT" ]; then
        warn "--set-default-user $DEFAULT_USER ignored: $WSL_CONF already names a default user, and it is never rewritten"
      fi
      ;;
    advise)
      say "$WSL_CONF names no default user. wsl-care then acts for the single login account (uid >= 1000) and"
      say "refuses its per-user cleanups when there are several. To name one: re-run with --set-default-user <name>,"
      say "or add [user] default=<name> to $WSL_CONF yourself. Nothing was written there."
      ;;
    write)
      if [ "$DRY_RUN" = 1 ]; then
        say "would add [user] default=$DEFAULT_USER to $WSL_CONF"
        return 0
      fi
      CHANGED=1
      next="$ROOT$WSL_CONF.wsl-care-install.$$"
      {
        if [ -s "$ROOT$WSL_CONF" ]; then
          cat "$ROOT$WSL_CONF"
          printf '\n'
        fi
        printf '[user]\ndefault=%s\n' "$DEFAULT_USER"
      } > "$next" || fail wsl-conf "could not write $next"
      chmod 0644 "$next"
      mv -f "$next" "$ROOT$WSL_CONF" || fail wsl-conf "could not replace $WSL_CONF"
      say "added [user] default=$DEFAULT_USER to $WSL_CONF (--set-default-user). WSL itself also logs in as"
      say "$DEFAULT_USER from the next start of this distro (wsl --terminate <distro> from Windows)."
      ;;
  esac
}

install_packages() {
  need=""
  have sar || need="sysstat"
  have atop || need="$need atop"
  if [ -n "$need" ] || ! sysstat_enabled; then
    # sysstat collects only when its debconf switch is on (Ubuntu ships it off): the package's own,
    # documented setting rather than an edit of its file.
    printf 'sysstat sysstat/enable boolean true\n' > "$WORK/sysstat.debconf"
    run debconf-set-selections "$WORK/sysstat.debconf" || fail packages "debconf-set-selections failed"
  fi
  case "$need" in
    "") say "sysstat and atop: already installed" ;;
    *)
      run timeout 600 apt-get update -q || fail packages "apt-get update failed"
      # No kill ceiling on the install itself: a dpkg killed mid-configure leaves a broken package
      # database, which is worse than a slow install. Its waits are bounded by apt's own lock timeout,
      # and its progress is on the terminal.
      case "$need" in
        sysstat) run apt-get install -y -q --no-install-recommends -o DPkg::Lock::Timeout=300 sysstat ;;
        " atop") run apt-get install -y -q --no-install-recommends -o DPkg::Lock::Timeout=300 atop ;;
        *) run apt-get install -y -q --no-install-recommends -o DPkg::Lock::Timeout=300 sysstat atop ;;
      esac || fail packages "apt-get install failed"
      ;;
  esac
  if [ "$DRY_RUN" = 0 ] && ! sysstat_enabled; then
    run dpkg-reconfigure -f noninteractive sysstat || fail packages "dpkg-reconfigure sysstat failed"
    sysstat_enabled || fail packages "sysstat collection is still off ($SYSSTAT_DEFAULT has no ENABLED=\"true\")"
  elif [ "$DRY_RUN" = 1 ] && ! sysstat_enabled; then
    say "would run: dpkg-reconfigure -f noninteractive sysstat (switches its collection on)"
  fi
}

enable_units() {
  run systemctl daemon-reload || fail enable-units "systemctl daemon-reload failed"
  if [ "$UPGRADE" = 1 ]; then
    # The follower keeps running the replaced binary until it restarts.
    run systemctl try-restart wsl-care-events.service || fail enable-units "systemctl try-restart wsl-care-events.service failed"
  fi
  run systemctl enable --now wsl-care.timer wsl-care-events.service \
    || fail enable-units "systemctl enable --now wsl-care.timer wsl-care-events.service failed"
  run systemctl enable --now sysstat.service atop.service \
    || fail enable-units "systemctl enable --now sysstat.service atop.service failed"
}

# Starting is not working: the installed binary records one full run, by its absolute path, as root. A
# collect started outside the timer measures and records; it never cleans. 75 = another run holds the lock
# (the timer fired first), which records just the same.
first_run() {
  if [ "$DRY_RUN" = 1 ]; then
    say "would run: $BIN_PATH collect (one full run: measure and record, no cleanup)"
    return 0
  fi
  code=0
  timeout 900 "$ROOT$BIN_PATH" collect > /dev/null || code=$?
  case "$code" in
    0 | 75) say "first full run recorded" ;;
    *) fail first-run "$BIN_PATH collect exited $code" ;;
  esac
}

healthy() {
  printf '%s\n' "$1" | grep -Eq '^  "healthy": true,?[[:space:]]*$'
}

verify() {
  if [ "$DRY_RUN" = 1 ]; then
    say "would verify: sar and atop on PATH, wsl-care.timer and wsl-care-events.service active, $BIN_PATH doctor --json healthy"
    return 0
  fi
  have sar || fail "verify: sysstat (sar on PATH)" "sar is not on PATH after installing sysstat"
  have atop || fail "verify: atop on PATH" "atop is not on PATH after installing it"
  for unit in wsl-care.timer wsl-care-events.service; do
    systemctl is-active --quiet "$unit" || fail "verify: $unit active" "systemctl is-active $unit: not active"
  done
  waited=0
  while :; do
    report=$(timeout 120 "$ROOT$BIN_PATH" doctor --json 2>/dev/null) || report=""
    if healthy "$report"; then
      break
    fi
    if [ "$waited" -ge "$DOCTOR_SECONDS" ]; then
      timeout 120 "$ROOT$BIN_PATH" doctor >&2 || true
      fail "verify: doctor healthy" "$BIN_PATH doctor --json is not healthy after ${waited}s — the checks above say why"
    fi
    sleep 5
    waited=$((waited + 5))
  done
  say "verified: sar, atop, wsl-care.timer, wsl-care-events.service, doctor healthy"
}

install_all() {
  install_preflight
  WORK=$(mktemp -d "${TMPDIR:-/tmp}/wsl-care-install.XXXXXX")
  gh_preflight
  resolve_version
  download_and_verify
  unpack
  install_files
  write_dropins
  apply_wsl_conf
  install_packages
  enable_units
  first_run
  verify
  if [ "$DRY_RUN" = 1 ]; then
    say "dry run: nothing was changed"
  else
    say "installed wsl-care $VERSION ($RID). Status: wsl-care status · health: wsl-care doctor ·"
    say "remove: curl -fsSL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo sh -s -- --uninstall"
  fi
}

if [ "$UNINSTALL" = 1 ]; then
  uninstall
else
  install_all
fi
