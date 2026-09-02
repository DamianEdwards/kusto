# Release, signing, provenance, and self-update

This document describes the release system used by `DamianEdwards/kusto-cli`.
It is intentionally local to this repository: the workflows originated in
`DamianEdwards/cli-repo-template`, but the trust rules, payload contents,
recovery procedures, and operational settings below are specific to Kusto CLI.

## Design goals

The release system is built around these invariants:

- Pull requests validate the same NativeAOT and packaged runtime paths used by
  releases.
- CI builds official release candidates once. Promotion does not rebuild them.
- The CI run, source commit, release tag, metadata, checksums, signatures, and
  attestations must all agree.
- Official Windows payloads fail closed unless every executable file is
  Authenticode signed and verified.
- Official Linux and macOS archives are verified against a tag-bound Sigstore
  attestation before extraction or execution.
- Install and self-update operations replace only manifest-managed files,
  preserve unrelated files, and retain a rollback copy until the new CLI passes
  its packaged chart diagnostic.
- Mutable workflow state is isolated on dedicated branches; official tags and
  published releases are immutable.

## Release channels and versions

The repository publishes three channels:

- **Dev**: public prereleases produced directly by `ci.yml`, for example
  `0.3.2-pre.1.dev.1`. Dev archives are unsigned and unattested by design, but
  installers and self-update always verify their checksums and metadata.
- **Pre-release**: official signed and attested tags such as
  `0.3.2-pre.1.rel` or `0.3.2-rc.1.rel`.
- **Stable**: official signed and attested SemVer releases such as `0.3.1`.
  Stable releases become the repository's latest release.

Mutable state is stored as `version-state.json` on the workflow-managed
`release-state` branch:

```json
{
  "base": "0.3.2",
  "phase": "pre",
  "phaseNumber": 1,
  "devNumber": 0,
  "pending": "none"
}
```

`scripts/version.cs` converts that state into the next Dev and promotable
versions. Releasing a `pre` or `rc` build increments `phaseNumber`. Releasing an
RTM build advances the base version and selects the next phase from the
`default_post_release_phase` dispatch input or the
`DEFAULT_POST_RELEASE_PHASE` repository variable.

This repository currently sets `DEFAULT_POST_RELEASE_PHASE=rtm`. A maintainer
can override it for a particular promotion by selecting `pre`, `rc`, or `rtm`
when dispatching `publish-release.yml`.

## Workflow architecture

### Pull request validation

`.github/workflows/pr.yml` runs:

- change-aware PowerShell, shell, and C# file-app validation
- the solution build and Linux x64 NativeAOT validation
- tests on Windows, Linux, and macOS
- NativeAOT validation on Windows x64, Linux ARM64, and macOS ARM64

The default-branch ruleset requires these checks:

- `validate-scripts`
- `build`
- `test (ubuntu-latest)`
- `test (windows-latest)`
- `test (macos-latest)`
- `publish-aot (windows-latest, win-x64)`
- `publish-aot (ubuntu-22.04-arm, linux-arm64)`
- `publish-aot (macos-latest, osx-arm64)`

### CI and promotable artifacts

`.github/workflows/ci.yml` runs on pushes to `main` and can also be dispatched
manually after a version-phase change.

It:

1. Reads `release-state`, or initializes it from existing releases if the
   branch does not yet exist.
2. Validates scripts, builds, and tests.
3. Publishes Dev and promotable archives for all six RIDs.
4. Runs packaged `_diag chart-self-test` checks on executable host/architecture
   combinations.
5. Merges each set into a release bundle.
6. Publishes a versioned public Dev release.
7. Advances `release-state` only after both bundles succeed.

CI, `bump-version.yml`, and `release.yml` use the same `release-state`
concurrency group with `queue: max`. This serializes every state writer without
discarding pending runs.

### Promotion dispatcher

`.github/workflows/publish-release.yml` is the maintainer entry point for an
official release. It has its own `publish-release` concurrency group because it
dispatches `release.yml`; putting the parent and child runs in the same group
can strand the child run behind its completed parent.

