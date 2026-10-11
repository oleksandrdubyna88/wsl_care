"""The archive-entry name rule the two zip readers share: fetch-windows-daemon.sh (the daemon's zip) and
verify-bundled-exe.sh (the extension's .vsix). One copy (E7.S5a, coai code round), so the fetch and the attest job can never
disagree about which names are safe. Both run python with -I and put this folder on sys.path themselves."""


def unsafe(name):
    """A name no extractor may be trusted with: a leading "/", a backslash, a colon, or an empty, "." or ".." segment.
    Read the name AS STORED (ZipInfo.orig_filename): zipfile turns a backslash into "/" when it reads on Windows."""
    parts = name.rstrip("/").split("/")
    return name.startswith("/") or "\\" in name or ":" in name or any(part in ("", ".", "..") for part in parts)
