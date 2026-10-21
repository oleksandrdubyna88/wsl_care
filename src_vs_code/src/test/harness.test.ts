import assert from 'node:assert/strict';
import { test } from 'node:test';

import { Element, runPageScript } from './support/pageHarness';

/**
 * The page harness is code under test (`common.generated-code-tests` §3): a permissive fake turns a suite green for
 * the wrong reason, so its contract is pinned here — the kit's tests it was ported from (deadline, no ambient globals,
 * `null` for a miss, refused selector shapes, bubbling, cloneable posts) and the STRICT additions this panel leans on
 * (every HTML sink and every unmodelled member throws).
 */

/** `node:vm`'s deadline error is minted in the page's realm, so it is read by its code. */
function isDeadline(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'code' in error && error.code === 'ERR_SCRIPT_EXECUTION_TIMEOUT';
}

function isReferenceErrorFor(name: string): (error: unknown) => boolean {
  return (error) => typeof error === 'object' && error !== null && 'name' in error && error.name === 'ReferenceError' && 'message' in error && error.message === `${name} is not defined`;
}

function withRoot(script: string): { page: ReturnType<typeof runPageScript>; root: Element } {
  const root = new Element('MAIN');
  return { page: runPageScript(script, { root }), root };
}

test('a page script that never returns fails by the deadline instead of hanging the suite', () => {
  assert.throws(() => runPageScript('while (true) {}'), isDeadline);
});

test('a message handler that never returns fails by the same deadline', () => {
  const page = runPageScript("window.addEventListener('message', () => { while (true) {} });");
  assert.throws(() => page.message({ type: 'view' }), isDeadline);
});

test('a click handler that never returns fails by the same deadline', () => {
  const { page, root } = withRoot(`
    const b = document.createElement('button');
    b.addEventListener('click', () => { while (true) {} });
    document.getElementById('root').appendChild(b);`);
  assert.throws(() => page.click(root.children[0] as Element), isDeadline);
});

for (const name of ['fetch', 'require', 'process', 'setTimeout', 'XMLHttpRequest']) {
  test(`${name} is not a global inside the page — a ReferenceError, not a stand-in`, () => {
    assert.throws(() => runPageScript(`${name};`), isReferenceErrorFor(name));
  });
}

for (const sink of ["el.innerHTML = '<b>x</b>'", 'el.innerHTML', "el.outerHTML = 'x'", "el.insertAdjacentHTML('beforeend', 'x')"]) {
  test(`an HTML sink throws: ${sink}`, () => {
    assert.throws(() => withRoot(`const el = document.getElementById('root'); ${sink};`), /strict DOM: .*\(an HTML sink\) is not modelled/);
  });
}

test('document.write is an HTML sink and throws', () => {
  assert.throws(() => runPageScript("document.write('<script>x</script>');"), /document\.write \(an HTML sink\)/);
});

for (const member of ['style', 'onclick', 'src', 'href', 'srcdoc', 'shadowRoot', 'parentNode']) {
  test(`an unmodelled member throws when read: ${member}`, () => {
    assert.throws(() => withRoot(`document.getElementById('root').${member};`), new RegExp(`MAIN\\.${member} is not modelled`));
  });
}

test('an unmodelled member throws when written, too', () => {
  assert.throws(() => withRoot("document.getElementById('root').onclick = () => 1;"), /writing MAIN\.onclick is not modelled/);
});

for (const tag of ['script', 'img', 'iframe', 'a', 'style', 'link', 'object']) {
  test(`createElement('${tag}') throws — a text page creates nothing that loads, runs or navigates`, () => {
    assert.throws(() => runPageScript(`document.createElement('${tag}');`), /createElement.* is not modelled/);
  });
}

for (const name of ['onclick', 'onerror', 'style', 'src', 'href', 'class']) {
  test(`setAttribute('${name}') throws`, () => {
    assert.throws(() => withRoot(`document.getElementById('root').setAttribute('${name}', 'x');`), /setAttribute\(".*"\) is not modelled/);
  });
}

