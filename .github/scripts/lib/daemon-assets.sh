# shellcheck shell=bash
# The two constants are read by the scripts that source this file, which shellcheck cannot see from here.
# shellcheck disable=SC2034
# The daemon release's asset contract, in ONE place (plan §9, §15e #1/#2; E4.S2). Sourced by package-daemon.sh (which
# makes the assets), verify-release-assets.sh (which checks a release holds exactly them) and release-guard.sh (the tag
# shape). release.yml's matrix, ci-daemon.yml's matrix and install.sh's architectures are held equal to DAEMON_RIDS by
# ReleaseWorkflowTests, so a RID added in one place and forgotten in another is a red build, not a release that ships
# two of its three platforms.

# Every RID a daemon release ships, in matrix order. Native AOT does not cross-compile: each is built on a runner of its
# own kind (release.yml).
readonly DAEMON_RIDS="linux-x64 linux-arm64 win-x64"

# A release version: what install.sh accepts for --version (its VERSION_PATTERN — ReleaseWorkflowTests holds the two
# spellings equal), so the guard never admits a tag the installer would refuse to install.
readonly DAEMON_VERSION_PATTERN='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$'

# The archive file name of one RID: a tarball for the distro (what install.sh downloads), a zip for the Windows probe
# (what the extension bundles, E5). An unknown RID is refused, never mapped by a catch-all: a platform nobody built for
# must not inherit another's shape.
daemon_archive_name() {
  case "$2" in
    linux-x64 | linux-arm64) printf 'wsl-care-%s-%s.tar.gz\n' "$1" "$2" ;;
    win-x64) printf 'wsl-care-%s-%s.zip\n' "$1" "$2" ;;
    *) return 1 ;;
  esac
}

# The single top folder every archive holds its files under.
daemon_archive_folder() {
  printf 'wsl-care-%s-%s\n' "$1" "$2"
}
