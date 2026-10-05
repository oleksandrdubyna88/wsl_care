/**
 * The cleanup rows the panel offers a button for (E6.S3, plan §15j M8) — a COMPILED, closed enum: the page may send only
 * these, by value, and the host maps each to the action of the same id. They are the rows `preview --all` reports
 * (`CleanupPreview.Build`: A4, A5, A5Testcontainers, A6, A6Unused, A7, A8, A9); `rowIds.test.ts` holds this list equal to
 * the golden's rows and every member to the compiled action registry. A1–A3 and A10–A12, which §7.2 lists as "further
 * rows", have no preview row, so they have no button here.
 *
 * <p>Deliberately not imported from `root/`: the panel's message validator (`panel/messages.ts`) reads it, and no panel
 * module may import anything under `src/root/` (`structure.test.ts`).</p>
 */
export const ROW_IDS = ['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7', 'A8', 'A9'] as const;

export type RowId = (typeof ROW_IDS)[number];

export function rowIdOf(value: unknown): RowId | undefined {
  return ROW_IDS.find((id) => id === value);
}
