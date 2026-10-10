# PLAN — the extension's Marketplace publish through Entra ID, before global PATs die

> Status: **plan only, 2026-10-09 (aligned with the coai and CredsForDevs plans 2026-10-10, § 8), nothing implemented yet.** Scope: `.github/workflows/release-extension.yml`'s
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

1. **Entra, the owner's browser steps** (recorded in `docs/repo-settings.md` step 9, nothing in code). Aligned 2026-10-10
   with the coai and CredsForDevs plans (coai #725, CredsForDevs #199, both merged; § 8):
   - the family's SHARED user-assigned managed identity (`remsoftdev-marketplace-publisher`, made once by whichever
     repository goes first and reused by the others), not an app registration of this repository's own;
   - ONE federated credential for this repository on that identity:
     - issuer `https://token.actions.githubusercontent.com`;
     - subject **`repo:oleksandrdubyna88@71817001/wsl_care@1401318131:environment:marketplace`**. This repository emits
       GitHub's IMMUTABLE subject (`gh api repos/oleksandrdubyna88/wsl_care/actions/oidc/customization/sub` →
       `use_immutable_subject: true`, prefix `repo:oleksandrdubyna88@71817001/wsl_care@1401318131`, checked 2026-10-10;
       <https://learn.microsoft.com/en-us/entra/workload-id/workload-identities-github-immutable-subjects>). The name form
       `repo:oleksandrdubyna88/wsl_care:environment:marketplace`, which this plan carried until 2026-10-10, would be accepted
       by Entra and never match a token;
     - audience `api://AzureADTokenExchange`;
   - the identity a member of the publisher `remsoftdev` (*Manage → Members*). Whether a managed identity can be a member,
     and with which role, is checked first. The Marketplace's own docs must say so; if they do not, the plan stops there and
     asks.
2. **The job.** `publish-marketplace` gains:
   - `id-token: write`;
   - an `azure/login` step, SHA-pinned, with `client-id` / `tenant-id` from Environment VARIABLES (not secrets) and
     `allow-no-subscriptions: true`, before the publish;
   - `--azure-credential` on `vsce publish` only. `vsce show` stays as it is: it is the gallery's PUBLIC query, and
     vsce 4.0.0's `show` has no such option (`vsce publish --help` lists `--azure-credential`, `vsce show --help` does not;
     checked 2026-10-09). The workflow's header comment said otherwise until 2026-10-10; corrected.
   - **every command carrying `--azure-credential` runs as `env -u VSCE_PAT …`** (from vsce's source, coai plan F5): `--pat`
     defaults to `process.env.VSCE_PAT` and a PAT is tried FIRST, so a `VSCE_PAT` left in a step's environment silently
     wins and the Entra road would never be exercised — until 2026-12-01, when it breaks.
   - a membership preflight `env -u VSCE_PAT vsce verify-pat remsoftdev --azure-credential` before the publish. It proves the
     identity is a MEMBER, not that it may publish: `verify-pat` succeeds for any role, Reader included (coai plan F6). Only
     the first real publish proves the right.

   **The road is a switch, not a fallback** (aligned with coai's § 3.4): an Environment variable `MARKETPLACE_AUTH` in
   `marketplace` — `entra` | `pat` | `manual` — picks ONE road per run. A run never falls back from one road to another (the
   plan round's findings 2 and 4 stand: a PAT that still publishes would hide a broken Entra road). Rolling back is setting
   `MARKETPLACE_AUTH=pat` and re-running the failed job (only before 2026-12-01), or `manual` (the attested `.vsix` uploaded by
   hand, step 9). The stored `VSCE_PAT` is deleted after the first Entra publish; `pat` is then no road at all.

   **A one-time probe workflow** (coai's § 3.5): dispatched on `main` against the `marketplace` Environment, it runs
   `azure/login` and the membership preflight and publishes nothing — the owner's dry check after the browser steps, before
   any release depends on them. **It proves the LOGIN and the MEMBERSHIP only** (plan round `bafae73a`): a Reader passes it
   too. So the publishing ROLE is its own prerequisite — the identity listed under the publisher's *Manage → Members* with
   **Contributor** or **Owner**, checked by the owner in that page — and `MARKETPLACE_AUTH` is set to `entra` only after it.
   The first Entra release is what proves the right to publish; until then `pat` (before 2026-12-01) or `manual` stays the
   road.
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

## 8. Aligned with the coai and CredsForDevs plans (2026-10-10, plan only — no workflow behaviour changed)

The coai plan (dew_flow_connect_other_ais #725) and the CredsForDevs plan (dew_flow_creds_for_devs #199), both merged,
found two errors in this plan's first sketch and three facts it lacked. Corrected above:

- **The subject.** This repository emits the IMMUTABLE OIDC subject, so the federated credential's subject is
  `repo:oleksandrdubyna88@71817001/wsl_care@1401318131:environment:marketplace`, not the name form (§ 3.1; verified here with
  `gh api repos/oleksandrdubyna88/wsl_care/actions/oidc/customization/sub`).
- **`vsce show` takes no credential.** The workflow's header comment put `--azure-credential` on it. Only `vsce publish` takes it.
- **A `VSCE_PAT` in the environment beats `--azure-credential`**, so every such command runs as `env -u VSCE_PAT …` (§ 3.2).
- **`verify-pat` succeeds for any publisher role**, so it is a membership preflight, not proof of the right to publish (§ 3.2).
- **One shared managed identity, one switch, one probe:** the family's user-assigned managed identity with one federated
  credential per repository; `MARKETPLACE_AUTH` (`entra` | `pat` | `manual`) as the per-run road; a one-time probe workflow
  as the dry check (§ 3.1, § 3.2).

**The boundary — what wsl_care does and does not do** (plan round `bafae73a`):
- **wsl_care makes:** ONE federated credential, its own, with the subject above.
- **The shared identity:** wsl_care provisions it only if no other repository already has. The coai plan's operator steps say
  the same from their side ("if another repository did this first, reuse that identity").
- **Other repositories' plans:** this plan does not edit them. That is the owner's, and the CredsForDevs repository is not
  this agent's to change.

The `docs/repo-settings.md` step 9 text and the workflow change itself come with the build (§ 4), not here.
