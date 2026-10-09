# Repository settings — what the owner applies once

The release pipeline (`release-please.yml` → `release.yml`, E4.S2 of `todo/PLAN_wsl_care_daemon.md`) needs a few
things that are **GitHub settings, not repository content**: an App installation, secrets, two rulesets, and two
third-party apps. No workflow and no agent applies them — the owner does, once, with their own `gh` login, in the order
below. The ruleset bodies live in `.github/rulesets/` so the intent is reviewable; `ReleaseConfigTests` keeps them in
step with the workflows (the tag the release triggers on, the check names a pull request really reports).

Run from the repository root in Git Bash or WSL, logged in as the repository's owner (`gh auth status`), with `node` on
`PATH` (it only rewrites a JSON body before it is sent):

```bash
REPO=oleksandrdubyna88/wsl_care
MAIN_SHA="$(gh api "repos/$REPO/commits/main" --jq .sha)"
```

State read on 2026-10-03: no ruleset (`gh api repos/$REPO/rulesets` → `[]`), `main` unprotected, CodeQL default setup
not configured, no secrets.

## 1. Install the release App on this repository (browser)

The App is `dew-flow-release-please`, the one the family's other releasing repositories use. Installing an App on a
repository has no API — open <https://github.com/settings/apps/dew-flow-release-please/installations>, *Configure*, and
add `wsl_care` under *Only select repositories*. The App needs Contents and Pull requests read & write (as configured
for the family).

## 2. Its two secrets (Actions store)

The App ID is the number on the App's settings page (<https://github.com/settings/apps/dew-flow-release-please>); the
family's rulesets name it as **5018284** — confirm it is the same number. The private key is a `.pem` generated on that
page.

```bash
gh secret set RELEASE_PLEASE_APP_ID -R "$REPO" --body '<the App ID>'
gh secret set RELEASE_PLEASE_APP_PRIVATE_KEY -R "$REPO" < /path/to/dew-flow-release-please.private-key.pem
```

From PowerShell, where `<` is a parse error: `Get-Content -Raw C:\path\to\key.pem | gh secret set
RELEASE_PLEASE_APP_PRIVATE_KEY -R oleksandrdubyna88/wsl_care`. Never pass the PEM with `--body`: a multi-line value from
a Windows shell arrives mangled and fails later as a token that will not mint.

**Check:** `gh secret list -R "$REPO"` names both; dispatching `release-please.yml` prints *app credentials present* in
its first step (without them that step fails, saying which is missing — that is the designed refusal).

## 3. The tag ruleset — only the App creates a `daemon-v*` tag, nobody moves or deletes one

`.github/rulesets/tags-daemon.json`: `refs/tags/daemon-v*`, rules `creation` + `update` + `deletion`, bypass = the App
(`actor_id` 5018284, `Integration`, `always`) and nobody else — not the owner, not an admin. If step 2 showed a
different App ID, change `actor_id` in that file through a pull request first.

Reading a ruleset back proves nothing about whether it acts, so it is created first WITH a probe pattern that no
workflow triggers on, refused once for real, and then replaced by the file verbatim:

```bash
# a. create it with the probe pattern added (the file itself is not changed)
node -e 'const r=JSON.parse(require("fs").readFileSync(".github/rulesets/tags-daemon.json","utf8")); r.conditions.ref_name.include.push("refs/tags/zz-ruleset-probe-*"); process.stdout.write(JSON.stringify(r))' > /tmp/tags-probe.json
TAGS_ID="$(gh api --method POST "repos/$REPO/rulesets" --input /tmp/tags-probe.json --jq .id)"; echo "$TAGS_ID"

# b. PROBE: your own token is not a bypass actor, so this must be REFUSED (HTTP 422, rule violation)
gh api --method POST "repos/$REPO/git/refs" -f ref=refs/tags/zz-ruleset-probe-1 -f sha="$MAIN_SHA"

# c. only after (b) was refused: the ruleset exactly as the file says
gh api --method PUT "repos/$REPO/rulesets/$TAGS_ID" --input .github/rulesets/tags-daemon.json

# d. no probe tag exists, and the ruleset covers the release tags
gh api "repos/$REPO/git/matching-refs/tags/zz-ruleset-probe" --jq length        # 0
gh api "repos/$REPO/rulesets/$TAGS_ID" --jq '.conditions.ref_name.include'      # ["refs/tags/daemon-v*"]
```

