import * as ts from 'typescript';

/**
 * Source read as a PROGRAM, not as text (`common.generated-code-tests` §1: parsed is behaviour, a substring is not):
 * the TypeScript compiler's own parser names every module a file imports — in every form — and every string literal
 * it holds, so a comment that mentions `child_process` or `--exec` is not a finding and a call spread over three lines
 * still is.
 */

/** Every module specifier a source imports: `import … from`, `import … =  require()`, `require()`, `import()`, `export … from`. */
export function importsOf(text: string, kind: ts.ScriptKind = ts.ScriptKind.TS): string[] {
  const found: string[] = [];
  const visit = (node: ts.Node): void => {
    const specifier = specifierOf(node);
    if (specifier !== undefined) {
      found.push(specifier);
    }
    ts.forEachChild(node, visit);
  };
  visit(ts.createSourceFile('scan.ts', text, ts.ScriptTarget.ES2022, true, kind));

  return found;
}

function specifierOf(node: ts.Node): string | undefined {
  if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) && node.moduleSpecifier !== undefined && ts.isStringLiteral(node.moduleSpecifier)) {
    return node.moduleSpecifier.text;
  }
  if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference) && ts.isStringLiteral(node.moduleReference.expression)) {
    return node.moduleReference.expression.text;
  }
  if (ts.isCallExpression(node) && isRequireOrImport(node) && node.arguments[0] !== undefined && ts.isStringLiteralLike(node.arguments[0])) {
    return node.arguments[0].text;
  }

  return undefined;
}

function isRequireOrImport(call: ts.CallExpression): boolean {
  return call.expression.kind === ts.SyntaxKind.ImportKeyword || (ts.isIdentifier(call.expression) && call.expression.text === 'require');
}

/** Every string a source spells: string literals, template literals without substitutions, and each template part. */
export function stringLiteralsOf(text: string, kind: ts.ScriptKind = ts.ScriptKind.TS): string[] {
  return stringLiteralsAt(text, kind).map((literal) => literal.text);
}

/** The same strings with the offset each starts at — what lets a scan say WHICH part of a bundle spelt one (E6.S2). */
export function stringLiteralsAt(text: string, kind: ts.ScriptKind = ts.ScriptKind.TS): { readonly text: string; readonly start: number }[] {
  const found: { text: string; start: number }[] = [];
  const file = ts.createSourceFile('scan.ts', text, ts.ScriptTarget.ES2022, true, kind);
  const visit = (node: ts.Node): void => {
    if (ts.isStringLiteralLike(node) || ts.isTemplateHead(node) || ts.isTemplateMiddle(node) || ts.isTemplateTail(node)) {
      found.push({ text: node.text, start: node.getStart(file) });
    }
    ts.forEachChild(node, visit);
  };
  visit(file);

  return found;
}

/** Whether a module specifier names Node's child_process, with or without the `node:` scheme. */
export function isChildProcess(specifier: string): boolean {
  return specifier === 'child_process' || specifier === 'node:child_process';
}
