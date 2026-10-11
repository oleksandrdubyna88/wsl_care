# shellcheck shell=sh
# The constants are read by the scripts and workflow steps that source this file, which shellcheck cannot see from here.
# shellcheck disable=SC2034
# The extension package's file name in ONE POSIX function (E7.S5a, research/PLAN_bundle_windows_binary.md §3.3). From the release
# that carries the bundled wsl-care.exe on, the package is TARGETED — `vsce package --target win32-x64` — and vsce 4.0.0 names a
# targeted package `<name>-<target>-<version>.vsix` (@vscode/vsce/out/package.js). Sourced by release-extension.yml's steps,
# verify-extension-assets.sh and POST_DEPLOY.md item 6 (which runs under sh). The C# twin is ReleaseFiles.ExtensionVsix and the
# Node one check-vsix.mjs's default name (`--print-default-name`): ONE cross-language test asks all three for the same version
# and requires the same name, and holds EXTENSION_NAME equal to src_vs_code/package.json's `name`.

# The extension's permanent id — src_vs_code/package.json `name`.
EXTENSION_NAME='ai-os-care'

# The one platform the package targets (owner decision A1; Q-B1, 2026-10-10: no universal package — Linux, macOS and Windows
# on Arm keep the last universal 0.3.1 on the Marketplace).
EXTENSION_TARGET='win32-x64'

# vsix_name <version>: the package file of that release, `ai-os-care-win32-x64-<version>.vsix`.
vsix_name() {
  printf '%s-%s-%s.vsix\n' "$EXTENSION_NAME" "$EXTENSION_TARGET" "$1"
}

# vsix_universal_name <version>: the name the package carried BEFORE it was targeted — refused from E7.S5a on, named only
# so a refusal can say what it saw.
vsix_universal_name() {
  printf '%s-%s.vsix\n' "$EXTENSION_NAME" "$1"
}