If (b) **succeeds**, the ruleset does not act: remove the probe tag (`gh api --method DELETE
"repos/$REPO/git/refs/tags/zz-ruleset-probe-1"` — no workflow listens to `zz-*`), delete the ruleset (`gh api --method
DELETE "repos/$REPO/rulesets/$TAGS_ID"`) and stop — do not cut a release on an unprotected tag namespace.

## 4. The `main` ruleset — pull requests, linear history, the required checks

`.github/rulesets/branch-main.json`: the default branch can be neither deleted nor force-pushed, history stays linear
(squash or rebase merges), every change arrives by pull request with its conversations resolved, and these checks —
the names GitHub reported on pull request #7, all from GitHub Actions (`integration_id` 15368) — must pass on an
up-to-date branch: `daemon · build · test · aot (linux-x64)`, `(linux-arm64)`, `(win-x64)`, `workflows · actionlint`,
`family · plans · pins · adapter · build flags`, `pr · semantic title`, and since E5.S1 the extension's two legs
`extension · typecheck · lint · test (windows-latest)` and `(ubuntu-24.04)` (`ci-extension.yml`) — re-apply the file (E5 live
gate, step 3) only after a pull request has REPORTED those two, since a required check that never reports blocks every
merge. No bypass actor (the family's
`enforce_admins`). Deliberately not required: `SonarCloud Scan` (it passes while skipping without a token — step 6),
`ask CodeRabbit` (a third-party free tier that runs out), CodeQL (not set up — step 8).

The same probe shape, on a throwaway branch made BEFORE the ruleset exists:

```bash
# a. the probe branch, while nothing protects it
gh api --method POST "repos/$REPO/git/refs" -f ref=refs/heads/zz-ruleset-probe -f sha="$MAIN_SHA"

# b. the ruleset with the probe branch added
node -e 'const r=JSON.parse(require("fs").readFileSync(".github/rulesets/branch-main.json","utf8")); r.conditions.ref_name.include.push("refs/heads/zz-ruleset-probe"); process.stdout.write(JSON.stringify(r))' > /tmp/main-probe.json
MAIN_ID="$(gh api --method POST "repos/$REPO/rulesets" --input /tmp/main-probe.json --jq .id)"; echo "$MAIN_ID"

# c. PROBE: a direct push (not a pull request) to a protected branch must be REFUSED (HTTP 422, rule violation).
#    The commit object it tries to point at is a copy of main's tree and is never referenced by anything.
TREE="$(gh api "repos/$REPO/git/commits/$MAIN_SHA" --jq .tree.sha)"
PROBE="$(gh api --method POST "repos/$REPO/git/commits" -f message='ruleset probe' -f tree="$TREE" -f "parents[]=$MAIN_SHA" --jq .sha)"
gh api --method PATCH "repos/$REPO/git/refs/heads/zz-ruleset-probe" -f sha="$PROBE"

# d. only after (c) was refused: the ruleset exactly as the file says, then the probe branch goes (it is no longer covered)
gh api --method PUT "repos/$REPO/rulesets/$MAIN_ID" --input .github/rulesets/branch-main.json
gh api --method DELETE "repos/$REPO/git/refs/heads/zz-ruleset-probe"

# e. the rules GitHub now ENFORCES on main — its evaluation, not the stored body
gh api "repos/$REPO/rules/branches/main" --jq '.[].type'
# deletion, non_fast_forward, required_linear_history, pull_request, required_status_checks
```

If (c) **succeeds**, delete the ruleset and the probe branch and stop. Optional, for `gh pr merge --auto`:
`gh api --method PATCH "repos/$REPO" -F allow_auto_merge=true`.

## 5. The extension's settings are steps 9–11

