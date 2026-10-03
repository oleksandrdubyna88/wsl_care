// Does killing wsl.exe (a PID this script started) end the Linux process? Read-only check with ps afterwards.
// Run: node research/diagnostics/wsl_exe_kill.mjs [sleep-marker]  — kills ONLY the wsl.exe it started (child.kill()).
import { spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
const WSL = join(process.env.SystemRoot, 'System32', 'wsl.exe');
const marker = process.argv[2] ?? '37.123';
const ps = () => spawnSync(WSL, ['-d', 'Ubuntu', '--cd', '/', '--exec', 'ps', '-eo', 'pid,ppid,args'], { timeout: 20000 }).stdout.toString('utf8')
  .split('\n').filter((l) => l.includes(`sleep ${marker}`)).join('\n');
const child = spawn(WSL, ['-d', 'Ubuntu', '--cd', '/', '--exec', 'sleep', marker], { shell: false, windowsHide: true });
console.log(`started wsl.exe pid ${child.pid} at ${new Date().toISOString()}`);
await new Promise((r) => setTimeout(r, 3000));
console.log(`before kill, in distro:\n${ps() || '(none)'}`);
const exited = new Promise((r) => child.on('exit', (code, signal) => r({ code, signal })));
console.log(`child.kill() -> ${child.kill()}`);
console.log(`wsl.exe exit: ${JSON.stringify(await exited)}`);
for (const wait of [500, 3000]) {
  await new Promise((r) => setTimeout(r, wait));
  console.log(`+${wait}ms after kill, in distro:\n${ps() || '(none)'}`);
}
