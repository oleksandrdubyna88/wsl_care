// Reads what `vsce show <extension-id> --json` wrote and says whether the Marketplace serves <version>:
//
//   node marketplace-served.cjs <vsce-show-json-file> <version>   ->   "served|<newest five versions>" or "not|<newest five>"
//
// The ONE reader for release-extension.yml's skip step and wait-marketplace-served.sh (coai code round 2026-10-09): two copies
// could disagree, and a skip that misses what the wait calls served publishes again. The whole list decides; the newest five
// are what a log shows. An answer that is not JSON with versions lists none — vsce 4.0.0 prints `undefined` and exits 0 for an
// extension it does not know (observed 2026-10-04). Exit 0 with an answer, 2 a missing argument.
'use strict';
const fs = require('fs');

const [file, version] = process.argv.slice(2);
if (!file || !version) {
  console.error('usage: marketplace-served.cjs <vsce-show-json-file> <version>');
  process.exit(2);
}

let versions = [];
try {
  versions = (JSON.parse(fs.readFileSync(file, 'utf8')).versions || []).map((entry) => entry.version);
} catch {
  versions = [];
}
process.stdout.write(`${versions.includes(version) ? 'served' : 'not'}|${versions.slice(0, 5).join(', ')}`);
