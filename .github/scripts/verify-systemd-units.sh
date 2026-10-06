#!/usr/bin/env bash
# The systemd units a release ships, read by systemd's own parser exactly as install.sh installs them (daemon 0.1.1):
# EVERY file in the units folder — never a list typed here — each template (`name@.service`) as an INSTANCE, and, when a
# built binary is given, each unit's drop-in from `wsl-care units dropin <unit>` beside it, the way install.sh writes
# /etc/systemd/system/<unit>.d/50-wsl-care-config.conf.
#
#   verify-systemd-units.sh <units-dir> [<wsl-care binary>]
#
# Why every file and an instance: until 0.1.1 ci-daemon.yml named three units by hand, and the detached-run template
# (wsl-care-act@.service, added later) was never read. Its `CollectMode=` sat under [Service], where systemd 255 says
# "Unknown key name 'CollectMode' in section 'Service', ignoring." — and daemon 0.1.0 shipped with a failed detached run
# that lingers in `systemctl --failed`. A template is verified through an instance name, so `%i` is expanded as systemd
# would expand it at `systemctl start`; systemd finds the template beside it (and reads its `<template>.d/` drop-ins).
#
# `systemd-analyze verify` EXITS 0 on an unknown key or section and only prints it (systemd 255), so ANY output fails:
# exit 1 with the output, exit 0 only when systemd had nothing to say. It also checks that each ExecStart program exists,
# so the caller puts an executable at the install path first (CI: a stand-in on a throwaway runner). Exit 2 on a bad
# argument.
set -euo pipefail

[ "$#" -ge 1 ] && [ "$#" -le 2 ] || { echo "usage: verify-systemd-units.sh <units-dir> [<wsl-care binary>]" >&2; exit 2; }
units_dir="$1"
binary="${2:-}"
[ -d "$units_dir" ] || { echo "verify-systemd-units.sh: no folder $units_dir" >&2; exit 2; }
[ -z "$binary" ] || [ -x "$binary" ] || { echo "verify-systemd-units.sh: not an executable: $binary" >&2; exit 2; }
command -v systemd-analyze > /dev/null || { echo "verify-systemd-units.sh: systemd-analyze is not on PATH" >&2; exit 2; }

# The name install.sh gives every drop-in (its DROPIN_NAME); systemd reads any *.conf in <unit>.d/, so the name only
# has to be the installed one for the output to name the same file a person would look at.
readonly DROPIN_NAME="50-wsl-care-config.conf"
# An instance name of the run-id shape the CLI validates (yyyyMMddTHHmmssZ-pid) — the only instances the daemon starts.
readonly INSTANCE="20000101T000000Z-1"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
# The unit search folder holds unit files and their <unit>.d/ drop-ins and nothing else; what the CLI prints on stderr
# is kept beside it, never in it.
units="$work/units"
mkdir "$units"

targets=()
names=()
for path in "$units_dir"/*; do
  [ -f "$path" ] || continue
  name="$(basename -- "$path")"
  # 0644 as install.sh installs them: a copy that kept an executable bit makes systemd print a warning of its own.
  install -m 0644 "$path" "$units/$name"
  if [ -n "$binary" ]; then
    mkdir "$units/$name.d"
    # The CLI logs every request to stderr: kept aside, shown only when the render fails.
    timeout 60 "$binary" units dropin "$name" > "$units/$name.d/$DROPIN_NAME" 2> "$work/dropin.err" \
      || { cat "$work/dropin.err"; echo "::error::$binary units dropin $name failed — install.sh would refuse this unit too"; exit 1; }
    [ -s "$units/$name.d/$DROPIN_NAME" ] || { echo "::error::$binary units dropin $name answered nothing"; exit 1; }
  fi
  case "$name" in
    *@.*) targets+=("$units/${name%%@.*}@$INSTANCE.${name##*@.}") ;;
    *) targets+=("$units/$name") ;;
  esac
  names+=("$name")
done
[ "${#targets[@]}" -gt 0 ] || { echo "::error::no unit files in $units_dir"; exit 1; }

code=0
# SYSTEMD_LOG_LEVEL unset: a level of err in the environment would hide the very warning this gate exists for.
out="$(env -u SYSTEMD_LOG_LEVEL systemd-analyze verify "${targets[@]}" 2>&1)" || code=$?
# The output names the temporary copies; name the shipped file instead (a drop-in is the one rendered for that unit).
out="${out//$units\//$units_dir/}"
if [ "$code" -ne 0 ]; then
  printf '%s\n' "$out"
  echo "::error::systemd-analyze verify failed (exit $code)"
  exit 1
fi
if [ -n "$out" ]; then
  printf '%s\n' "$out"
  echo "::error::systemd-analyze verify reported something about the units (it exits 0 on an unknown key)"
  exit 1
fi
echo "systemd-analyze verify: nothing to report for ${names[*]}${binary:+, each with its drop-in}"