Before the production approval gate, it verifies that the selected CI run:

- used `.github/workflows/ci.yml`
- completed successfully
- ran for `main` in this repository
- was triggered by a `push` or `workflow_dispatch`
- has a valid source SHA
- produced the expected version and all six RIDs
- produced metadata whose `sourceCommit` matches that SHA
- produced archives matching `checksums.txt`

After approval, it creates an annotated `v{version}` tag at the CI SHA and
dispatches `release.yml` on that exact tag.

### Signing and release publication

`.github/workflows/release.yml` validates the tag and CI run again, downloads
the promotable bundle, and performs the official publication:

1. The `production` environment gates the Windows signing job.
2. Azure Artifact Signing signs every Windows executable payload:
   - `kusto.exe`
   - `libSkiaSharp.dll`
   - `libHarfBuzzSharp.dll`
   - `libsodium.dll`
3. The workflow verifies every signature and signer issuer chain.
4. Signed Windows archives replace their unsigned CI equivalents.
5. Checksums and release metadata are regenerated.
6. `actions/attest` creates tag-bound attestations for all six final archives.
7. The action's uncompressed JSONL bundle is published as
   `attestations.jsonl` for portable offline verification.
8. GitHub generates release notes from `.github/release.yml`.
9. The workflow advances `release-state`.

Signing is mandatory. Missing Azure settings fail the workflow rather than
publishing an unsigned official Windows release.

If release publication succeeded but state advancement did not, rerunning
`release.yml` on the same tag verifies the existing release, checksums, source
commit, six-RID inventory, and attestations before completing the state update.
It never deletes and recreates an existing official release.

## Release artifact layout

Each Dev or official release contains:

- `kusto-win-x64.zip`
- `kusto-win-arm64.zip`
- `kusto-linux-x64.tar.gz`
- `kusto-linux-arm64.tar.gz`
- `kusto-osx-x64.tar.gz`
- `kusto-osx-arm64.tar.gz`
- `checksums.txt`
- `release-metadata.json`

Official releases also contain `attestations.jsonl`.

Each archive contains:

- `kusto.exe` on Windows or `kusto` on Unix
- the platform's SkiaSharp, HarfBuzzSharp, and libsodium native sidecars
- `LICENSE`
- `THIRD-PARTY-NOTICES.md`
- `payload-manifest.json`

`payload-manifest.json` is a sorted list of every install-managed file except
the manifest itself. Packaging, installers, and self-update reject missing,
undeclared, or unsafe paths. The manifest is intentionally forward-compatible:
later releases may add or remove managed files without changing a hard-coded
updater allowlist. Official Windows releases sign and verify every declared
`.exe` and `.dll`.

## Trust model

### Windows

The PowerShell installer is the source of truth for:

- the expected signer subject
- allowed immediate issuer SHA512 thumbprints
- allowed parent intermediate issuer SHA512 thumbprints
- executable payload files that must be signed

`scripts/Generate-VerifyProvenance.ps1` extracts the shared validation region
from the installer and embeds it into Windows builds. This prevents the
installer and self-update trust policy from drifting.

Verification requires a valid Authenticode signature, timestamp, certificate
chain, signer subject, and allowed issuer. Root certificates are never accepted
as the issuer fallback.

### Linux and macOS

Official archives are attested by `release.yml` on the exact release tag.

- The CLI reads `attestations.jsonl` and verifies it locally with the `Sigstore`
  package.
- `install.sh` verifies the same bundle with Cosign 2.4.0 or newer. It uses the
  explicit new-bundle flag required by Cosign 2.x and the default bundle mode
  used by Cosign 3.x, then independently requires the signed DSSE envelope to
  contain SLSA v1 provenance for compatibility with Cosign versions that do not
  enforce the requested predicate type themselves.
- Both policies require the GitHub Actions OIDC issuer and this exact identity:

```text
https://github.com/DamianEdwards/kusto-cli/.github/workflows/release.yml@refs/tags/<tag>
```

Publishing the portable bundle avoids depending on GitHub's compressed
`bundle_url` response format at install time.

