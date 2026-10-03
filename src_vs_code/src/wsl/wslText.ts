/**
 * Reading what `wsl.exe` and the program behind it print (measured 2026-10-03, WSL 2.7.10.0,
 * research/2026-10-03_wsl_exe_facts.md):
 *
 * - `wsl.exe`'s OWN output — `--list --quiet`, `-l -v`, `--list --running --quiet`, its refusals — is UTF-16LE with
 *   CRLF and no byte-order mark (`--list --quiet` begins `55 00 62 00`); with `WSL_UTF8=1` in the environment it is
 *   UTF-8 instead (measured for `--list --quiet`).
 * - The Linux program's stdout and stderr pass through as its own bytes: UTF-8 (`printf 'café ж'` arrived as
 *   `63 61 66 c3 a9 20 d0 b6`); the relay's `execvpe` refusal is UTF-8 too.
 *
 * So the decision is made from the bytes, not from which call it was: a NUL at an odd offset only appears in UTF-16LE
 * text (every line ends in `0d 00 0a 00`), never in the UTF-8 the daemon writes.
 */

/** Whether `bytes` is UTF-16LE text: a byte-order mark, or a NUL in an odd position. */
function isUtf16(bytes: Buffer): boolean {
  return hasUtf16Mark(bytes) || (bytes.length % 2 === 0 && bytes.some((byte, index) => byte === 0 && index % 2 === 1));
}

function hasUtf16Mark(bytes: Buffer): boolean {
  return bytes[0] === 0xff && bytes[1] === 0xfe;
}

export function decodeWslText(bytes: Buffer): string {
  if (!isUtf16(bytes)) {
    return bytes.toString('utf8');
  }
  const text = bytes.toString('utf16le');

  return text.startsWith('﻿') ? text.slice(1) : text;
}

/** The non-blank lines of `text`, split on CRLF or LF, each without trailing whitespace. */
export function textLines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trimEnd()).filter((line) => line.trim().length > 0);
}

/** ANSI escape sequences: CSI (`ESC [ … final`) and OSC (`ESC ] … BEL` or `ESC \`). */
// eslint-disable-next-line no-control-regex -- an ANSI sequence starts with ESC (and an OSC ends with BEL) by definition
const ANSI = /\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)/g;

/** `text` without colour or cursor sequences — the daemon's console sink colours its levels (logging rule). */
export function stripAnsi(text: string): string {
  return text.replace(ANSI, '');
}
