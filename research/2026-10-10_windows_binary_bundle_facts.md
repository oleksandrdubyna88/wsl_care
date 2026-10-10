# Can the extension carry the release-built `wsl-care.exe`? — measured 2026-10-10

> What [PLAN_bundle_windows_binary.md](../todo/PLAN_bundle_windows_binary.md) rests on. Every step was **read-only** on the
> owner's machine (Windows 11 Pro 10.0.26300, Smart App Control enforced), in a scratch folder. One product binary was started
> once, with `--version`. Nothing was installed or registered, and nothing was copied into any program or profile folder.

## 1. The attested asset

`gh release download daemon-v0.3.0 -R oleksandrdubyna88/wsl_care -p 'wsl-care-0.3.0-win-x64.zip*'`, then:

| check | result |
|---|---|
| `sha256sum -c wsl-care-0.3.0-win-x64.zip.sha256` | OK |
| `gh attestation verify … --repo oleksandrdubyna88/wsl_care --cert-identity https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v0.3.0 --deny-self-hosted-runners` | exit 0 |
| zip members | exactly one: `wsl-care-0.3.0-win-x64/wsl-care.exe` |
| exe size | 13,676,544 bytes |

The attestation's subject is the ZIP (`release.yml`'s `attest-build-provenance` over the asset), not the exe inside it.

## 2. Does Windows let it run?

| check | result |
|---|---|
| `Get-AuthenticodeSignature` | `NotSigned` |
| `Zone.Identifier` stream (Mark of the Web) | none |
| `wsl-care.exe --version` under the enforced Smart App Control | ran, exit 0: `0.3.0+609ba17aaa52398bc13f8e83115c96c6f31788ce` |

So an unsigned exe built by the daemon release is not blocked here. The memory of a blocked FRESH LOCAL build (error
`0x800711C7`) does not carry over to the release build. A blocked start must still be reported by its code if it ever happens.

## 3. Would the `.vsix` leak scan accept it?

The text scan in `src_vs_code/src/test/support/vsixCheck.ts:73-80` refuses drive paths, `/home/`, `/mnt/`, `\\wsl` and e-mail
addresses in every non-image entry. Counted as raw bytes in the exe:

| needle | count |
|---|---|
| `D:\a\` (the GitHub runner's build directory) | 1 |
| `\wsl` | 1 |
| `.pdb` | 1 |
| `/home/`, `C:\Users\`, `@users.noreply`, `runneradmin` | 0 |

So the scan would refuse the exe as it stands. What it finds is the runner's build path, not this machine's data. The plan
therefore exempts exactly `bin/wsl-care.exe` from the TEXT scan, and holds it by its attested digest instead.

## 4. How vsce names a targeted package

vsce 4.0.0 (`@vscode/vsce/out/package.js:1455-1464`): `--target` gives `${name}-${target}-${version}.vsix`. A `win32-x64` build of
this extension is therefore `ai-os-care-win32-x64-<v>.vsix`, and every place in the release chain that spells
`ai-os-care-<v>.vsix` changes with it.
