/**
 * The kit's stylesheet is imported for its effect — esbuild emits it beside the script as `dist/webview/full.css` — and a
 * stylesheet has no types. TypeScript 6 refuses a side-effect import it cannot resolve to a module, so the one specifier
 * the page files may use is declared here, by name and not by a `*.css` wildcard.
 */
declare module '@oleksandrdubyna88/vscode-webview-kit/components.css';
