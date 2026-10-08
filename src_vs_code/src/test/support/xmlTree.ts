/**
 * A small, STRICT XML reader for the tests of generated XML (`common.generated-code-tests` §1: assert over the parsed form,
 * never a substring): elements, attributes, text and the five predefined entities — and nothing else. Anything outside
 * that subset (a comment, CDATA, a DOCTYPE, an unknown entity, a mismatched or unclosed tag, text outside the root) THROWS,
 * so a fake that is stricter than a real parser can only fail loudly. Its own tests are in `xmlTree.test.ts`.
 */

export interface XmlElement {
  readonly name: string;
  readonly attributes: Readonly<Record<string, string>>;
  readonly children: readonly XmlElement[];
  /** The element's own text (its text nodes joined), entities decoded. */
  readonly text: string;
}

const ENTITIES: Readonly<Record<string, string>> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };

function decode(raw: string): string {
  return raw.replace(/&([a-z]+);|&/g, (whole, name: string | undefined) => {
    const value = name === undefined ? undefined : ENTITIES[name];
    if (value === undefined) {
      throw new Error(`xmlTree: unknown or bare entity at "${whole}"`);
    }
    return value;
  });
}

function attributesOf(raw: string): Record<string, string> {
  const attributes: Record<string, string> = {};
  const rest = raw.replace(/\s*([A-Za-z_:][\w:.-]*)\s*=\s*"([^"<]*)"/g, (_m, name: string, value: string) => {
    attributes[name] = decode(value);
    return '';
  });
  if (rest.trim() !== '') {
    throw new Error(`xmlTree: unreadable attributes "${raw}"`);
  }
  return attributes;
}

interface Open {
  readonly name: string;
  readonly attributes: Record<string, string>;
  readonly children: XmlElement[];
  text: string;
}

function close(stack: Open[], name: string): XmlElement {
  const open = stack.pop();
  if (open === undefined || open.name !== name) {
    throw new Error(`xmlTree: </${name}> closes ${open === undefined ? 'nothing' : `<${open.name}>`}`);
  }
  return { name: open.name, attributes: open.attributes, children: open.children, text: open.text };
}

function attach(stack: Open[], element: XmlElement, roots: XmlElement[]): void {
  const parent = stack.at(-1);
  if (parent === undefined) {
    roots.push(element);
  } else {
    parent.children.push(element);
  }
}

function tag(stack: Open[], roots: XmlElement[], body: string): void {
  if (body.startsWith('/')) {
    attach(stack, close(stack, body.slice(1).trim()), roots);
    return;
  }
  const selfClosing = body.endsWith('/');
  const inner = selfClosing ? body.slice(0, -1) : body;
  const match = /^([A-Za-z_][\w:.-]*)([\s\S]*)$/.exec(inner);
  if (match === null) {
    throw new Error(`xmlTree: unreadable tag <${body}>`);
  }
  const open: Open = { name: match[1] ?? '', attributes: attributesOf(match[2] ?? ''), children: [], text: '' };
  if (selfClosing) {
    attach(stack, { name: open.name, attributes: open.attributes, children: [], text: '' }, roots);
  } else {
    stack.push(open);
  }
}

/** The document's one root element. */
export function parseXml(xml: string): XmlElement {
  const body = xml.replace(/^<\?xml[^?]*\?>/, '');
  if (/<!|<\?/.test(body)) {
    throw new Error('xmlTree: comments, CDATA, DOCTYPE and processing instructions are outside the subset');
  }
  const stack: Open[] = [];
  const roots: XmlElement[] = [];
  for (const part of body.split(/(<[^<>]*>)/)) {
    if (part.startsWith('<')) {
      tag(stack, roots, part.slice(1, -1));
    } else if (part.trim() !== '') {
      const open = stack.at(-1);
      if (open === undefined) {
        throw new Error(`xmlTree: text outside the root: "${part.trim()}"`);
      }
      open.text += decode(part);
    }
  }
  if (stack.length !== 0 || roots.length !== 1) {
    throw new Error(`xmlTree: ${stack.length} unclosed element(s), ${roots.length} root(s)`);
  }
  return roots[0] as XmlElement;
}

/** The one child named `name` — it throws when there is none or several. */
export function child(element: XmlElement, name: string): XmlElement {
  const found = element.children.filter((c) => c.name === name);
  if (found.length !== 1) {
    throw new Error(`xmlTree: <${element.name}> has ${found.length} <${name}>`);
  }
  return found[0] as XmlElement;
}

/** Follows a path of single children: `at(task, 'Settings', 'ExecutionTimeLimit')`. */
export function at(element: XmlElement, ...names: string[]): XmlElement {
  return names.reduce((e, n) => child(e, n), element);
}
