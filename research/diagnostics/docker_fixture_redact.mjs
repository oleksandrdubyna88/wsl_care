// Redacts a live-contract capture (WSL_CARE_LIVE_CAPTURE=<dir>, src_daemon/tests/WslCare.LiveContract)
// into a fixture directory (src_daemon/tests/fixtures/docker/<capture>/). Read-only on its input; writes
// only <out>. Usage: node docker_fixture_redact.mjs <capture-dir> <out-dir>
//
// MECHANICAL rules, so the next capture is redacted the same way without anyone remembering names:
//   - labels (system df -v / ps rows, event attributes): ONLY an allowlist survives — the keys the product
//     reads (org.testcontainers*, wsl-care.keep, com.docker.volume.anonymous) and Docker Desktop's own
//     desktop.docker.io/ports* and wsl-distro. Everything else goes: compose labels carry project names and
//     host paths, Aspire's usvc-dev labels carry environment variables, bind labels carry source paths.
//   - a container's Command is replaced by "REDACTED" (argv may hold credentials).
//   - container names -> container-NN, named volumes -> named-volume-NN, networks other than bridge/host/none
//     -> network-NN, image repositories not in the PUBLIC list below -> local-image-NN; each name is mapped
//     ONCE and replaced in every file (df -v, ps, inspect, stats, volume ls, events), so the fixtures stay
//     consistent with one another.
//   - /home/<user> -> /home/user.
// Then every original name is searched for in the output, and the script fails if one survived.
import fs from 'node:fs';
import path from 'node:path';

const [input, output] = process.argv.slice(2);
if (!input || !output) {
  console.error('usage: node docker_fixture_redact.mjs <capture-dir> <out-dir>');
  process.exit(2);
}

// Images published on public registries: their names identify nothing about the owner.
const PUBLIC = new Set([
  'postgres', 'redis', 'mysql', 'node', 'neo4j', 'composer', 'rabbitmq', 'qdrant/qdrant', 'docker.io/postgres',
  'docker.io/qdrant/qdrant', 'mcr.microsoft.com/playwright', 'minlag/mermaid-cli', 'dunglas/frankenphp',
  'ghcr.io/orange-opensource/hurl', 'rhysd/actionlint', 'koalaman/shellcheck', 'localstack/localstack',
  'docker.elastic.co/elasticsearch/elasticsearch', '<none>',
]);
const KEEP_LABEL = (k) => k === 'wsl-care.keep' || k === 'com.docker.volume.anonymous' || k.startsWith('org.testcontainers')
  || k === 'desktop.docker.io/wsl-distro' || k.startsWith('desktop.docker.io/ports');
const isHex64 = (s) => /^[0-9a-f]{64}$/.test(s);

const read = (f) => (fs.existsSync(path.join(input, f)) ? fs.readFileSync(path.join(input, f), 'utf8') : '');
const lines = (text) => text.split('\n').filter((l) => l.trim().length > 0);
const dfv = JSON.parse(read('system-df-v.out'));

const maps = { container: new Map(), volume: new Map(), network: new Map(), repo: new Map() };
const pad = (n) => String(n).padStart(2, '0');
const mapName = (kind, prefix, name) => {
  if (!name) return name;
  if (!maps[kind].has(name)) maps[kind].set(name, `${prefix}-${pad(maps[kind].size + 1)}`);
  return maps[kind].get(name);
};
dfv.Containers.forEach((c) => mapName('container', 'container', c.Names));
dfv.Volumes.filter((v) => !isHex64(v.Name)).forEach((v) => mapName('volume', 'named-volume', v.Name));
dfv.Images.filter((i) => !PUBLIC.has(i.Repository)).forEach((i) => mapName('repo', 'local-image', i.Repository));

