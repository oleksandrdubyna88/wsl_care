# PLAN — extract coai's help / language / text-size / tone modules into one shared npm package

> Status: **proposal, nothing implemented yet (2026-10-02).** Scope: a new repository and npm package,
> and the switch of `wsl_care` (first consumer) and `dew_flow_connect_other_ais` (source) to it.
> CredsForDevs may join later.
>
> Parent plan: [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) §7.6 and §13 Q1.

## 1. Why

`wsl_care` needs coai's help page, language switch with fallback, text size (`uiScale`) and brightness
(`textTone`). Copying them makes two drifting copies on day one — and the stale-translation detection
(coai `todo/PLAN_a_stale_translation_is_invisible.md`) would then be built twice or only once.

## 2. Where — a new repository, not `dew_flow_conventions`

| Option | Verdict |
|---|---|
| `dew_flow_conventions` | **No.** It holds shared *rules* and tools, consumed as a git submodule pinned to a release branch; product code there would ride every rule-pin cascade. |
| A git submodule of a new repo | **No.** Submodule pins go stale on every merge (measured pain in this family); no version ranges, no Dependabot. |
| GitHub Packages npm registry | **No.** Even public packages need an auth token to install — friction in every CI and every fresh clone. |
| **New repo `dew_flow_vscode_kit` → public npm package `@oleksandrdubyna88/vscode-webview-kit`** | **Yes.** Versioned, Dependabot updates consumers, both extensions already bundle with esbuild so the package is compiled *into* `dist/extension.js` and ships nothing extra in the `.vsix`. |

## 3. What goes in — and what stays in each extension

| Module (from `dew_flow_connect_other_ais/src_vs_code/src/`) | In the kit as | Made generic by |
|---|---|---|
| `helpContent.ts` (types, `HELP_LANGUAGES`, `bodyFor` fallback), `helpPage.ts` (`renderHelpHtml`, mini-markup, search index, client routing), `helpPanel.ts` | `help/` | articles, language list and command id passed in; **the articles themselves stay in each extension** |
| stale-translation digests (coai plan, not built yet) | `help/digest.ts` + a `vscode-kit help-stamp` CLI | built once, here |
| `zoomControl.ts` + `uiScaleHost.ts` | `display/zoom` | the configuration section name is a parameter (`coai` / `wslCare`) |
| `textTone.ts` + `textToneHost.ts` | `display/tone` | same; CSS variable prefix a parameter |
| `settingWrite.ts` | `settings/` | the notify function injected |
| `webviewHtml.ts` (`escapeHtml`, `jsonForScript`, CSP nonce) | `webview/` | — |

Pure modules (no `vscode` import) are separated from host modules; `vscode` is a peer dependency through
`@types/vscode` only, marked external by each consumer's esbuild — as today.

## 4. How

1. Create the repo with the family's machinery (release-please `node` component, `npm publish
   --provenance` from Actions on the tag, Dependabot, branch protection, CodeRabbit, Sonar), MIT licence.
2. Move the modules **with their tests** from coai (`helpCoverage`'s generic half, `textTone.test.ts`, the
   zoom tests, the page-script-runs-in-a-synthetic-document helpers). Behaviour must be byte-identical:
   the coai tests keep passing against the kit before coai switches.
3. Build the stale-translation digests in the kit.
4. `wsl_care` consumes the kit from its first extension commit.
5. coai switches its imports in one PR; its own help articles and help tests (coverage of its commands and
   settings) stay in coai.
6. CredsForDevs adopts it when it gets a help page.

## 5. Test plan

- The moved tests pass unchanged in the kit; coai's suite passes after the switch with no snapshot
  changes in the rendered help/zoom/tone HTML.
- A consumer-shaped test: an extension with section `demo` gets `demo.uiScale` / `demo.textTone` /
  `demo.helpLanguage` wired, and the page receives the pushed values.
- `help-stamp`: changing one English article marks exactly that article stale in every language.

## 6. Definition of Done

- [ ] `@oleksandrdubyna88/vscode-webview-kit` published from CI with provenance.
- [ ] `wsl_care` and coai both import it; no copy of these modules remains in coai.
- [ ] Stale-translation detection works in both consumers.

## 7. Open questions

1. Package scope: `@oleksandrdubyna88/…` (personal) or an organisation scope?
2. Does the kit also take coai's live-regions helpers, or only help + display?
