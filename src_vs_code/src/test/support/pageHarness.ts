import assert from 'node:assert/strict';
import { createContext, runInContext, Script, type Context } from 'node:vm';

/**
 * The harness that RUNS the panel's page script (`media/panel.js`) — `common.generated-code-tests` §1: a page is a
 * program, so it is executed and the DOM it builds is asserted, never its source matched as text.
 *
 * <p><b>Ported, not depended on</b> (plan §15g M7: E5 does not depend on the kit), from dew_flow_vscode_kit's
 * `src/test/pageHarness.ts` (2026-10-03, itself from ConnectOtherAIs): the `node:vm` sandbox with an EXPLICIT allowlist
 * of globals — `document`, `window`, `acquireVsCodeApi`, nothing of this process (no `require`, `process`, `fetch`,
 * `setTimeout`) —, the 5 s deadline on the script AND on every event dispatched into it afterwards (through a
 * trampoline run in the page's context), `null` for a selector miss, selector shapes refused rather than guessed,
 * `posted[]` structured-cloned, events bubbling up the tree.</p>
 *
 * <p><b>Stricter than the kit's, and stricter than a browser, never more permissive.</b> This page builds its whole
 * DOM itself, so the harness models `createElement` / `appendChild` / `replaceChildren` — and every element the page
 * touches is a PROXY that throws on any member the harness does not model. In particular every HTML sink —
 * `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write` — throws, `style` throws, `setAttribute` takes only
 * `role`, `scope`, `title`, `type`, `colspan`, `aria-*` and `data-*` (never `on*`, `style`, `src`, `href`), and
 * `createElement` only the tags a text page needs (never `script`, `img`, `iframe`, `a`, `style`, `link`). A page that
 * reached for any of them fails here, by name, even where a browser would have obliged. `textContent` takes only a
 * string (a browser would print `undefined` — the blank row this panel must never show) and, as in a browser,
 * replaces the element's children.</p>
 */

const TIMEOUT_MS = 5000;

/** What a page may create: a text page's elements, nothing that loads, runs or navigates. */
export const CREATABLE = new Set(['DIV', 'SECTION', 'HEADER', 'H1', 'H2', 'H3', 'P', 'SPAN', 'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD', 'BUTTON', 'UL', 'LI', 'SMALL', 'STRONG']);

/** The attributes `setAttribute` takes; `data-*` and `aria-*` by prefix. */
const ATTRIBUTES = new Set(['role', 'scope', 'title', 'type', 'colspan']);

/** Event kinds a page may listen for on an element. */
const ELEMENT_EVENTS = new Set(['click']);

type Handler = (event: unknown) => void;

/** The selector shapes this shim reads: `[data-x]`, `[data-x="y"]`, optionally behind a tag name. */
function selectorOf(selector: string, what: string): { tag: string | undefined; key: string; value: string | undefined } {
  const found = /^(?:([a-z][a-z0-9]*))?\[data-([a-z][a-z0-9-]*)(?:="([^"]*)")?\]$/.exec(selector);
  assert.ok(found !== null, `${what}(${JSON.stringify(selector)}): this harness reads only [data-x] and [data-x="y"], optionally behind a tag name`);

  return { tag: found[1]?.toUpperCase(), key: camel(found[2] ?? ''), value: found[3] };
}

/** `data-remove-prompt` is `dataset.removePrompt`, as a browser spells it. */
export function camel(attribute: string): string {
  return attribute.replace(/-([a-z])/g, (_all, letter: string) => letter.toUpperCase());
}

/** A page's element, as the TEST sees it: the raw object, with everything readable. */
export class Element {
  readonly children: Element[] = [];
  parent: Element | undefined = undefined;
  /** This element's OWN text — `textContent` reads it together with its descendants'. */
  text = '';
  className = '';
  hidden = false;
  disabled = false;
  readonly dataset: Record<string, string> = {};
  readonly attributes: Record<string, string> = {};
  readonly handlers: { kind: string; run: Handler }[] = [];

  constructor(readonly tagName: string) {}