const repoOf = (image) => {
  // "name:tag", "registry:port/name:tag", "sha256:…" — the repository is before the LAST colon after the last slash.
  if (image.startsWith('sha256:')) return image;
  const slash = image.lastIndexOf('/');
  const colon = image.indexOf(':', slash + 1);
  const repo = colon < 0 ? image : image.slice(0, colon);
  const tag = colon < 0 ? '' : image.slice(colon);
  return PUBLIC.has(repo) ? image : `${mapName('repo', 'local-image', repo)}${tag}`;
};
const labels = (text) => (text ?? '').split(',').filter((kv) => KEEP_LABEL(kv.split('=')[0])).join(',');
const network = (n) => (n ?? '').split(',').filter(Boolean).map((x) => (['bridge', 'host', 'none'].includes(x) ? x : mapName('network', 'network', x))).join(',');
const volumeName = (n) => (isHex64(n) ? n : mapName('volume', 'named-volume', n));
const containerRow = (c) => ({
  ...c,
  Command: c.Command === undefined ? undefined : '"REDACTED"',
  Image: repoOf(c.Image),
  Labels: labels(c.Labels),
  Mounts: (c.Mounts ?? '').split(',').filter(Boolean).map((m) => (m.includes('/') ? 'REDACTED-BIND' : volumeName(m))).join(','),
  Names: mapName('container', 'container', c.Names),
  Networks: network(c.Networks),
});

const out = {};
out['system-df-v.out'] = JSON.stringify({
  Images: dfv.Images.map((i) => ({ ...i, Repository: PUBLIC.has(i.Repository) ? i.Repository : mapName('repo', 'local-image', i.Repository) })),
  Containers: dfv.Containers.map(containerRow),
  Volumes: dfv.Volumes.map((v) => ({ ...v, Labels: labels(v.Labels), Mountpoint: v.Mountpoint.replace(v.Name, volumeName(v.Name)), Name: volumeName(v.Name) })),
  BuildCache: dfv.BuildCache.map((b) => ({ ...b, Description: b.Description ? 'REDACTED' : '' })),
}) + '\n';
out['ps-a.out'] = lines(read('ps-a.out')).map((l) => JSON.stringify(containerRow(JSON.parse(l)))).join('\n') + '\n';
out['container-inspect.out'] = lines(read('container-inspect.out')).map((l) => {
  const d = JSON.parse(l);
  return JSON.stringify({ ...d, name: `/${mapName('container', 'container', d.name.replace(/^\//, ''))}`, mounts: d.mounts.map((m) => ({ ...m, name: m.type === 'volume' ? volumeName(m.name) : m.name })) });
}).join('\n') + '\n';
out['stats.out'] = lines(read('stats.out')).map((l) => {
  const s = JSON.parse(l);
  return JSON.stringify({ ...s, Name: mapName('container', 'container', s.Name) });
}).join('\n') + '\n';
out['volume-ls-dangling.out'] = lines(read('volume-ls-dangling.out')).map(volumeName).join('\n') + '\n';
const event = (l) => {
  const e = JSON.parse(l);
  const a = e.Actor?.Attributes ?? {};
  const kept = Object.fromEntries(Object.entries(a).filter(([k]) => KEEP_LABEL(k) || ['execID', 'exitCode'].includes(k)));
  if (a.name) kept.name = mapName('container', 'container', a.name);
  if (a.image) kept.image = repoOf(a.image);
  return JSON.stringify({ ...e, Actor: { ...e.Actor, Attributes: kept } });
};
for (const f of ['events.out', 'events-exec-die.out']) out[f] = lines(read(f)).map(event).join('\n') + (read(f).trim() ? '\n' : '');
for (const f of ['system-df.out', 'version.out', 'journalctl-disk-usage.out', 'captured-at.txt']) out[f] = read(f);
// The live contract shows two units under one command name; the fixture names each by the unit it is about.
for (const f of fs.readdirSync(input).filter((n) => n.startsWith('systemctl-show'))) {
  out[read(f).includes('Id=systemd-journald.service') ? 'systemctl-show-journald.out' : 'systemctl-show-missing.out'] = read(f);
}

const originals = [...maps.container.keys(), ...maps.volume.keys(), ...maps.network.keys(), ...maps.repo.keys()];
fs.mkdirSync(output, { recursive: true });
let leaks = 0;
for (const [file, text0] of Object.entries(out)) {
  const text = text0.replace(/\/home\/[a-z_][a-z0-9_-]*/g, '/home/user');
  for (const name of originals) {
    if (text.includes(`"${name}"`) || text.includes(`/${name}"`)) {
      console.error(`LEAK: ${file} still holds ${name}`);
      leaks++;
    }
  }
  fs.writeFileSync(path.join(output, file), text);
}
console.log(`containers ${maps.container.size}, named volumes ${maps.volume.size}, networks ${maps.network.size}, local images ${maps.repo.size}; leaks ${leaks}`);
process.exit(leaks === 0 ? 0 : 1);