Nothing in steps 1–4 is needed again for the extension, and nothing of the extension is needed for a daemon release. The
extension's own settings — the Marketplace publisher and its credential, the `marketplace` Environment, the
`extension-v*` tag ruleset — are steps 9–11 below, run at the **E5 live gate** (plan §16), after the E4 live gate's
stamp. The `main` ruleset of step 4 already names the extension's two checks; `ci-extension.yml`'s job names did not
change in E5.S3, so step 4 needs no second application for it.

## 6. SonarCloud (optional; the analysis skips loudly until it is done)

1. At <https://sonarcloud.io>, organisation `remsoftdev`, *Analyze new project* → `oleksandrdubyna88/wsl_care`. The
   project key must be **`remsoftdev_wsl_care`** — what `.github/sonar.properties` says.
2. *Administration → Analysis Method*: switch **Automatic Analysis OFF** (SonarCloud refuses a CI analysis while it is on).
3. *My Account → Security*: generate a token, then set it in BOTH secret stores — a Dependabot pull request reads only
   Dependabot's own store, and `secrets.SONAR_TOKEN` is empty there otherwise:

   ```bash
   gh secret set SONAR_TOKEN -R "$REPO"                    # Actions — prompts for the value
   gh secret set SONAR_TOKEN -R "$REPO" --app dependabot   # Dependabot
   ```

**Check:** the next push to `main` runs `SonarCloud Scan` without the *SonarCloud analysis SKIPPED* warning, and a
`SonarCloud Code Analysis` check appears on the commit (a check run, not a commit status). To make the scan a required
check afterwards, add `{ "context": "SonarCloud Scan", "integration_id": 15368 }` to `branch-main.json` and
`sonarcloud.yml` to `ReleaseConfigTests.GatingWorkflows` in one pull request, then re-run step 4 (d)'s PUT.

## 7. CodeRabbit (browser)

Install the CodeRabbit App for this repository: <https://github.com/apps/coderabbitai> → *Configure* → add `wsl_care`.
`.coderabbit.yaml` (language `ru-RU`, profile `chill`) is read from then on; below ten stars the free plan waits for a
request, which `coderabbit-review.yml` posts when a pull request opens. **Check:** the next pull request gets the
`@coderabbitai review` comment and, minutes later, a review.

## 8. CodeQL default setup (optional, plan §9)

```bash
gh api --method PATCH "repos/$REPO/code-scanning/default-setup" -f state=configured
gh api "repos/$REPO/code-scanning/default-setup" --jq .state     # configured
```

Not a required check until it has reported on a pull request (a required check that never reports blocks every merge).

## 9. The Marketplace publisher, its credential, and the `marketplace` Environment (E5 live gate, steps 1–2)

**The publisher (browser, permanent).** Sign in at <https://marketplace.visualstudio.com/manage> with the Microsoft
account that will own the extension and create a publisher. Its **id** is permanent and becomes part of the extension's
identity (`<publisher>.ai-os-care`); check first that the display name **AI OS Care** is free (search the Marketplace)
and that no extension `<id>.ai-os-care` exists. Then put the id into `src_vs_code/package.json` (`"publisher"`) through
a pull request — nothing else changes: the host scenarios and `POST_DEPLOY.md` item 6 read it from the manifest, and both
`release-extension-guard.sh` and `check-vsix.mjs --release` refuse the placeholder `publisher-tbd`.

**Done 2026-10-06:** the owner created the publisher **`remsoftdev`** (display name *RemSoftDev*); `package.json` carries
it, so the extension is **`remsoftdev.ai-os-care`**, displayed as **AI OS Care** (the owner's decision of the same day:
the extension id `ai-os-care`, permanent; the daemon keeps the name `wsl-care`, the setting and command keys stay
`wslCare.*`). The guard and `check-vsix.mjs --release` still refuse `publisher-tbd` — the repository no longer carries it.