test('setAttribute takes role, scope, title, type, colspan, aria-* and data-* (data-* lands in dataset)', () => {
  const { root } = withRoot(`
    const el = document.getElementById('root');
    for (const [k, v] of [['role', 'region'], ['scope', 'row'], ['title', 't'], ['type', 'button'], ['colspan', '2'], ['aria-label', 'l']]) el.setAttribute(k, v);
    el.setAttribute('data-row-id', 'memory.total');`);
  assert.deepEqual(root.attributes, { role: 'region', scope: 'row', title: 't', type: 'button', colspan: '2', 'aria-label': 'l' });
  assert.equal(root.dataset.rowId, 'memory.total');
});

test('textContent takes a string only — undefined or a number would print, and a blank row is a defect here', () => {
  assert.throws(() => withRoot("document.getElementById('root').textContent = undefined;"), /textContent takes a string/);
  assert.throws(() => withRoot("document.getElementById('root').textContent = 0;"), /textContent takes a string/);
});

test('setting textContent replaces the children, as a browser does; reading it joins the descendants\' text', () => {
  const { root } = withRoot(`
    const el = document.getElementById('root');
    const a = document.createElement('span'); a.textContent = 'a';
    const b = document.createElement('span'); b.textContent = 'b';
    el.appendChild(a); el.appendChild(b);
    acquireVsCodeApi().postMessage(el.textContent);
    el.textContent = 'only';`);
  assert.equal(root.children.length, 0);
  assert.equal(root.textContent, 'only');
});

test('appendChild accepts only an element of this page, never an object the page made up, and never a cycle', () => {
  assert.throws(() => withRoot("document.getElementById('root').appendChild({ tagName: 'DIV' });"), /not an element of this page/);
  assert.throws(() => withRoot(`
    const el = document.getElementById('root');
    const d = document.createElement('div'); el.appendChild(d); d.appendChild(el);`), /cannot be placed inside itself/);
});

test('replaceChildren swaps the whole content, so a re-render never duplicates', () => {
  const { root } = withRoot(`
    const el = document.getElementById('root');
    const make = (t) => { const p = document.createElement('p'); p.textContent = t; return p; };
    el.replaceChildren(make('1'), make('2'));
    el.replaceChildren(make('3'));`);
  assert.deepEqual(root.children.map((c) => c.textContent), ['3']);
});

test('a selector or id that matches nothing answers null and an empty list, never undefined; other shapes are refused', () => {
  const { page } = withRoot(`
    const el = document.getElementById('root');
    acquireVsCodeApi().postMessage({ id: document.getElementById('absent') === null, one: el.querySelector('[data-x]') === null, all: el.querySelectorAll('[data-x]').length });`);
  assert.deepEqual(page.posted, [{ id: true, one: true, all: 0 }]);
  assert.throws(() => withRoot("document.getElementById('root').querySelector('.hidden');"), /reads only \[data-x\]/);
});

test('a click runs the element\'s listeners, then each ancestor\'s (bubbling), with target and currentTarget', () => {
  const { page, root } = withRoot(`
    const vscode = acquireVsCodeApi();
    const el = document.getElementById('root');
    const b = document.createElement('button'); b.setAttribute('data-action', 'refresh');
    b.addEventListener('click', () => vscode.postMessage('own'));
    el.addEventListener('click', (e) => vscode.postMessage('root saw ' + e.target.dataset.action + ' at ' + e.currentTarget.tagName));
    el.appendChild(b);`);
  page.click(root.one('[data-action="refresh"]'));
  assert.deepEqual(page.posted, ['own', 'root saw refresh at MAIN']);
});

test('clicking an element that is not in the page fails instead of doing nothing', () => {
  const { page } = withRoot('');
  assert.throws(() => page.click(new Element('BUTTON')), /not in the running page/);
});

test('only click listeners on elements, only message listeners on window', () => {
  assert.throws(() => withRoot("document.getElementById('root').addEventListener('mouseover', () => 1);"), /mouseover on an element is not modelled/);
  assert.throws(() => runPageScript("window.addEventListener('load', () => 1);"), /message only/);
});

test('a host message reaches the window listener as { data }', () => {
  const page = runPageScript("const v = acquireVsCodeApi(); window.addEventListener('message', (e) => v.postMessage(e.data.n + 1));");
  page.message({ n: 41 });
  assert.deepEqual(page.posted, [42]);
});

