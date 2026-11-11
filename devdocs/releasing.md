# Releasing RPGModder

A release is a tag. Pushing `v<version>` builds both packages, writes `SHA256SUMS` and creates a **draft** GitHub release. A maintainer reviews the draft and publishes it. Nexus Mods uploads are a separate, manual workflow.

## Quick path

1. Set `<Version>`, `<FileVersion>` and `<AssemblyVersion>` in `RPGModder.UI/RPGModder.UI.csproj`, for example `2.1.0`, and commit.
2. Tag that commit `v2.1.0` and push the tag.
3. Wait for the **Release** workflow. It must end with a draft release holding four files.
4. Edit the draft notes (the first line says what to replace), then publish.
5. Optional: run **Nexus upload** once per Nexus file (see below).

## What the release contains

| Asset | Built by | Used by |
|---|---|---|
| `RPGModder-<version>-win-x64.zip` | `release.yml`, Windows job | Windows players, both updaters |
| `RPGModder-<version>-linux-x64.tar.gz` | `packaging/linux/package.sh` | Linux players, the current updater |
| `RPGModder-<version>-linux-x64.zip` | `packaging/linux/package.sh` | the 2.0.0 updater on Linux |
| `SHA256SUMS` | `release.yml`, release job | players checking a download, the updater |

Every package holds one top folder, `RPGModder-<version>-<runtime>/`, with `RPGModder.exe` or `RPGModder` inside it.

The names are a contract. `RPGModder.Core/Updates/ReleaseAssets.cs` defines them, and `RPGModder.Tests/Updates/ReleaseContractTests.cs` fails if the workflow, the package script or the updater stop agreeing.

## Rules the workflow enforces

| Rule | Where | What happens if it is broken |
|---|---|---|
| The tag equals `v` plus the csproj `<Version>` | `version` job | The run fails before anything is built |
| Restore uses the committed lock files | every job (`--locked-mode`, `RestoreLockedMode`) | The run fails; refresh the lock files with a normal `dotnet restore` and commit them |
| Tests pass on Windows and Linux | `windows` and `linux` jobs | No package is built |
| The published binary starts | smoke steps (`--platform-diagnostics` on Windows, `--platform-self-test` on Linux) | No package is built |
| The release holds exactly the three packages | release job, before `SHA256SUMS` | No release is created |
| Only the release job can write | `permissions: contents: write` on that job only | |
| The release is a draft | `gh release create --draft` | A human publishes |

## Updaters in the field

The updater since this change takes its package by exact name and refuses one whose SHA-256 is not the line in `SHA256SUMS`. A release without `SHA256SUMS` is not offered at all.

The 2.0.0 updater checks no hash. It takes the first `.zip` whose name contains `win` on Windows, and the first `.tar.gz` or `.zip` whose name contains `linux` on Linux. So:

- [ ] Exactly one asset name contains `win`, and it is the Windows zip.
- [ ] Every asset whose name contains `linux` is a complete package.
- [ ] `SHA256SUMS`, and any asset added later, contains neither word.

Keep these until the oldest supported version checks hashes. The contract test runs the 2.0.0 choice against every order of the assets.

## Code signing and SmartScreen

Releases are not code signed; the owner accepted the Windows SmartScreen warning. No workflow has a signing step. The draft notes, the README and `docs/verify-your-download.md` tell players how to check a download against `SHA256SUMS`.

## Nexus Mods upload

**Nexus upload** (`.github/workflows/nexus.yml`) is started by hand from the Actions tab. It downloads one package of a *published* release, checks it against `SHA256SUMS`, and uploads it with the official `Nexus-Mods/upload-action` as a new version of one Nexus file.

| Input | Example | Meaning |
|---|---|---|
| `tag` | `v2.1.0` | The published release to upload from |
| `package` | `win-x64.zip` | Which package |
| `file_id` | `1234` | The Nexus file that receives the new version. Each game page has its own |

The API key never reaches a tag, push or pull request workflow, and it is never used on a local machine.

## One-time setup (owner)

These are repository settings, so only the owner can make them.

- [ ] **Settings, Environments, New environment** named `nexus`.
- [ ] In `nexus`, add **Required reviewers** (the owner).
- [ ] In `nexus`, limit **Deployment branches and tags** to the default branch.
- [ ] In `nexus`, add the environment secret `NEXUS_API_KEY` (a Nexus Mods API key with upload rights).
- [ ] **Settings, Actions, General, Workflow permissions**: keep "Read repository contents". The release job asks for write access itself.
- [ ] Optional: a tag ruleset that limits who can push `v*` tags.

## Building packages locally

Publish into a scratch folder, never into a folder that holds a shipped build:

```bash
dotnet publish RPGModder.UI/RPGModder.UI.csproj --configuration Release --runtime linux-x64 \
  --self-contained true -p:RestoreLockedMode=true --output /tmp/rpgmodder-publish
PREBUILT_DIR=/tmp/rpgmodder-publish sh packaging/linux/package.sh 0.0.0-local
```

`package.sh` writes to `artifacts/` in the repository and needs `zip` for the second archive. The version argument is required.
