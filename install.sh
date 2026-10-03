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
# its .sha256 beside it. Authenticity is the build-provenance attestation (`gh attestation verify`, which
# needs the GitHub CLI): it proves this repository's release workflow built exactly these bytes. Without
# `gh` the installer stops before installing anything; `--skip-attestation` proceeds knowingly, says so
# loudly, and still checks the .sha256.
#
# `sh`, not `bash`: POSIX only, no eval, every expansion quoted. It needs root and says so; it never calls
# sudo itself.
set -eu
umask 022

readonly REPO="oleksandrdubyna88/wsl_care"
# The release workflow that signs the archives (plan §15 #12, E4.S2): an attestation made by any other
# workflow of the repository is refused.
readonly SIGNER_WORKFLOW="$REPO/.github/workflows/release.yml"

readonly BIN_DIR="/opt/wsl-care/bin"
readonly BIN_PATH="$BIN_DIR/wsl-care"
readonly LINK_PATH="/usr/local/bin/wsl-care"
readonly UNIT_DIR="/etc/systemd/system"
readonly UNITS="wsl-care.service wsl-care.timer wsl-care-events.service"
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
  --skip-attestation          do not verify the build-provenance attestation (no `gh` needed); printed
                              loudly — the .sha256 integrity check still applies
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

# $1 url, $2 destination file, $3 ceiling in seconds. HTTPS only, redirects included (a release asset
# redirects to GitHub's object store); a ceiling on every transfer.
fetch() {
  curl --url "$1" --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
    --tlsv1.2 --retry 3 --connect-timeout 15 --max-time "$3" --output "$2"
}

# --- preflight, shared -------------------------------------------------------------------------------
preflight_common() {
  os=$(uname -s)
  [ "$os" = Linux ] || fail preflight "this installs into a WSL Linux distro; this is $(printable "$os")"
  if [ "$DRY_RUN" = 1 ]; then
    say "dry run: nothing will be changed, and root is not needed for it"
  elif [ "$(id -u)" != 0 ]; then
    fail preflight "run it as root — it installs units and packages. It never calls sudo itself; re-run:
  curl -fsSL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo sh -s -- $ARGS_SHOWN"
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
  for unit in $UNITS; do
    if [ -f "$ROOT$UNIT_DIR/$unit" ]; then run rm -f -- "$ROOT$UNIT_DIR/$unit"; fi
  done
  run systemctl daemon-reload || fail units "systemctl daemon-reload failed"

  if [ -h "$ROOT$LINK_PATH" ] && [ "$(readlink "$ROOT$LINK_PATH")" = "$BIN_PATH" ]; then
    run rm -f -- "$ROOT$LINK_PATH"
  elif [ -e "$ROOT$LINK_PATH" ] || [ -h "$ROOT$LINK_PATH" ]; then
    warn "left $LINK_PATH alone: it is not the link this installer makes"
  fi
  if [ -e "$ROOT$BIN_PATH" ]; then run rm -f -- "$ROOT$BIN_PATH"; fi
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
  if [ "$SKIP_ATTESTATION" = 0 ]; then
    have gh || fail preflight "the GitHub CLI (gh) is not installed, and it is what verifies that this
repository's release workflow built the archive (its build-provenance attestation). Nothing was installed.
Either install gh (https://cli.github.com — on Ubuntu: sudo apt-get install gh), log in (gh auth login), and
run this again; or proceed knowingly WITHOUT the attestation (the .sha256 integrity check still applies):
  curl -fsSL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo sh -s -- --skip-attestation"
    if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then
      matches "$SUDO_USER" "$USER_PATTERN" || fail preflight "SUDO_USER is not a user name: $(printable "$SUDO_USER")"
      have runuser || fail preflight "runuser is not installed (gh runs as $SUDO_USER, whose gh login it uses)"
    fi
  fi
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

resolve_version() {
  if [ -n "$VERSION" ]; then
    return 0
  fi
  # The newest `daemon-v*` release, NOT releases/latest: this repository releases the daemon and the
  # VS Code extension, and "latest" is usually the extension's .vsix. The API lists newest first.
  api="https://api.github.com/repos/$REPO/releases?per_page=100"
  fetch "$api" "$WORK/releases.json" 60 || fail resolve-release "could not read $api"
  sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"daemon-v\([^"]*\)".*/\1/p' "$WORK/releases.json" > "$WORK/daemon-tags"
  while IFS= read -r candidate; do
    if matches "$candidate" "$VERSION_PATTERN"; then
      VERSION=$candidate
      return 0
    fi
  done < "$WORK/daemon-tags"
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
  if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then
    # gh uses the login of the person who ran sudo; root usually has none. The folder is made readable
    # to them; it stays root's, so they cannot change the archive between the check and the install.
    chmod 0755 "$WORK"
    timeout 180 runuser -u "$SUDO_USER" -- gh attestation verify --repo "$REPO" --signer-workflow "$SIGNER_WORKFLOW" "$WORK/$ARCHIVE" \
      || fail attestation "gh attestation verify (as $SUDO_USER) refused $ARCHIVE — nothing is installed. If gh is not logged in, run gh auth login first."
  else
    timeout 180 gh attestation verify --repo "$REPO" --signer-workflow "$SIGNER_WORKFLOW" "$WORK/$ARCHIVE" \
      || fail attestation "gh attestation verify refused $ARCHIVE — nothing is installed. If gh is not logged in, run gh auth login first."
  fi
  say "attestation ok: built by $SIGNER_WORKFLOW (authenticity)"
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
  for file in wsl-care systemd/wsl-care.service systemd/wsl-care.timer systemd/wsl-care-events.service config/machine.json; do
    [ -f "$SRC/$file" ] && [ ! -h "$SRC/$file" ] || fail unpack "$ARCHIVE has no $NAME/$file"
  done
}

install_files() {
  UPGRADE=0
  if [ -e "$ROOT$BIN_PATH" ]; then UPGRADE=1; fi
  run install -d -m 0755 "$ROOT/opt/wsl-care" "$ROOT$BIN_DIR" "$ROOT/usr/local/bin" \
    || fail install-binary "could not create $BIN_DIR"
  run install -m 0755 "$SRC/wsl-care" "$ROOT$BIN_PATH" || fail install-binary "could not install $BIN_PATH"
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
  resolve_version
  download_and_verify
  unpack
  install_files
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