**The credential — what was checked, 2026-10-04 (plan §15g m9).** `vsce publish` takes either a Personal Access Token
(`VSCE_PAT`) or, since vsce 3.x, `--azure-credential` (Microsoft Entra ID through `DefaultAzureCredential`). A Marketplace
PAT must be created with organisation **All accessible organizations** — a GLOBAL PAT — and scope **Marketplace →
Manage**. Azure DevOps retires global PATs: the creation block once planned for 2026-03-15 was withdrawn, but **every
global PAT stops working on 2026-12-01** (Azure DevOps blog, *Retirement of Global Personal Access Tokens*), and
organisation-scoped PATs for the Marketplace are an open request (microsoft/vsmarketplace#2121). So:

- **Decided 2026-10-06 (owner): the manual upload — no stored credential at all.** No `VSCE_PAT` secret and no Entra
  identity; the owner uploads the attested `.vsix` to the Marketplace by hand, and the workflow is unchanged. Record
  `VSCE_PAT expires: none — manual upload` in `POST_DEPLOY.md` (item 12 then passes: there is no credential to expire).
  Per release, after `release-extension.yml` started on the tag:
  1. **The draft carries the attested `.vsix`.** `github-draft` uploads the build's `.vsix` + `.sha256` onto the DRAFT
     release BEFORE `publish-marketplace` starts, so it is there while that job waits for your approval. A draft is
     visible only to people with write access, so download it as the owner:
     `gh release download extension-v<x.y.z> -R oleksandrdubyna88/wsl_care --pattern '*.vsix' --pattern '*.sha256'`,
     then `sha256sum -c ai-os-care-<x.y.z>.vsix.sha256` and `gh attestation verify ai-os-care-<x.y.z>.vsix --repo
     oleksandrdubyna88/wsl_care --cert-identity "https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release-extension.yml@refs/tags/extension-v<x.y.z>" --deny-self-hosted-runners`.
  2. **Upload THAT file by hand**: <https://marketplace.visualstudio.com/manage> → publisher `remsoftdev` → *AI OS Care*
     (the first time: *New extension → Visual Studio Code*) → upload `ai-os-care-<x.y.z>.vsix`. Never a local build: only
     the attested file may be served, and `github-public` compares the draft with it.
  3. **Wait until the Marketplace serves it** — it validates a new version first, and what `vsce show` reads can lag more than
     20 minutes behind the publish (extension 0.2.0, 2026-10-09; the job now waits up to 45 minutes, logging every attempt):
     `npx --yes @vscode/vsce@4.0.0 show remsoftdev.ai-os-care --json` lists `<x.y.z>` among its `versions`.
  4. **Check that the Marketplace serves THE ATTESTED BUILD — required before approving.** The job's served check
     matches the VERSION only: a wrong `.vsix` uploaded with the same version would be "served", the publish skipped,
     and the release made public over bytes nobody attested. So install the served version and compare the installed
     folder with the attested file, FILE BY FILE — every file of the `.vsix`, nothing extra (a changed `package.json`
     pointing `main` at an added file is exactly what a bundle-only check would miss), apart from what VS Code itself
     adds (`.vsixmanifest`, `__metadata` in package.json). The same script `POST_DEPLOY.md` item 6 runs after the release;
     inside WSL, from the repository root, with the downloaded `.vsix` in `<dir>`:
     `code --install-extension remsoftdev.ai-os-care@<x.y.z> --force`, then
     `bash .github/scripts/compare-installed-extension.sh <dir>/ai-os-care-<x.y.z>.vsix "$(wslpath "$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')")/.vscode/extensions/remsoftdev.ai-os-care-<x.y.z>"`.
     Any difference (exit 1, each one named) → do NOT approve: reject the deployment (the release stays a draft), and fix forward with the next patch
     version — a Marketplace version cannot be uploaded twice.
  5. **Then approve `publish-marketplace`** (or, if it already ran and failed, *Re-run FAILED jobs* — never all jobs).
     Its first step asks the Marketplace whether `<x.y.z>` is served; it is, so the publish step is SKIPPED (no token is
     read), the wait passes at once, and **`github-public`** compares the draft with the attested build and makes the
     release public. (It compares the DRAFT, not the Marketplace — step 4 is the only byte check of what the Marketplace
     serves before the release is public; `POST_DEPLOY.md` item 6 repeats it afterwards.)

  Order matters. Approved BEFORE the upload, the job finds the version not served and fails at *VSCE_PAT is not set*;
  that is harmless — upload, wait, then *Re-run FAILED jobs*. **Rejecting** the deployment fails the run and leaves the
  release a draft; left **unapproved**, GitHub ends the waiting deployment after 30 days, also as a failure. Either way
  the release stays an invisible draft until `github-public` runs, and `POST_DEPLOY.md` item 6 (which lists only
  published releases) fails until then. The tag exists regardless, so a later extension release's guard is not
  blocked by it.
- **Later, to automate the publish:**
  - **OIDC, no stored secret.** Register an Entra application (or a user-assigned managed identity), add a
  federated credential for subject `repo:oleksandrdubyna88/wsl_care:environment:marketplace` (issuer
  `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`), and add that identity as a member
  of the publisher (*Manage → Members*, role Contributor). Then, in ONE pull request: in `release-extension.yml`'s
  `publish-marketplace` job add `id-token: write`, an `azure/login` step (SHA-pinned; `client-id`, `tenant-id`,
  `allow-no-subscriptions: true`) before the publish, and `--azure-credential` on `vsce publish`; widen
  `ReleaseWorkflowTests` / `ReleaseExtensionWorkflowTests`' signing-scope assertions to that job; set the two ids as
  Environment variables (not secrets). Record `VSCE_PAT expires: none — OIDC` in `POST_DEPLOY.md`.
  - **Or, until 2026-12-01: a PAT.** Create it at `https://dev.azure.com/<org>/_usersSettings/tokens` (Organization: All
  accessible organizations; Scopes: Custom defined → Marketplace → **Manage**; expiry at most 2026-12-01), store it ONLY
  in the Environment (below), and record `VSCE_PAT expires: <YYYY-MM-DD>` in `POST_DEPLOY.md` (item 12 fails 30 days
  before it).

**The Environment** — a required reviewer, and only `extension-v*` tags may deploy to it:

```bash
OWNER_ID="$(gh api user --jq .id)"
printf '{"reviewers":[{"type":"User","id":%s}],"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}' "$OWNER_ID" > /tmp/marketplace-env.json
gh api --method PUT "repos/$REPO/environments/marketplace" --input /tmp/marketplace-env.json
gh api --method POST "repos/$REPO/environments/marketplace/deployment-branch-policies" -f name='extension-v*' -f type=tag
gh secret set VSCE_PAT -R "$REPO" --env marketplace        # prompts for the value; skip with OIDC or the manual upload
```

**Check:** `gh api "repos/$REPO/environments/marketplace" --jq '.protection_rules[].type'` lists `required_reviewers` and
`branch_policy`; `gh api "repos/$REPO/environments/marketplace/deployment-branch-policies" --jq '.branch_policies[] |
[.name,.type]'` is `["extension-v*","tag"]`; `gh secret list -R "$REPO" --env marketplace` names `VSCE_PAT` (and nothing, with the manual upload). Whether the
protection ACTS is observed on the first release: `publish-marketplace` stops at *Waiting for review* until you approve.

## 10. The extension tag ruleset — only the App creates an `extension-v*` tag, nobody moves or deletes one (E5 live gate, step 3)

`.github/rulesets/tags-extension.json` is `tags-daemon.json` with the pattern `refs/tags/extension-v*` (the daemon's
file is not edited; `ReleaseExtensionWorkflowTests` holds the two equal but for the name and the pattern). Applied
exactly as step 3, WITH its own probe:

```bash
node -e 'const r=JSON.parse(require("fs").readFileSync(".github/rulesets/tags-extension.json","utf8")); r.conditions.ref_name.include.push("refs/tags/zz-ruleset-probe-ext-*"); process.stdout.write(JSON.stringify(r))' > /tmp/tags-ext-probe.json
EXT_ID="$(gh api --method POST "repos/$REPO/rulesets" --input /tmp/tags-ext-probe.json --jq .id)"; echo "$EXT_ID"
gh api --method POST "repos/$REPO/git/refs" -f ref=refs/tags/zz-ruleset-probe-ext-1 -f sha="$MAIN_SHA"     # MUST be refused (HTTP 422)
gh api --method PUT "repos/$REPO/rulesets/$EXT_ID" --input .github/rulesets/tags-extension.json            # only after the refusal
gh api "repos/$REPO/git/matching-refs/tags/zz-ruleset-probe-ext" --jq length                              # 0
gh api "repos/$REPO/rulesets/$EXT_ID" --jq '.conditions.ref_name.include'                                 # ["refs/tags/extension-v*"]
```

If the probe tag is **created**, the ruleset does not act: delete the probe tag and the ruleset (as in step 3) and stop.

## 11. The `main` ruleset after the extension's checks reported (only if it changed)

Step 4's `branch-main.json` already requires `extension · typecheck · lint · test (windows-latest)` and
`(ubuntu-24.04)`. Re-apply it (step 4 (d)'s PUT, with its probe if the ruleset is new) only when a pull request has
REPORTED every context it names — a required check that never reports blocks every merge. E5.S3 renamed no job, so the
file is unchanged by it.

## Cutting `daemon-v0.1.0` — the E4 live gate

E4 is done when its pull request is merged (plan §16, §15f #4); this section is the **E4 live gate** that follows it,
the owner's alone. After steps 1–4 (6–8 optional; each ruleset applied only after its probe was refused), with the epic's
pull request merged to `main` and CI green there:

1. **Before tagging**, the release checklist: `POST_DEPLOY.md` item 4 (the live contract inside WSL, 0 failed, 0
   skipped).
2. `gh workflow run release-please.yml -R "$REPO"`. Expect ONE pull request, *chore(main): release daemon 0.1.0*,
   changing exactly `src_daemon/version.txt` (0.0.0 → **0.1.0**), `.release-please-manifest.json` and
   `src_daemon/CHANGELOG.md`. **Read it.** Any other version means the bootstrap did not hold
   (`release-please-config.json`, `$bootstrap`) — close it and fix the configuration; never merge a wrong first version.
3. Squash-merge it once its checks are green.
4. `gh workflow run release-please.yml -R "$REPO"` **again** — this run, not the merge, cuts the tag `daemon-v0.1.0`
   and a DRAFT release, and the tag starts `release.yml`: `gh run list -R "$REPO" --workflow release.yml --limit 3`.
5. `release.yml`: the guard → three build legs (tests, AOT publish, the smoke, the archive, its attestation) → publish
   (completeness checked before upload and again from the draft, then the draft goes public).
6. `POST_DEPLOY.md` items 8 and 10 against the published release; then install it here
   (`curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/main/install.sh | sudo sh` — gh 2.56.0 or
   newer from GitHub's apt repository, no gh login needed).
7. `POST_DEPLOY.md` items 1–2, 4–5 and 7–11 against the installation (inside WSL: `node .agents/conventions/tools/post-deploy-check.mjs
   --target 0.1.0`, plus the manual items; 3, 6 and 12 are the extension's, at the E5 live gate), and its `Last verified:` line
   stamped `Last verified: <YYYY-MM-DD> · <target> · daemon 0.1.0` — `release-extension.yml`'s guard reads that shape, from
   `main`'s tip at the time its job runs (never from the extension tag's own tree, which predates the stamp). That stamp is
   what the E5 live gate (the Marketplace publish) and every story of E6 wait for; E5.S0–S3 need not wait. Phase 0 does not gate this
   install any more — it gates the review of the dryRun week (plan §16).

If `release.yml` fails, nothing is public: the draft stays a draft. Re-run the failed jobs for a transient failure;
otherwise fix the cause on `main` and let release-please cut the next patch. **Never move or delete a release tag** —
the tag ruleset refuses it anyway.

## Cutting `extension-v0.1.0` — the E5 live gate

E5 is done when its pull request is merged (plan §16, §15g M4); this section is the **E5 live gate**, the owner's alone,
AFTER the E4 live gate's stamp (`release-extension.yml`'s guard refuses before it anyway). Each step observed, not
assumed:

1. Steps 9–11 above: the publisher (its id merged into `package.json`), the credential and the `marketplace` Environment,
   the tag ruleset with its refused probe.
2. The minimum daemon (`daemon-v0.1.0`, carrying E5.S0) published and `POST_DEPLOY.md` stamped `… · daemon 0.1.0` by the
   E4 live gate; then `contracts/golden/daemon-0.1.0/` frozen from it (plan §15g m7) and committed.
3. `POST_DEPLOY.md` item 3's preview timing at this machine's real container count — BEFORE the listing (§15h #1).
4. `gh workflow run release-please.yml -R "$REPO"`. Expect a pull request *chore(main): release extension 0.1.0*
   changing exactly `src_vs_code/package.json` and `package-lock.json` (0.0.0 → **0.1.0**), `.release-please-manifest.json`
   and `src_vs_code/CHANGELOG.md`. **Read it**; any other version means the bootstrap did not hold — close it. (A
   `Release-As:` footer applies to EVERY package the commit touches — never use one here.) Squash-merge it green.
5. `gh workflow run release-please.yml -R "$REPO"` **again** — this run cuts `extension-v0.1.0` and a DRAFT release; the
   tag starts `release-extension.yml`: record the run ids BEFORE (`gh run list -R "$REPO" --workflow release-extension.yml
   --limit 5 --json databaseId`) and accept only a NEW run on event `push` for `refs/tags/extension-v0.1.0`. Push no other
   tag in the same minute (more than three tags in one push trigger nothing).
6. `release-extension.yml`, observed job by job: guard (tag, package.json, publisher, main, the minimum daemon from
   `src_vs_code/min-daemon.json` published, and stamped in `POST_DEPLOY.md` on `main`'s tip — a tag cut before the stamp merged
   passes on "Re-run failed jobs" once it has) → build (tests, the extension-host tier, `vsce package` once, the
   leak checks with `--release --min-daemon`; `contents: read` only) → **attest** (the only signing job: the build's
   `.vsix` checked against its `.sha256` and attested, no npm) → **github-draft** (the `.vsix` + `.sha256` on the draft,
   read back and compared — the rollback source exists before anything is public) → **publish-marketplace** (approve it:
   the Environment waits for you; it skips if the Marketplace already serves 0.1.0, otherwise publishes the attested file
   and waits until the Marketplace serves it — with the manual upload of step 9, upload the draft's `.vsix` by hand,
   wait until it is served and check its bundle against the attested one BEFORE approving, so the job skips) → **github-public** (the draft compared with the attested build once more,
   then public).
7. `POST_DEPLOY.md` items 3, 6 and 12 against the Marketplace build installed in VS Code
   (`code --install-extension remsoftdev.ai-os-care`), then the stamp extended to `… · daemon 0.1.0 · extension 0.1.0`.

**Every job is re-runnable with "Re-run FAILED jobs"** — it replays the same tag event and reuses the successful build's
artifact. **Never "Re-run all jobs"**: it rebuilds, and a rebuilt `.vsix` is not byte-identical while the Marketplace may
already serve the first. So no asset on a release is ever replaced, draft or public: github-draft uploads only what the
draft lacks and COMPARES the rest with this run's build, refusing on a difference with exactly that advice;
publish-marketplace skips a version the Marketplace already serves and waits again; github-public compares the draft with
the attested build before making it public and is a no-op on a public release. A failure before github-public
leaves an invisible draft (and, at worst, a Marketplace version whose `.vsix` is already on that draft). Fix forward: the
next patch through release-please. **Never move or delete an `extension-v*` tag** — the ruleset refuses it.

**Rollback** never builds: install a previous version's `.vsix` from its GitHub release, its attestation verified first —
`gh release download extension-v<previous> -R oleksandrdubyna88/wsl_care --pattern '*.vsix'`, then
`gh attestation verify ai-os-care-<previous>.vsix --repo oleksandrdubyna88/wsl_care --cert-identity "https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release-extension.yml@refs/tags/extension-v<previous>" --deny-self-hosted-runners` (it must say the bytes were built by `release-extension.yml` AT that tag on a GitHub-hosted runner — the exact identity, as POST_DEPLOY item 6 pins it; never `--signer-workflow`, which gh matches as a prefix, so a run from any branch would pass), then
`code --install-extension ai-os-care-<previous>.vsix` — or ship the next patch. Every release keeps its `.vsix` (a release
asset does not expire, unlike a workflow artifact).
