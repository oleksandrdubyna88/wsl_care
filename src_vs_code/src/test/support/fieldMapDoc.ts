import { FIELD_MAP, isArriving, refreshOf, SECTIONS, type FieldRow } from '../../panel/fieldMap';

/**
 * The field map as the Markdown table research/architecture.md carries between its two markers — generated from
 * `FIELD_MAP`, never typed by hand. `fieldMap.test.ts` holds the document equal to it; `npm run fieldmap:doc`
 * (`scripts/field-map-doc.mjs`) rewrites the block from it.
 */

export const BEGIN = '<!-- field-map:begin (generated from src_vs_code/src/panel/fieldMap.ts by npm run fieldmap:doc) -->';
export const END = '<!-- field-map:end -->';

const BACKTICK = String.fromCharCode(96);

function code(text: string): string {
  return BACKTICK + text + BACKTICK;
}

const VERB_COMMAND: Readonly<Record<'status' | 'preview' | 'doctor', string>> = {
  status: 'status --json',
  preview: 'preview --all --json',
  doctor: 'doctor --json',
};

const REFRESH_TEXT: Readonly<Record<'poll' | 'panel', string>> = {
  poll: 'status poll (focused window) + panel open / Refresh',
  panel: 'panel open / Refresh',
};

function titleOf(row: FieldRow): string {
  return SECTIONS.find((s) => s.id === row.section)?.title ?? row.section;
}

function line(row: FieldRow): string {
  if (isArriving(row)) {
    return `| ${titleOf(row)} | ${row.label} | — | — | — | arrives in ${row.arrives} — ${row.why} |`;
  }

  return `| ${titleOf(row)} | ${row.label} | ${code(row.path)} | ${code(VERB_COMMAND[row.verb])} | ${REFRESH_TEXT[refreshOf(row.verb)]} | yes |`;
}

/** The table, with its markers, exactly as the document must hold it. */
export function fieldMapMarkdown(): string {
  return [
    BEGIN,
    '| Section | Row | JSON path | Verb | Refresh trigger | In E5 |',
    '|---|---|---|---|---|---|',
    ...FIELD_MAP.map(line),
    END,
  ].join('\n');
}
