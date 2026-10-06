#!/usr/bin/env bash
# The installed extension is the attested build, file by file (the manual Marketplace upload, docs/repo-settings.md
# step 9; POST_DEPLOY.md item 6). The Marketplace's "served" answer names a VERSION only, so the owner compares what
# VS Code installed from the Marketplace with the attested .vsix BEFORE approving the release, and item 6 repeats it
# after the release.
#
#   compare-installed-extension.sh <attested.vsix> <installed extension folder>
#
# Every file under the .vsix's `extension/` must exist in the folder with the same bytes, and the folder may hold
# nothing else. Two differences are VS Code's own and are allowed, as measured on an installed Marketplace extension
# (2026-10-06): it adds `.vsixmanifest`, and it writes an `__metadata` object into the installed package.json — so
# package.json is compared as JSON with `__metadata` removed. Python 3's zipfile reads the .vsix: stock Ubuntu has
# python3 and no zip extractor.
#
# Exit 0 and one line when they are the same; 1 naming every difference; 2 on usage or an unreadable input.
set -euo pipefail

[ "$#" -eq 2 ] || { echo "usage: compare-installed-extension.sh <attested.vsix> <installed extension folder>" >&2; exit 2; }
[ -f "$1" ] || { echo "compare-installed-extension: $1 is not a file" >&2; exit 2; }
[ -d "$2" ] || { echo "compare-installed-extension: $2 is not a folder" >&2; exit 2; }

exec python3 -c '
import json, os, sys, zipfile, zlib

vsix, folder = sys.argv[1], sys.argv[2]
PREFIX = "extension/"
VS_CODE_ADDS = {".vsixmanifest"}

try:
    archive = zipfile.ZipFile(vsix)
except (OSError, zipfile.BadZipFile) as error:
    print(f"compare-installed-extension: {vsix} is not a readable .vsix: {error}", file=sys.stderr)
    sys.exit(2)

attested = {}
try:
    for info in archive.infolist():
        if info.filename.startswith(PREFIX) and not info.is_dir():
            attested[info.filename[len(PREFIX):]] = archive.read(info)
except (OSError, EOFError, RuntimeError, NotImplementedError, ValueError, zipfile.BadZipFile, zlib.error) as error:
    # An archive that opens but whose entry does not read (a bad CRC, a truncated or encrypted entry, an unknown
    # compression) is unreadable too: exit 2, never 1 - a "difference" - with a traceback.
    print(f"compare-installed-extension: {vsix} is not a readable .vsix: {error}", file=sys.stderr)
    sys.exit(2)
if not attested:
    print(f"compare-installed-extension: {vsix} holds no extension/ files", file=sys.stderr)
    sys.exit(2)

installed = set()
for directory, _, files in os.walk(folder):
    for name in files:
        installed.add(os.path.relpath(os.path.join(directory, name), folder).replace(os.sep, "/"))

def package_json(data):
    value = json.loads(data)
    if isinstance(value, dict):
        value.pop("__metadata", None)
    return value

findings = []
for name in sorted(attested):
    path = os.path.join(folder, *name.split("/"))
    if name not in installed:
        findings.append(f"{name}: missing from the installed folder")
        continue
    with open(path, "rb") as handle:
        data = handle.read()
    if name == "package.json":
        try:
            same = package_json(data) == package_json(attested[name])
        except ValueError:
            same = False
        if not same:
            findings.append("package.json: differs from the attested one (compared without VS Code'"'"'s __metadata)")
    elif data != attested[name]:
        findings.append(f"{name}: different bytes")
for name in sorted(installed - set(attested) - VS_CODE_ADDS):
    findings.append(f"{name}: in the installed folder, not in the attested .vsix")

if findings:
    print(f"compare-installed-extension: the installed extension is NOT the attested build - {len(findings)} difference(s):")
    for finding in findings:
        print(f"  - {finding}")
    sys.exit(1)
print(f"compare-installed-extension: the installed extension is the attested build ({len(attested)} files)")
' "$1" "$2"
