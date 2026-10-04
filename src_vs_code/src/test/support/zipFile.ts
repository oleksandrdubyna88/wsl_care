import { deflateRawSync, inflateRawSync } from 'node:zlib';

/**
 * A ZIP reader for the one archive this repository inspects — the packaged `.vsix` (plan §15g M8, *Verify the
 * ARTEFACT*) — and a writer for the tests that plant content into one. No dependency and no `unzip` on `PATH`: the
 * Windows and Linux CI legs both read the `.vsix` through the same code.
 *
 * The reader walks the END OF CENTRAL DIRECTORY record → the central directory → each entry's local header, and
 * inflates `stored` (0) and `deflate` (8) entries — what vsce's yazl writes. It REFUSES what it does not understand
 * rather than guessing: ZIP64, encryption, another compression method, a size that does not match, a duplicate name, an
 * absolute or `..` path. A refusal is an exception naming the entry — a check must not pass over an archive it could not
 * read.
 */

const EOCD = 0x06054b50;
const CENTRAL = 0x02014b50;
const LOCAL = 0x04034b50;

function eocdOffset(zip: Buffer): number {
  for (let at = zip.length - 22; at >= Math.max(0, zip.length - 22 - 0xffff); at--) {
    if (zip.readUInt32LE(at) === EOCD) {
      return at;
    }
  }
  throw new Error('zip: no end-of-central-directory record (not a ZIP, or truncated)');
}

interface CentralEntry {
  readonly name: string;
  readonly method: number;
  readonly flags: number;
  readonly compressed: number;
  readonly size: number;
  readonly localOffset: number;
  readonly next: number;
}

function centralEntry(zip: Buffer, at: number): CentralEntry {
  if (zip.readUInt32LE(at) !== CENTRAL) {
    throw new Error(`zip: no central directory header at ${at}`);
  }
  const nameLength = zip.readUInt16LE(at + 28);
  const extra = zip.readUInt16LE(at + 30);
  const comment = zip.readUInt16LE(at + 32);

  return {
    name: zip.toString('utf8', at + 46, at + 46 + nameLength),
    flags: zip.readUInt16LE(at + 8),
    method: zip.readUInt16LE(at + 10),
    compressed: zip.readUInt32LE(at + 20),
    size: zip.readUInt32LE(at + 24),
    localOffset: zip.readUInt32LE(at + 42),
    next: at + 46 + nameLength + extra + comment,
  };
}

function checkedName(entry: CentralEntry): string {
  const name = entry.name;
  if (name.startsWith('/') || name.includes('\\') || name.split('/').includes('..')) {
    throw new Error(`zip: the entry name "${name}" is absolute or climbs out`);
  }
  if ((entry.flags & 1) !== 0) {
    throw new Error(`zip: "${name}" is encrypted`);
  }
  if (entry.compressed === 0xffffffff || entry.size === 0xffffffff || entry.localOffset === 0xffffffff) {
    throw new Error(`zip: "${name}" needs ZIP64`);
  }

  return name;
}

function entryData(zip: Buffer, entry: CentralEntry): Buffer {
  const at = entry.localOffset;
  if (zip.readUInt32LE(at) !== LOCAL) {
    throw new Error(`zip: no local header for "${entry.name}"`);
  }
  const start = at + 30 + zip.readUInt16LE(at + 26) + zip.readUInt16LE(at + 28);
  const raw = zip.subarray(start, start + entry.compressed);
  const data = entry.method === 0 ? Buffer.from(raw) : entry.method === 8 ? inflateRawSync(raw) : undefined;
  if (data === undefined) {
    throw new Error(`zip: "${entry.name}" uses compression method ${entry.method}`);
  }
  if (data.length !== entry.size) {
    throw new Error(`zip: "${entry.name}" inflates to ${data.length} bytes, its header says ${entry.size}`);
  }

  return data;
}

/** Every FILE entry of the archive by name, in central-directory order (directory entries are skipped). */
export function readZip(zip: Buffer): Map<string, Buffer> {
  const end = eocdOffset(zip);
  const count = zip.readUInt16LE(end + 10);
  const entries = new Map<string, Buffer>();
  let at = zip.readUInt32LE(end + 16);
  for (let i = 0; i < count; i++) {
    const entry = centralEntry(zip, at);
    const name = checkedName(entry);
    if (entries.has(name)) {
      throw new Error(`zip: the entry "${name}" appears twice`);
    }
    if (!name.endsWith('/')) {
      entries.set(name, entryData(zip, entry));
    }
    at = entry.next;
  }

  return entries;
}

function header(signature: number, size: number): Buffer {
  const buffer = Buffer.alloc(size);
  buffer.writeUInt32LE(signature, 0);
  return buffer;
}

/** A minimal ZIP of `input` — pairs, so a test can write the same name twice (deflated, no CRC — the reader above does not check it; for planting in tests only). */
export function writeZip(input: Iterable<readonly [string, Buffer]>): Buffer {
  const files = [...input];
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const [name, data] of files) {
    const nameBytes = Buffer.from(name, 'utf8');
    const packed = deflateRawSync(data);
    const local = header(LOCAL, 30);
    local.writeUInt16LE(8, 8);
    local.writeUInt32LE(packed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    const central = header(CENTRAL, 46);
    central.writeUInt16LE(8, 10);
    central.writeUInt32LE(packed.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, packed);
    centrals.push(central, nameBytes);
    offset += 30 + nameBytes.length + packed.length;
  }
  const directory = Buffer.concat(centrals);
  const end = header(EOCD, 22);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);

  return Buffer.concat([...locals, directory, end]);
}