Dev builds intentionally skip signature/attestation verification because they
are produced before official promotion. Checksum and release metadata
verification still run.

## Self-update

`kusto update` selects the highest SemVer candidate containing the current
platform archive:

- Dev builds can advance to any newer channel.
- Official prereleases can advance to newer official prereleases or stable.
- Stable builds select stable updates unless `--pre-release` or
  `include_prerelease_updates=true` opts in to official prereleases.
- `--stable-only` overrides the configured prerelease preference.

Useful commands:

```powershell
kusto update --check
kusto update --dry-run
kusto update
kusto update --pre-release
kusto update --stable-only
kusto config --set include_prerelease_updates=true
```

Checksums and metadata are always required. `--skip-provenance-checks` exists
for an explicitly trusted local test source; it does not disable checksum
verification. `KUSTO_DISABLE_SELF_UPDATES=1` disables update checks.

Versions through `0.3.1` use the earlier fixed payload allowlist. If a later
release adds runtime files, users of those versions must run the current
installer once; subsequent updates use the manifest-driven contract.

The updater validates the downloaded version and packaged chart stack before
staging. It performs a manifest-managed file transaction:

- Existing managed files are copied to a backup before mutation.
- Unrelated install-directory files, including generated completion scripts,
  are preserved.
- Unix replaces managed files in-process.
- Windows starts a detached helper, waits for the active process to exit, then
  replaces managed files while holding the update lock.
- The backup remains until the new CLI passes `_diag chart-self-test`.
- Any failure restores the previous payload.

## Install script publication

Install scripts are published independently from CLI releases.

`.github/workflows/install-scripts.yml`:

1. Runs from `main` behind a `production` approval.
2. Requires all Azure signing settings.
3. Signs and verifies `install.ps1`.
4. Stages `install.ps1`, `install.sh`, snapshot-local `.gitattributes`, and the
   attestation workflow.
5. Generates `install-scripts.json` and `checksums.txt`.
6. Fast-forwards the workflow-managed `install-scripts` branch.
7. Creates an annotated `install-scripts-vYYYY.MM.DD.RUN.ATTEMPT` tag.
8. Dispatches `attest-install-scripts.yml` on that tag.

`.github/workflows/attest-install-scripts.yml` has a second `production`
approval. It verifies:

- the annotated tag belongs to the protected `install-scripts` history
- the recorded source commit belongs to `main`
- script checksums
- the PowerShell Authenticode signature and signer

It then attests both scripts and publishes an immutable, non-latest snapshot
release containing the scripts, manifest, checksums, and
`attestations.jsonl`.

Latest installer URLs are:

- `https://kusto.damianedwards.dev/install.ps1`
- `https://kusto.damianedwards.dev/install.sh`
- `https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.ps1`
- `https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh`

The Cloudflare Worker special-cases `kusto-cli` to serve the
`install-scripts` branch. Other repositories can continue using their legacy
standing `install-scripts` release tag.

## Repository configuration

### Production environment

The `production` environment allows:

- `main`
- `v*` tags
- `install-scripts-v*` tags

It has a required reviewer and currently permits administrator bypass. Release
tag creation, Windows signing, installer signing, and installer snapshot
attestation all use this environment as appropriate.

### Required environment secrets

