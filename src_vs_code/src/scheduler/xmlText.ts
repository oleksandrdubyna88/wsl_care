const XML_ESCAPES: Readonly<Record<string, string>> = { '&': '&amp;', '<': '&lt;', '>': '&gt;' };

/**
 * Text content for an XML element. In the peer (`src/scheduler/`) since WR-E3: the guard builds its own task XML with it and the Windows
 * tasks' guards compare the daemon's XML against it, and neither consumer may import the other (`schedulerModule.test.ts`).
 */
export function xmlText(text: string): string {
  return text.replace(/[&<>]/g, (c) => XML_ESCAPES[c] ?? c);
}
