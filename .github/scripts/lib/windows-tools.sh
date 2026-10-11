#!/usr/bin/env bash
# The two tools the scripts that read a zip share (E7.S5a; own Fable review of the code, finding 3 — one copy, not one per script):
# fetch-windows-daemon.sh and verify-bundled-exe.sh source this file.

# A path as a Windows program reads it: unchanged off Windows, `cygpath -m` under Git for Windows' bash.
native() {
  if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi
}

# The first python that can read a zip: python3, else python (a Windows runner may name it so; the Store's python3 alias
# exits non-zero without Python, and is skipped). Every caller runs it with -I (isolated: no current directory or script
# folder on sys.path, no PYTHONPATH, no user site), so a zipfile.py or hashlib.py beside the work cannot answer (finding 2).
python_cmd() {
  local candidate
  for candidate in python3 python; do
    if "$candidate" -I -c 'import zipfile' > /dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}