test('what a page posts must be structured-cloneable, as for a real postMessage', () => {
  assert.throws(() => runPageScript('acquireVsCodeApi().postMessage({ run: () => 1 });'), /could not be cloned/);
});

test('acquireVsCodeApi can be called once, as in VS Code', () => {
  assert.throws(() => runPageScript('acquireVsCodeApi(); acquireVsCodeApi();'), /only be called once/);
});

test('dataset writes take strings only, and classList keeps className in step', () => {
  const { root } = withRoot(`
    const el = document.getElementById('root');
    el.dataset.state = 'value';
    el.classList.add('a', 'b'); el.classList.remove('a');`);
  assert.equal(root.dataset.state, 'value');
  assert.equal(root.className, 'b');
  assert.throws(() => withRoot("document.getElementById('root').dataset.n = 3;"), /dataset\.n takes a string/);
});

// ---- E6.S4: the date picker — an <input> with a value, and its min / max — and nothing more of a form ----

test('E6.S4: an input may be created; its value is a string the page reads and writes; min, max and type are attributes', () => {
  const { page, root } = withRoot(`
    const el = document.getElementById('root');
    const input = document.createElement('input');
    input.setAttribute('type', 'date'); input.setAttribute('min', '2026-07-08'); input.setAttribute('max', '2026-10-05');
    input.setAttribute('data-picker', 'day');
    input.value = '2026-10-01';
    el.appendChild(input);
    acquireVsCodeApi().postMessage(input.value);`);
  const input = root.one('[data-picker="day"]');
  assert.deepEqual([input.tagName, input.attributes.type, input.attributes.min, input.attributes.max, input.value], ['INPUT', 'date', '2026-07-08', '2026-10-05', '2026-10-01']);
  assert.deepEqual(page.posted, ['2026-10-01']);
});

test('E6.S4: an input\'s value takes a string only, and a form, a select or a textarea is still not creatable', () => {
  assert.throws(() => withRoot("const i = document.createElement('input'); i.value = 7;"), /value takes a string/);
  for (const tag of ['form', 'select', 'textarea']) {
    assert.throws(() => runPageScript(`document.createElement('${tag}');`), /createElement.* is not modelled/);
  }
  assert.throws(() => withRoot("document.createElement('input').setAttribute('formaction', 'x');"), /setAttribute\(".*"\) is not modelled/);
});

// ---- W0 (todo/PLAN_disk_health_E18.md §4 item 7): the widening the kit's DOM port needs — and nothing more ----
//
// The kit's components touch `id` and `tabindex` attributes, `keydown` and `input` events, `focus()` and an event's `key` and
// `preventDefault()` (the kit's research/PLAN_kit_components_storybook.md §4.3). Each is modelled here and every neighbour
// stays refused, so a component that reached one step further fails in this harness, by name.

test('W0 item 7: setAttribute takes id and tabindex — what the kit\'s tabs set — and still refuses style, onclick and href', () => {
  const { root } = withRoot(`
    const el = document.getElementById('root');
    el.setAttribute('id', 'full-tab-wsl');
    el.setAttribute('tabindex', '-1');`);
  assert.deepEqual(root.attributes, { id: 'full-tab-wsl', tabindex: '-1' });
  for (const name of ['style', 'onclick', 'href']) {
    assert.throws(() => withRoot(`document.getElementById('root').setAttribute('${name}', 'x');`), /setAttribute\(".*"\) is not modelled/, name);
  }
});

test('W0 item 7: an element takes keydown and input listeners, and still refuses change and focus', () => {
  const { root } = withRoot(`
    const el = document.getElementById('root');
    el.addEventListener('keydown', () => 1);
    el.addEventListener('input', () => 1);`);
  assert.deepEqual(root.handlers.map((h) => h.kind), ['keydown', 'input']);
  for (const kind of ['change', 'focus']) {
    assert.throws(() => withRoot(`document.getElementById('root').addEventListener('${kind}', () => 1);`), new RegExp(`listening for ${kind} on an element is not modelled`), kind);
  }
});