All six values are mandatory for official releases and installer publication:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_SIGNING_ENDPOINT`
- `AZURE_SIGNING_ACCOUNT`
- `AZURE_CERT_PROFILE`

Azure holds the signing key material. GitHub stores only the identifiers used
for OIDC login and Azure Artifact Signing.

### Rulesets

- The default branch requires pull requests, linear history, one approval, and
  the status checks listed above.
- The `install-scripts` branch ruleset blocks deletion and non-fast-forward
  updates.
- The `install-scripts-v*` tag ruleset blocks deletion and tag rewrites.

The minimal installer rules allow the default `GITHUB_TOKEN` to create and
fast-forward the branch and create snapshot tags. If this repository later adds
non-admin writers, use a dedicated GitHub App or write deploy key for
publication, put that actor on the bypass list, and then restrict branch/tag
creation and updates to that actor.

### Repository variables

- `DEFAULT_POST_RELEASE_PHASE=rtm`

## Maintainer procedures

### Publish normal development output

Merge a PR to `main`, or manually dispatch `ci.yml`. Confirm all six Dev and
promotable jobs, both bundle jobs, and the release-state update succeed.

### Change the release phase

Dispatch `bump-version.yml` with:

- `version_bump`: `auto`, `patch`, `minor`, or `major`
- `phase`: `pre`, `rc`, or `rtm`

Then merge a change or dispatch `ci.yml` to produce artifacts from that state.

### Publish an official release

1. Identify the successful `ci.yml` run to promote.
2. Dispatch `publish-release.yml` on `main` with that run ID.
3. Approve the production tag-publication deployment.
4. Confirm the annotated tag points to the CI source SHA.
5. Approve the production signing deployment in `release.yml`.
6. Confirm the release, attestations, and release-state advancement.

If tag dispatch fails after the tag is pushed, dispatch `release.yml` manually
on the existing tag with the original CI run ID and phase. Do not recreate the
tag.

Promote a CI run whose source commit contains the current workflow files.
GitHub correctly rejects a workflow token attempting to create a tag at an
older commit when that operation would introduce different workflow content.

### Publish installers

1. Dispatch `install-scripts.yml` on `main`.
2. Approve signing and branch/tag publication.
3. Approve `attest-install-scripts.yml` for the generated snapshot tag.
4. Verify the vanity and raw branch URLs return the same script bytes.

### Clean old snapshots

`releases-cleanup.yml` runs weekly and can be dispatched manually. It keeps the
latest five Dev releases and latest five installer snapshot releases by
default. Dev cleanup removes generated tags; installer cleanup preserves the
protected snapshot tags.

## Verification commands

Verify an official archive attestation:

```powershell
$tag = "v0.3.1"
$identity = "https://github.com/DamianEdwards/kusto-cli/.github/workflows/release.yml@refs/tags/$tag"
gh release download $tag --repo DamianEdwards/kusto-cli
gh attestation verify .\kusto-win-x64.zip `
  --repo DamianEdwards/kusto-cli `
  --cert-identity $identity
```

Verify Windows payload signatures:

```powershell
Expand-Archive .\kusto-win-x64.zip .\kusto-win-x64
.\scripts\Verify-WindowsBinaryIssuer.ps1 `
  -PayloadDirectory .\kusto-win-x64 `
  -InstallerScriptPath .\scripts\install\install-kusto-cli.ps1
```

Exercise negative provenance cases:

```powershell
.\scripts\Test-InstallerProvenance.ps1 `
  -Scenario UnsignedBinary `
  -BinaryPath .\artifacts\unsigned\kusto.exe

.\scripts\Test-InstallerProvenance.ps1 `
  -Scenario ChecksumMismatch `
  -ArchivePath .\kusto-win-x64.zip `
  -ChecksumsPath .\checksums.txt
```

Verify stable installation and update:

```powershell
irm https://kusto.damianedwards.dev/install.ps1 | iex
kusto --version
kusto update --check
kusto update
```

## Workflow helpers

Release support is implemented by:

- `scripts/Publish-NativeAsset.ps1`
- `scripts/merge-release-bundle.cs`
- `scripts/update-release-bundle-metadata.cs`
- `scripts/expand-windows-release-assets.cs`
- `scripts/compress-windows-release-assets.cs`
- `scripts/write-install-scripts-manifest.cs`
- `scripts/version.cs`
- `scripts/publish-branch-content.sh`
- `scripts/update-dev-release.sh`
- `scripts/Generate-VerifyProvenance.ps1`
- `scripts/Verify-WindowsBinaryIssuer.ps1`
- `scripts/Test-InstallerProvenance.ps1`
- `scripts/Verify-PowerShellSyntax.ps1`
- `scripts/verify-shell-syntax.sh`

There is no Homebrew formula, Winget/Scoop manifest, NuGet tool package,
apt/rpm package, container image, or SBOM publication in this release system.
Those would be separate distribution features.
