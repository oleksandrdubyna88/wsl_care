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
`family · plans · pins · adapter · build flags`, `pr · semantic title`. No bypass actor (the family's
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

## 5. Nothing for the extension yet

`VSCE_PAT` (plan §9) belongs to the extension's Marketplace leg, E5.S3; it is not needed for a daemon release.

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

## Cutting `daemon-v0.1.0`

After steps 1–4 (6–8 optional), with the epic's pull request merged to `main` and CI green there:

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
6. `POST_DEPLOY.md` items 8–10 against the published release; then install it here (E4's done-line).

If `release.yml` fails, nothing is public: the draft stays a draft. Re-run the failed jobs for a transient failure;
otherwise fix the cause on `main` and let release-please cut the next patch. **Never move or delete a release tag** —
the tag ruleset refuses it anyway.
