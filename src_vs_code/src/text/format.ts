/**
 * The formats several views share (E6.S3 review, coai #8 / #12 / #14; widened in E6.S4) — defined ONCE: decimal gigabytes,
 * as `docker system df` and `df -H` print them; binary GiB for memory, as `/proc/meminfo` counts it; a percentage; an
 * instant to the minute in UTC (the clock the daemon's records use); an instant to the minute in the LOCAL zone, with its
 * offset (the Logs page's local days, `common.utc-timestamps` rule 4: converted at the presentation edge only); and a
 * daemon metric in its own unit.
 */

export function gb(bytes: number): string {
  return `${(bytes / 1e9).toFixed(1)} GB`;
}

/** The decimal units a small amount is read in — largest first. */
const DECIMAL_UNITS: readonly (readonly [number, string])[] = [[1e9, 'GB'], [1e6, 'MB'], [1e3, 'kB']];

/**
 * A byte count readable at ANY size (E10.S1b code round #2): decimal like `gb`, in the largest unit it reaches — 200 bytes read
 * "200 B", never an empty-looking "0.0 GB". For the archive's backlog, which is small far more often than a Docker cache.
 */
export function sizeText(bytes: number): string {
  const unit = DECIMAL_UNITS.find(([size]) => bytes >= size);

  return unit === undefined ? `${bytes} B` : `${(bytes / unit[0]).toFixed(1)} ${unit[1]}`;
}

const GIB = 1024 ** 3;

export function gib(bytes: number): string {
  return `${(bytes / GIB).toFixed(1)} GiB`;
}

export function percent(value: number): string {
  return `${Number(value.toFixed(1))} %`;
}

/** `2026-10-05 10:00 UTC` — or `fallback` when the text is not an instant. */
export function minuteOf(instant: string, fallback = 'an unknown time'): string {
  const ms = Date.parse(instant);

  return Number.isFinite(ms) ? `${new Date(ms).toISOString().slice(0, 16).replace('T', ' ')} UTC` : fallback;
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

/** `UTC+03:00`, `UTC-04:00`, `UTC+05:30` — the machine's offset AT that instant. */
function offsetOf(at: Date): string {
  const east = -at.getTimezoneOffset();
  const size = Math.abs(east);

  return `UTC${east < 0 ? '-' : '+'}${pad(Math.floor(size / 60))}:${pad(size % 60)}`;
}

/** `2026-10-02 02:59 (UTC+03:00)` in the machine's zone — or `fallback` when the text is not an instant. */
export function localMinuteOf(instant: string, fallback = 'an unknown time'): string {
  const ms = Date.parse(instant);
  if (!Number.isFinite(ms)) {
    return fallback;
  }
  const at = new Date(ms);

  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ${pad(at.getHours())}:${pad(at.getMinutes())} (${offsetOf(at)})`;
}

/** The daemon's byte metrics that measure MEMORY (`/proc/meminfo`) — shown in GiB; every other byte metric in GB. */
const MEMORY_METRICS: ReadonlySet<string> = new Set(['memAvailableBytes', 'pageCacheBytes', 'swapUsedBytes']);

/** The units the daemon's `logs` metrics carry, each as this extension spells it. */
const UNITS: Readonly<Record<string, (name: string, value: number) => string>> = {
  '%': (_name, value) => percent(value),
  bytes: (name, value) => (MEMORY_METRICS.has(name) ? gib(value) : gb(value)),
  starts: (_name, value) => `${value} starts`,
};

/** A metric's value in its unit — a unit this build does not know reads as the number and the unit as answered. */
export function metricText(name: string, unit: string, value: number): string {
  const show = Object.hasOwn(UNITS, unit) ? UNITS[unit] : undefined;

  return show === undefined ? `${value} ${unit}` : show(name, value);
}
