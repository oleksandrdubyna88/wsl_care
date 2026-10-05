import * as ts from 'typescript';

/**
 * The page-source scan both webviews' tests share (E5.S2, shared since E6.S4): every identifier and string literal of a page
 * script, read by the parser, so a comment that names a sink is no finding and a sink in any spelling is.
 */

/** What a page script must never name (read by the parser — a comment naming one is no finding). */
export const FORBIDDEN_NAMES = ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'write', 'writeln', 'eval', 'Function', 'srcdoc', 'setTimeout', 'setInterval', 'fetch'];

/** Every identifier and string literal of a page script, read by the TypeScript parser. */
export function namesIn(text: string): string[] {
  const found: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isIdentifier(node) || ts.isPrivateIdentifier(node)) {
      found.push(node.text);
    }
    if (ts.isStringLiteralLike(node)) {
      found.push(node.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(ts.createSourceFile('panel.js', text, ts.ScriptTarget.ES2022, true, ts.ScriptKind.JS));

  return found;
}