test('W0 item 7: focus() makes the element the document\'s active element — read by the page, recorded for the test', () => {
  const { page, root } = withRoot(`
    const vscode = acquireVsCodeApi();
    const el = document.getElementById('root');
    const b = document.createElement('button'); el.appendChild(b);
    vscode.postMessage({ before: document.activeElement === null });
    b.focus();
    vscode.postMessage({ after: document.activeElement === b });`);
  assert.deepEqual(page.posted, [{ before: true }, { after: true }]);
  assert.equal(page.activeElement, root.children[0]);
});

test('W0 code round: focus() takes effect only on an ATTACHED and focusable element (a button, an input, or one with a tabindex) — anywhere else it is a no-op, as in a browser', () => {
  const { page, root } = withRoot(`
    const vscode = acquireVsCodeApi();
    const el = document.getElementById('root');
    const make = (tag, attributes = {}) => { const e = document.createElement(tag); for (const [k, v] of Object.entries(attributes)) e.setAttribute(k, v); return e; };
    const tried = (name, e) => { const before = document.activeElement; e.focus(); const now = document.activeElement; vscode.postMessage({ name, took: now !== before && now === e }); };

    tried('detached button', make('button'));
    tried('detached input', make('input'));
    const plain = make('div'); el.appendChild(plain);
    tried('div without tabindex', plain);
    const span = make('span', { tabindex: '-1' }); el.appendChild(span);
    tried('span with tabindex', span);
    const button = make('button'); el.appendChild(button);
    tried('attached button', button);
    const input = make('input'); el.appendChild(input);
    tried('attached input', input);`);
  assert.deepEqual(page.posted, [
    { name: 'detached button', took: false },
    { name: 'detached input', took: false },
    { name: 'div without tabindex', took: false },
    { name: 'span with tabindex', took: true },
    { name: 'attached button', took: true },
    { name: 'attached input', took: true },
  ]);
  assert.equal(page.activeElement, root.children[3], 'the last element to take focus is the input');
});
test('W0 item 7: a dispatched keydown carries its key, and a preventDefault() the page calls is recorded', () => {
  const { page, root } = withRoot(`
    const vscode = acquireVsCodeApi();
    const el = document.getElementById('root');
    const b = document.createElement('button'); b.setAttribute('data-key', 'x'); el.appendChild(b);
    el.addEventListener('keydown', (e) => { vscode.postMessage(e.key); if (e.key.startsWith('Arrow')) e.preventDefault(); });`);
  const button = root.one('[data-key="x"]');
  const arrow = page.key(button, 'ArrowRight');
  const letter = page.key(button, 'a');
  assert.deepEqual(page.posted, ['ArrowRight', 'a']);
  assert.deepEqual([arrow.type, arrow.key, arrow.defaultPrevented], ['keydown', 'ArrowRight', true]);
  assert.deepEqual([letter.type, letter.key, letter.defaultPrevented], ['keydown', 'a', false]);
});

test('W0 item 7: typing into an input sets its value and dispatches input, which the page reads; only an input can be typed into', () => {
  const { page, root } = withRoot(`
    const vscode = acquireVsCodeApi();
    const el = document.getElementById('root');
    const box = document.createElement('input'); box.setAttribute('data-box', 'q'); el.appendChild(box);
    const other = document.createElement('div'); other.setAttribute('data-other', 'd'); el.appendChild(other);
    box.addEventListener('input', () => vscode.postMessage(box.value));`);
  const event = page.type(root.one('[data-box="q"]'), 'ollama');
  assert.deepEqual(page.posted, ['ollama']);
  assert.equal(event.type, 'input');
  assert.equal(root.one('[data-box="q"]').value, 'ollama');
  assert.throws(() => page.type(root.one('[data-other="d"]'), 'x'), /only an input can be typed into/);
});

test('W0 item 7: a key or typing aimed at an element that is not in the running page fails instead of doing nothing', () => {
  const { page } = withRoot('');
  assert.throws(() => page.key(new Element('BUTTON'), 'a'), /not in the running page/);
  assert.throws(() => page.type(new Element('INPUT'), 'a'), /not in the running page/);
});
