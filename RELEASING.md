# Releasing anchor

Push a `v*` tag and `.github/workflows/ci.yml` does the rest:

1. Tests, then a build for each platform. macOS builds run on macOS and the Windows build on
   Windows, so they can be signed there.
2. The macOS binaries are signed with a Developer ID and notarized, and the Windows binary is
   signed with Azure Artifact Signing.
3. The release gets `SHA256SUMS` and a build provenance attestation. The install scripts refuse
   an archive that doesn't match `SHA256SUMS`, so every release needs it.
4. The Homebrew formula and the Scoop manifest are rendered from `SHA256SUMS`
   (`packaging/render.sh`) and pushed to their repos, and a winget PR is opened.

Steps 2 and 4 are skipped, with a warning on the run, until their secrets exist. A release
without them is still complete; it's just unsigned and absent from package managers.

## One-time setup

### macOS signing and notarization

Needs an Apple Developer Program membership.

| Secret | Value |
|---|---|
| `APPLE_CERTIFICATE_P12` | A "Developer ID Application" certificate with its private key, exported as .p12, base64-encoded |
| `APPLE_CERTIFICATE_PASSWORD` | The .p12's password |
| `APPLE_SIGNING_IDENTITY` | e.g. `Developer ID Application: Your Name (TEAMID)` |
| `APPLE_ID` | The Apple ID that submits for notarization |
| `APPLE_TEAM_ID` | The 10-character team id |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID |

The binary is signed with the hardened runtime and `packaging/anchor.entitlements`, which allows
the .NET JIT and loading the native libraries bundled in the binary (they're extracted on first
run and aren't signed by us). A bare binary can't be stapled, so Gatekeeper checks the notarization online.

### Windows signing

Needs an [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) account
with a public trust certificate profile, and an app registration with the "Artifact Signing
Certificate Profile Signer" role on it.

| Secret | Value |
|---|---|
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | The app registration |
| `AZURE_SIGNING_ENDPOINT` | The account's region endpoint, e.g. `https://eus.codesigning.azure.net/` |
| `AZURE_SIGNING_ACCOUNT` | The signing account name |
| `AZURE_CERTIFICATE_PROFILE` | The certificate profile name |

### Homebrew and Scoop

The formula lives in [`fuseraft/homebrew-tap`](https://github.com/fuseraft/homebrew-tap)
(`Formula/anchor.rb`) and the manifest in
[`fuseraft/scoop-bucket`](https://github.com/fuseraft/scoop-bucket) (`bucket/anchor.json`).
The release job pushes both using `PACKAGING_TOKEN`, a fine-grained token with Contents
read and write on those two repos.

### winget

winget only accepts updates for a package that already exists, so submit the first version by
hand with [`wingetcreate`](https://github.com/microsoft/winget-create) as `Fuseraft.Anchor`
(a portable package, with `anchor.exe` as the nested installer in the zip). Then add
`WINGET_TOKEN`, a classic token with `public_repo` scope whose owner has a fork of
`microsoft/winget-pkgs`; later releases open their PRs on their own.
