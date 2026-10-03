// Read-only wsl.exe measurements for E5.S1 (research/2026-10-03_wsl_exe_facts.md).
// Run: node research/diagnostics/wsl_exe_facts.mjs [distro] [all|peek]  (Windows, Node 20+; 2026-10-03: Node 24.18.0).
// Spawns the absolute System32 wsl.exe with shell:false, exactly as the extension's runner will,
// and prints exit code + raw bytes (hex head) + decodings. Never --shutdown, never -u, never terminate.
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';

const WSL = join(process.env.SystemRoot, 'System32', 'wsl.exe');
const distro = process.argv[2] ?? 'Ubuntu';

function hex(buf, n = 48) {
  return [...buf.subarray(0, n)].map((b) => b.toString(16).padStart(2, '0')).join(' ');
}

function show(label, args, env = process.env) {
  const t0 = Date.now();
  const r = spawnSync(WSL, args, { shell: false, env, timeout: 60_000, windowsHide: true });
  const ms = Date.now() - t0;
  console.log(`\n### ${label}`);
  console.log(`argv: ${JSON.stringify(args)}`);
  console.log(`exit: ${r.status} signal: ${r.signal} error: ${r.error ? r.error.message : 'none'} ms: ${ms}`);
  for (const [name, buf] of [['stdout', r.stdout], ['stderr', r.stderr]]) {
    console.log(`${name} bytes: ${buf.length}`);
    if (buf.length > 0) {
      console.log(`${name} hex: ${hex(buf)}`);
      console.log(`${name} utf8: ${JSON.stringify(buf.toString('utf8'))}`);
      console.log(`${name} utf16le: ${JSON.stringify(buf.toString('utf16le'))}`);
    }
  }
}

const mode = process.argv[3] ?? 'all';
if (mode === 'all') {
  show('version', ['--version']);
  show('list quiet', ['--list', '--quiet']);
  show('list verbose', ['-l', '-v']);
  show('list running quiet', ['--list', '--running', '--quiet']);
  show('list quiet WSL_UTF8=1', ['--list', '--quiet'], { ...process.env, WSL_UTF8: '1' });
  show('exec echo $HOME', ['-d', distro, '--exec', 'echo', '$HOME']);
  show('-- echo $HOME', ['-d', distro, '--', 'echo', '$HOME']);
  show('exec printf non-ascii', ['-d', distro, '--cd', '/', '--exec', 'printf', 'café ж']);
  show('exec wsl-care --version (absent)', ['-d', distro, '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', '--version']);
  show('exec missing path', ['-d', distro, '--cd', '/', '--exec', '/opt/wsl-care-missing-e5s1/nope']);
  show('exec missing path WSL_UTF8=1', ['-d', distro, '--cd', '/', '--exec', '/opt/wsl-care-missing-e5s1/nope'], { ...process.env, WSL_UTF8: '1' });
  show('exec non-executable file', ['-d', distro, '--cd', '/', '--exec', '/etc/hostname']);
  show('exec exit 2', ['-d', distro, '--cd', '/', '--exec', 'sh', '-c', 'echo to-err >&2; exit 2']);
  show('unknown distro', ['-d', 'NoSuchDistro-e5s1', '--cd', '/', '--exec', 'true']);
  show('unknown option', ['--no-such-option-e5s1']);
}
if (mode === 'peek') {
  show('list verbose', ['-l', '-v']);
  show('list running quiet', ['--list', '--running', '--quiet']);
}
