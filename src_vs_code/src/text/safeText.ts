/**
 * The one sanitiser for text that arrives from outside and is shown to a person — a daemon answer's process name, an
 * unknown enum value, a refusal's message (E5.S2's panel rule, extracted for the root paths in E6.S2 so there is ONE copy).
 * C0 and C1 controls, DEL, and the bidirectional embedding / override / isolate characters are each replaced by a visible
 * U+FFFD: a process name is chosen by whoever started the process, and a right-to-left override in it would make a person
 * read a different name than the one running (CVE-2021-42574's trick). Then the text is clipped to `max` characters, and
 * says it was.
 */

// eslint-disable-next-line no-control-regex -- matching control characters is the point of this pattern
const UNSAFE = /[\u0000-\u001f\u007f-\u009f\u202A-\u202E\u2066-\u2069]/g;

export function safeText(text: string, max: number): string {
  const visible = text.replace(UNSAFE, '\uFFFD');

  return visible.length > max ? `${visible.slice(0, max)}…` : visible;
}
