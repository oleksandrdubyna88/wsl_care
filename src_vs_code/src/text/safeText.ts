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

/**
 * The characters that are INVISIBLE rather than controls (E6.S3 review A1): the line and paragraph separators, the
 * left-to-right / right-to-left / Arabic letter marks, the zero-width space / non-joiner / joiner, the word joiner and the
 * byte-order mark. Built from their code points, so this source carries none of them.
 */
const INVISIBLE = new RegExp(`[${[0x2028, 0x2029, 0x200e, 0x200f, 0x061c, 0x200b, 0x200c, 0x200d, 0x2060, 0xfeff].map((c) => String.fromCharCode(c)).join('')}]`, 'g');

const REPLACEMENT = String.fromCharCode(0xfffd);

export function safeText(text: string, max: number): string {
  const visible = text.replace(UNSAFE, REPLACEMENT).replace(INVISIBLE, REPLACEMENT);

  return visible.length > max ? `${visible.slice(0, max)}…` : visible;
}

/** The longest sentence a notification carries. */
const NOTICE_MAX = 1_000;

/**
 * Text headed for a VS Code NOTIFICATION or modal (E6.S3 review A1). A notification renders markdown links, so
 * `[label](command:id)` in a daemon's message would become a clickable command: every `](` is broken into `] (`, which no
 * renderer reads as a link — after the sanitiser above. The ONE road: `cleanupHost.ts` wraps every cleanup surface in it,
 * and *Install daemon*'s report takes it too.
 */
export function noticeText(text: string): string {
  return unlinked(safeText(text, NOTICE_MAX));
}

/** Markdown link syntax broken — for a modal's text, which `modalText.ts` has already made printable and short. */
export function unlinked(text: string): string {
  return text.split('](').join('] (');
}