  /** As a browser's `textContent`: this element's text and every descendant's, in document order. */
  get textContent(): string {
    return this.text + this.children.map((child) => child.textContent).join('');
  }

  /** Every element under this one, depth first. */
  descendants(): Element[] {
    return this.children.flatMap((child) => [child, ...child.descendants()]);
  }

  matches(selector: string): boolean {
    return this.fits(selectorOf(selector, 'matches'));
  }

  /** Every element under this one matching `selector` — the shape checked FIRST, so a refused shape is refused even
   * where nothing could have matched (found by its own test: an empty element answered `[]` for `.hidden`). */
  all(selector: string): Element[] {
    const wanted = selectorOf(selector, 'querySelectorAll');
    return this.descendants().filter((e) => e.fits(wanted));
  }

  private fits(wanted: { tag: string | undefined; key: string; value: string | undefined }): boolean {
    const held = this.dataset[wanted.key];

    return held !== undefined && (wanted.value === undefined || held === wanted.value) && (wanted.tag === undefined || this.tagName === wanted.tag);
  }

  /** The one element under this one matching `selector` — exactly one, or the test fails naming it. */
  one(selector: string): Element {
    const found = this.all(selector);
    assert.equal(found.length, 1, `expected one ${selector}, found ${found.length}`);
    return found[0] as Element;
  }
}

/** What the running page offers a test. */
export interface Page {
  /** Every message the page posted, copied into this realm. */
  readonly posted: readonly unknown[];
  /** A message from the host, delivered to every `window` message listener as `{ data }`, under the deadline. */
  message(data: unknown): void;
  /** A click on an element of the page — its listeners, then each ancestor's — under the deadline. */
  click(element: Element): void;
  /** The elements the test handed in by id. */
  readonly ids: Readonly<Record<string, Element>>;
}

const SLOT = '\u0000dispatch';
const TRAMPOLINE = new Script(`globalThis[${JSON.stringify(SLOT)}]()`);

function runBounded(context: Context, sandbox: Record<string, unknown>, run: () => void): void {
  Object.defineProperty(sandbox, SLOT, { value: run, configurable: true, enumerable: false, writable: false });
  try {
    TRAMPOLINE.runInContext(context, { timeout: TIMEOUT_MS });
  } finally {
    delete sandbox[SLOT];
  }
}

/** The one door between the raw elements and what the page holds: a proxy per element, refusing what is not modelled. */
class Membrane {
  private readonly toProxy = new Map<Element, object>();
  private readonly toElement = new WeakMap<object, Element>();

  view(element: Element): object {
    const known = this.toProxy.get(element);
    if (known !== undefined) {
      return known;
    }
    const proxy = new Proxy({}, strictTraps(this.members(element), element.tagName));
    this.toProxy.set(element, proxy);
    this.toElement.set(proxy, element);

    return proxy;
  }

  /** The element behind a value the page handed back — only one this page holds. */
  element(value: unknown, what: string): Element {
    const element = typeof value === 'object' && value !== null ? this.toElement.get(value) : undefined;
    assert.ok(element !== undefined, `${what}: not an element of this page`);
    return element;
  }

  /** Everything a page may read or write on `element`, by name. */
  private members(element: Element): Members {
    return elementMembers(element, this);
  }
}

interface Member {
  readonly get?: () => unknown;
  readonly set?: (value: unknown) => void;
}

type Members = Readonly<Record<string, Member>>;

/** The proxy traps: a modelled member is served; anything else — read, write, `in`, delete — throws, naming it. */
function strictTraps(members: Members, owner: string): ProxyHandler<object> {
  const refuse = (key: string | symbol, how: string): never => {
    const name = String(key);
    const sink = ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'write', 'writeln'].includes(name) ? ' (an HTML sink)' : '';
    throw new Error(`strict DOM: ${how} ${owner}.${name}${sink} is not modelled`);
  };

  return {
    get: (_target, key) => (typeof key === 'string' && members[key]?.get !== undefined ? members[key].get() : refuse(key, 'reading')),
    set: (_target, key, value) => {
      const member = typeof key === 'string' ? members[key] : undefined;
      if (member?.set === undefined) {
        return refuse(key, 'writing');
      }
      member.set(value);
      return true;
    },
    has: (_target, key) => typeof key === 'string' && key in members,
    deleteProperty: (_target, key) => refuse(key, 'deleting'),
    defineProperty: (_target, key) => refuse(key, 'defining'),
  };
}

