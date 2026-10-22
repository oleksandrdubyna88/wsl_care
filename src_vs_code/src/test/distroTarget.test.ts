import assert from 'node:assert/strict';
import { test } from 'node:test';

import { persistedNameOf } from '../wsl/distroTarget';

/** A name re-minted from the journal passes the setting's pattern too (security check): every name `listDistros` can mint already does. */

test('persistedNameOf mints a name the setting pattern accepts, and nothing else', () => {
  for (const ok of ['Ubuntu', 'Ubuntu-26.04', 'docker-desktop', 'my_distro', 'A'.repeat(64)]) {
    assert.equal(persistedNameOf(ok), ok);
  }
  for (const bad of ['', '-x', 'My Distro', 'a"b', "a'b", 'a\nb', 'a\r', '~', 'a~b', 'a;b', 'a$b', '.hidden', 'A'.repeat(65)]) {
    assert.equal(persistedNameOf(bad), undefined, JSON.stringify(bad));
  }
});
