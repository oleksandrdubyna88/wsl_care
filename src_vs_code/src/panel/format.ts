/**
 * Units, ages and the one gate every daemon string passes before it reaches the page.
 *
 * <p>Memory is shown in binary GiB (what `/proc/meminfo` counts and what the daemon's own verdict texts say), Docker and
 * disk figures in decimal GB (what `docker system df` and `df -H` say), so a number on the panel reads the same as the
 * tool it came from (plan §1 success criterion).</p>
 */

/** The longest daemon string the page is given; longer ones end in an ellipsis. */
export const MAX_TEXT = 500;

const GIB = 1024 ** 3;

export function gib(bytes: number): string {
  return `${(bytes / GIB).toFixed(1)} GiB`;
}

export function gb(bytes: number): string {
  return `${(bytes / 1e9).toFixed(1)} GB`;
}

export function percent(value: number): string {
  return `${Number(value.toFixed(1))} %`;
}

/** How long ago, as a person says it. */
export function age(seconds: number): string {
  if (seconds < 60) {
    return 'just now';
  }
  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)} min ago`;
  }

  return seconds < 48 * 3600 ? `${(seconds / 3600).toFixed(1)} h ago` : `${Math.floor(seconds / 86400)} d ago`;
}

/**
 * C0 and C1 controls, DEL, and the bidirectional embedding / override / isolate characters. Each is replaced by a
 * visible U+FFFD: a process name is chosen by whoever started the process, and a right-to-left override in it would
 * make the panel show a different name than the one running (CVE-2021-42574's trick, here on a table cell).
 */
// eslint-disable-next-line no-control-regex -- matching control characters is the point of this pattern
const UNSAFE = /[\u0000-\u001f\u007f-\u009f\u202A-\u202E\u2066-\u2069]/g;

/** A daemon string as the page may show it: unsafe characters made visible, at most `MAX_TEXT` characters. */
export function safeText(text: string): string {
  const visible = text.replace(UNSAFE, '\uFFFD');

  return visible.length > MAX_TEXT ? `${visible.slice(0, MAX_TEXT)}…` : visible;
}