function stringOnly(value: unknown, what: string): string {
  assert.ok(typeof value === 'string', `${what} takes a string, got ${typeof value} — a browser would print it, the panel must not`);
  return value;
}

function booleanOnly(value: unknown, what: string): boolean {
  assert.ok(typeof value === 'boolean', `${what} takes a boolean, got ${typeof value}`);
  return value;
}

function setAttribute(element: Element, name: unknown, value: unknown): void {
  const key = stringOnly(name, 'setAttribute(name)');
  const text = stringOnly(value, `setAttribute(${key})`);
  if (key.startsWith('data-')) {
    element.dataset[camel(key.slice(5))] = text;
    return;
  }
  assert.ok(ATTRIBUTES.has(key) || key.startsWith('aria-'), `strict DOM: setAttribute(${JSON.stringify(key)}) is not modelled — never on*, style, src or href`);
  element.attributes[key] = text;
}

/** Places `child` under `parent`, refusing a cycle (a browser throws HierarchyRequestError too). */
function adopt(parent: Element, child: Element): void {
  assert.ok(child !== parent && !child.descendants().includes(parent), 'appendChild: an element cannot be placed inside itself');
  if (child.parent !== undefined) {
    child.parent.children.splice(child.parent.children.indexOf(child), 1);
  }
  child.parent = parent;
  parent.children.push(child);
}

function datasetView(element: Element): object {
  return new Proxy(element.dataset, {
    set: (target, key, value) => {
      target[String(key)] = stringOnly(value, `dataset.${String(key)}`);
      return true;
    },
  });
}

function classListView(element: Element): object {
  const names = (): string[] => element.className.split(/\s+/).filter((n) => n.length > 0);
  const write = (list: readonly string[]): void => { element.className = list.join(' '); };

  return {
    add: (...added: string[]) => write([...new Set([...names(), ...added])]),
    remove: (...gone: string[]) => write(names().filter((n) => !gone.includes(n))),
    contains: (name: string) => names().includes(name),
  };
}

function textMember(element: Element): Member {
  return {
    get: () => element.textContent,
    set: (value) => {
      element.text = stringOnly(value, 'textContent');
      for (const child of element.children.splice(0)) {
        child.parent = undefined;
      }
    },
  };
}

function treeMembers(element: Element, membrane: Membrane): Members {
  const fn = (run: (...args: unknown[]) => unknown): Member => ({ get: () => run });

  return {
    appendChild: fn((child) => { adopt(element, membrane.element(child, 'appendChild')); return child; }),
    replaceChildren: fn((...children) => {
      const adopted = children.map((c) => membrane.element(c, 'replaceChildren'));
      for (const old of element.children.splice(0)) {
        old.parent = undefined;
      }
      adopted.forEach((c) => adopt(element, c));
    }),
    children: { get: () => element.children.map((c) => membrane.view(c)) },
    querySelector: fn((selector) => { const hit = element.all(stringOnly(selector, 'querySelector'))[0]; return hit === undefined ? null : membrane.view(hit); }),
    querySelectorAll: fn((selector) => element.all(stringOnly(selector, 'querySelectorAll')).map((e) => membrane.view(e))),
  };
}

function elementMembers(element: Element, membrane: Membrane): Members {
  return {
    tagName: { get: () => element.tagName },
    textContent: textMember(element),
    className: { get: () => element.className, set: (v) => { element.className = stringOnly(v, 'className'); } },
    hidden: { get: () => element.hidden, set: (v) => { element.hidden = booleanOnly(v, 'hidden'); } },
    disabled: { get: () => element.disabled, set: (v) => { element.disabled = booleanOnly(v, 'disabled'); } },
    dataset: { get: () => datasetView(element) },
    classList: { get: () => classListView(element) },
    setAttribute: { get: () => (name: unknown, value: unknown) => setAttribute(element, name, value) },
    getAttribute: { get: () => (name: unknown) => element.attributes[stringOnly(name, 'getAttribute')] ?? null },
    addEventListener: {
      get: () => (kind: unknown, run: unknown) => {
        const name = stringOnly(kind, 'addEventListener(kind)');
        assert.ok(ELEMENT_EVENTS.has(name), `strict DOM: listening for ${name} on an element is not modelled`);
        assert.ok(typeof run === 'function', 'addEventListener(run) takes a function');
        element.handlers.push({ kind: name, run: run as Handler });
      },
    },
    ...treeMembers(element, membrane),
  };
}

