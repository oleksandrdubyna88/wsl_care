// §15h #3 (2026-10-04): does killing wsl.exe end a running `wsl-care preview --all --json` AND the docker CLI child it
// is waiting on? Read-only on the machine: start wsl.exe -d Ubuntu --cd / --exec <binary> preview --all --json the way
// the extension's runner does (stdin ignored by default, stdout/stderr piped, shell: false), kill ONLY that wsl.exe pid
// (child.kill() — never by image name), then ask ps inside the distro what is left.
//
// Run: node research/diagnostics/wsl_exe_kill_preview.mjs <linux path of a wsl-care build> [kill-after-ms] [ignore|pipe]
// (from Git Bash set MSYS_NO_PATHCONV=1, or the /tmp path is rewritten into a Windows one before node sees it).
import { spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';

const WSL = join(process.env.SystemRoot, 'System32', 'wsl.exe');
const binary = process.argv[2];
const killAfterMs = Number(process.argv[3] ?? 1500);
const stdinMode = process.argv[4] ?? 'ignore';
if (!binary || !binary.startsWith('/')) {
  console.error('usage: wsl_exe_kill_preview.mjs </linux/path/to/wsl-care> [kill-after-ms] [ignore|pipe]');
  process.exit(2);
}
const ps = () => spawnSync(WSL, ['-d', 'Ubuntu', '--cd', '/', '--exec', 'ps', '-eo', 'pid,ppid,pgid,sid,stat,etimes,args'], { timeout: 20000 })
  .stdout.toString('utf8').split('\n')
  .filter((l) => l.includes(binary) || /\/usr\/bin\/docker\b/.test(l));
const child = spawn(WSL, ['-d', 'Ubuntu', '--cd', '/', '--exec', binary, 'preview', '--all', '--json'], { shell: false, windowsHide: true, stdio: [stdinMode, 'pipe', 'pipe'] });
console.log(`started wsl.exe pid ${child.pid} (stdin ${stdinMode}) at ${new Date().toISOString()}; killing it after ${killAfterMs} ms`);
await new Promise((r) => setTimeout(r, killAfterMs));
console.log(`before kill, in distro:\n${ps().join('\n') || '(none)'}`);
const exited = new Promise((r) => child.on('exit', (code, signal) => r({ code, signal })));
console.log(`child.kill() -> ${child.kill()}`);
console.log(`wsl.exe exit: ${JSON.stringify(await exited)}`);
for (const wait of [500, 3000, 10000]) {
  await new Promise((r) => setTimeout(r, wait));
  console.log(`+${wait}ms after kill, in distro:\n${ps().join('\n') || '(none)'}`);
}
