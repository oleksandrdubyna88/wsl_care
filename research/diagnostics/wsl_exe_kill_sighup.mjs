// Same as wsl_exe_kill.mjs, but the Linux process ignores SIGHUP (inherited SIG_IGN across exec): does it survive?
// Run: node research/diagnostics/wsl_exe_kill_sighup.mjs  — kills ONLY the wsl.exe it started; the sleep ends on its own.
import { spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
const WSL = join(process.env.SystemRoot, 'System32', 'wsl.exe');
const marker = '38.517';
const ps = () => spawnSync(WSL, ['-d', 'Ubuntu', '--cd', '/', '--exec', 'ps', '-eo', 'pid,ppid,args'], { timeout: 20000 }).stdout.toString('utf8')
  .split('\n').filter((l) => l.includes(`sleep ${marker}`) && !l.includes('trap')).join('\n');
const child = spawn(WSL, ['-d', 'Ubuntu', '--cd', '/', '--exec', 'sh', '-c', `trap "" HUP; exec sleep ${marker}`], { shell: false, windowsHide: true });
console.log(`started wsl.exe pid ${child.pid}`);
await new Promise((r) => setTimeout(r, 3000));
console.log(`before kill:\n${ps() || '(none)'}`);
const exited = new Promise((r) => child.on('exit', (code, signal) => r({ code, signal })));
child.kill();
console.log(`wsl.exe exit: ${JSON.stringify(await exited)}`);
await new Promise((r) => setTimeout(r, 2000));
console.log(`+2s after kill:\n${ps() || '(none)'}`);
