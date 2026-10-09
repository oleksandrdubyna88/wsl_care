# PLAN — the extension's Marketplace publish through Entra ID, before global PATs die

> Status: **plan only, 2026-10-09, nothing implemented yet.** Scope: `.github/workflows/release-extension.yml`'s
> `publish-marketplace` job, `docs/repo-settings.md` step 9, `POST_DEPLOY.md` item 12, the release workflow tests.
>
> Related docs: [docs/repo-settings.md](../docs/repo-settings.md) step 9, [architecture.md](../research/architecture.md)
> § *The extension: Install daemon, packaging and its release*.

## 1. Symptom and goal

- **What happens on 2026-12-01.** Since 2026-10-09 15:51Z the extension publishes with `VSCE_PAT`, stored in the
  `marketplace` Environment by the owner. A Marketplace PAT has to be a **global** PAT (organisation "All accessible
  organizations"), and Azure DevOps stops honouring every global PAT on **2026-12-01**. Organisation-scoped Marketplace PATs
  are an open request (microsoft/vsmarketplace#2121). From that day `vsce publish` fails, and releases fall back to the
  manual upload.
- **Goal.** `vsce publish` runs with `--azure-credential`.
  - The token comes from GitHub's OIDC, exchanged by `azure/login` for an Entra token.
  - That token belongs to an **app registration added as a member of the publisher `remsoftdev`**.
  - No stored secret.
  - The Environment's protection (required reviewer `oleksandrdubyna88`, `extension-v*` tags only) stays exactly as it is.
- **Other repos, not this plan.** ConnectOtherAIs (coai) and CredsForDevs publish their extensions the same way and need the
  same move before 2026-12-01, each in its own repository.

## 2. What exists (verified 2026-10-09)

- `.github/workflows/release-extension.yml:277` — the `publish-marketplace` job.
  - `:284` `environment: marketplace`.
  - `:285` its `permissions` (`contents: read` only).
  - `:325` `vsce show "$EXTENSION_ID" --json` (the "already served" check).
  - `:333` `VSCE_PAT: ${{ secrets.VSCE_PAT }}`.
  - `:340` `vsce publish --packagePath "from-build/ai-os-care-$VERSION.vsix"`.
- `.github/workflows/release-extension.yml:51-61` — the header already describes the OIDC alternative.
- `:191-194` — the `attest` job is today the ONLY job with `id-token: write`.
- `src_daemon/tests/WslCare.Scenarios/ReleaseExtensionWorkflowTests.cs:63` and `ReleaseWorkflowTests.cs` pin each job's
  permissions; `id-token` belongs to `attest` alone.
- `POST_DEPLOY.md` item 12 — the publish credential's expiry; `VSCE_PAT expires: none — OIDC` is already a passing form.

## 3. Design

1. **Entra, the owner's browser steps** (recorded in `docs/repo-settings.md` step 9, nothing in code):
   - an app registration;
   - a federated credential:
     - issuer `https://token.actions.githubusercontent.com`;
     - subject `repo:oleksandrdubyna88/wsl_care:environment:marketplace`;
     - audience `api://AzureADTokenExchange`;
   - the app added to the publisher `remsoftdev` (*Manage → Members*, role Contributor). Whether a service principal can be
     a member, and with which role, is checked first. The Marketplace's own docs must say so; if they do not, the plan
     stops there and asks.
2. **The job.** `publish-marketplace` gains:
   - `id-token: write`;
   - an `azure/login` step, SHA-pinned, with `client-id` / `tenant-id` from Environment VARIABLES (not secrets) and
     `allow-no-subscriptions: true`, before the publish;
   - `--azure-credential` on `vsce publish` only. `vsce show` stays as it is: it is the gallery's PUBLIC query, and
     vsce 4.0.0's `show` has no such option (`vsce publish --help` lists `--azure-credential`, `vsce show --help` does not;
     checked 2026-10-09).

   **No inline PAT fallback** (plan round 2026-10-09, findings 2 and 4): a fallback step cannot run after a failed publish
   step, and a PAT that still publishes would hide a broken OIDC path. So the same PR removes `VSCE_PAT` from the publish
   step, and the OIDC path must publish once with the PAT UNAVAILABLE to the job. The stored secret stays in the Environment,
   unread, only as a manual rollback (revert the PR), and is deleted after that first OIDC publish.
3. **The tests.** `ReleaseExtensionWorkflowTests` and `ReleaseWorkflowTests` allow `id-token: write` on `publish-marketplace`
   too. The pin stays exact, so a third job asking for it fails. A test also holds `azure/login` SHA-pinned, holds
   `--azure-credential` on `vsce publish`, and holds that no step of the job reads `secrets.VSCE_PAT`.
4. **Records.** `POST_DEPLOY.md` gets `VSCE_PAT expires: none — OIDC` once the first OIDC publish is observed.
   `docs/repo-settings.md` step 9 describes OIDC as the way.

## 4. Build order

1. The owner's Entra steps, and the Environment variables `AZURE_CLIENT_ID` and `AZURE_TENANT_ID`.
2. The workflow + tests PR (RED first: the permission pins and the `--azure-credential` assertions fail before the change).
3. An extension release through the Environment's approval: observe `vsce publish` with the Entra token and no PAT in the job.
4. Delete the stored `VSCE_PAT` and record `none — OIDC`.

## 5. Test plan

- Workflow tests:
  - the job permissions (exactly `attest` and `publish-marketplace` hold `id-token: write`);
  - the SHA pin on `azure/login`;
  - `--azure-credential` on `vsce publish`;
  - no step of `publish-marketplace` reads `secrets.VSCE_PAT`.
- Live: one extension release published by OIDC (`POST_DEPLOY.md` item 6 afterwards).

## 6. Definition of Done

- [ ] The Entra app registration is a member of `remsoftdev` with a federated credential for the `marketplace` Environment.
- [ ] `publish-marketplace` publishes with `--azure-credential`; the workflow tests hold the permissions and the pins.
- [ ] One extension release published that way with no PAT in the job; the stored `VSCE_PAT` deleted; `POST_DEPLOY.md` says
      `none — OIDC`.
- [ ] `docs/repo-settings.md` step 9 describes OIDC as the way; the Environment's reviewer and tag policy unchanged.
- [ ] Done before 2026-12-01.

## 7. Plan round (coai session `18d556c9`, 2026-10-09) — proceed

- **Accepted:**
  - (0) `vsce show` has no `--azure-credential` — only `publish` takes it. The service-principal membership is to be
    verified before building.
  - (2, 4) no inline PAT fallback; the OIDC path publishes once with no PAT in the job.
- **Rejected:**
  - (1) writing the PAT's expiry into `POST_DEPLOY.md` here: only the owner knows the date. Step 9 asks for it.
  - (3, 5) plans in the coai and CredsForDevs repositories: those are other repositories, and this task is scoped to
    wsl_care. The need and the deadline are named here and handed to the coordinator.
