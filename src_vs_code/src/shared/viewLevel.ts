/** How a line of the panel is levelled — in its own module so a part of the panel (the Windows tasks') can name it without importing the panel's view model, whose imports reach every other part. */
export type ViewLevel = 'none' | 'ok' | 'warn' | 'critical' | 'unknown';