interface Listeners {
  readonly window: Handler[];
}

function documentView(membrane: Membrane, ids: Readonly<Record<string, Element>>, body: Element): object {
  const members: Members = {
    getElementById: { get: () => (id: unknown) => { const hit = ids[stringOnly(id, 'getElementById')]; return hit === undefined ? null : membrane.view(hit); } },
    createElement: {
      get: () => (tag: unknown) => {
        const name = stringOnly(tag, 'createElement').toUpperCase();
        assert.ok(CREATABLE.has(name), `strict DOM: createElement(${JSON.stringify(tag)}) is not modelled — a text page creates no ${name}`);
        return membrane.view(new Element(name));
      },
    },
    body: { get: () => membrane.view(body) },
    querySelector: { get: () => (selector: unknown) => { const hit = body.all(stringOnly(selector, 'querySelector'))[0]; return hit === undefined ? null : membrane.view(hit); } },
  };

  return new Proxy({}, strictTraps(members, 'document'));
}

function windowView(listeners: Listeners): object {
  const members: Members = {
    addEventListener: {
      get: () => (kind: unknown, run: unknown) => {
        assert.equal(kind, 'message', `strict DOM: window listens for message only, not ${String(kind)}`);
        assert.ok(typeof run === 'function', 'addEventListener(run) takes a function');
        listeners.window.push(run as Handler);
      },
    },
  };

  return new Proxy({}, strictTraps(members, 'window'));
}

function vscodeApi(posted: unknown[]): () => object {
  let acquired = false;

  return () => {
    assert.ok(!acquired, 'acquireVsCodeApi can only be called once — VS Code throws on a second call');
    acquired = true;
    return new Proxy({}, strictTraps({ postMessage: { get: () => (message: unknown) => { posted.push(structuredClone(message)); } } }, 'vscode'));
  };
}

/** Bubble a click: the element's own click listeners, then each ancestor's. */
function bubble(element: Element, membrane: Membrane): void {
  for (let at: Element | undefined = element; at !== undefined; at = at.parent) {
    const event = { type: 'click', target: membrane.view(element), currentTarget: membrane.view(at) };
    at.handlers.filter((h) => h.kind === 'click').forEach((h) => h.run(event));
  }
}

function attached(element: Element, roots: readonly Element[]): boolean {
  let top = element;
  while (top.parent !== undefined) {
    top = top.parent;
  }

  return roots.includes(top);
}

/**
 * Run a page script. `ids` are what `document.getElementById` answers with (each placed under `document.body`); every
 * other element the page has, it created itself.
 */
export function runPageScript(script: string, ids: Readonly<Record<string, Element>> = {}): Page {
  const posted: unknown[] = [];
  const listeners: Listeners = { window: [] };
  const membrane = new Membrane();
  const body = new Element('BODY');
  Object.values(ids).forEach((element) => adopt(body, element));
  const sandbox: Record<string, unknown> = {
    document: documentView(membrane, ids, body),
    window: windowView(listeners),
    acquireVsCodeApi: vscodeApi(posted),
  };
  const context = createContext(sandbox);
  runInContext(script, context, { timeout: TIMEOUT_MS });

  return {
    posted,
    ids,
    message: (data) => runBounded(context, sandbox, () => listeners.window.forEach((run) => run({ data }))),
    click: (element) => {
      assert.ok(attached(element, [body]), `this ${element.tagName} was clicked but is not in the running page`);
      runBounded(context, sandbox, () => bubble(element, membrane));
    },
  };
}
