import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';

/**
 * The SHA-256 of a file's bytes as 64 lower-case hex digits, streamed from disk (the bundled exe is 13.7 MB). The Windows client's
 * default way to learn the digest of the exe it asks to stage (PLAN_windows_runner.md §3.7): a read, never a process.
 */
export async function sha256OfFile(file: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) {
    hash.update(chunk as Buffer);
  }

  return hash.digest('hex');
}
