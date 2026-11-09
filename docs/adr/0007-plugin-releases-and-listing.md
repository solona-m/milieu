# ADR 0007: Plugin releases, version stamping and the plugins-repo listing

## Status

Accepted. Makes the code match ADR 0005's mirror (`MilieuAssets.MirrorBase` is now set).

## Context

Milieu had no release pipeline (ADR 0005 retired `upload-backdrops.yml`), a hardcoded `0.1.0.0` version and no listing
in `https://dl.solona.info/repo.json`. Players install from that listing, which the `solona-m/plugins` sync builds from
each plugin's newest GitHub releases. Proteus and Camera Tools already release this way.

Facts found while planning (they shape the gates below):
- GitHub Actions is enabled for `solona-m/milieu`, but no workflow was ever registered: the `backdrops-v1` tag carried
  `upload-backdrops.yml` and nothing ran. The GitHub default branch is `main`, which held only the scaffold and no
  workflows. Workflows are expected to register once they are on `main`.
- The dl.solona.info worker (Proteus repo) serves Milieu under `/milieu/`: `/milieu/<tag>/latest.zip`,
  `/milieu/backdrops-v<N>/<stem>.atex`, and, from the `test` branch, the README at `/milieu` and the icon at
  `/milieu/icon.png`. So `README.md` and `Milieu/images/icon.png` on `test` are live surfaces: a `test` push publishes
  them within minutes.

## Decision

- **Workflows, modelled on Proteus and Camera Tools**, every action pinned to a commit SHA with a version comment:
  - `test.yml`: the quick suite on push and PR to `test` and `main` (and `workflow_dispatch`); `contents: read`, no
    secrets. It is also the canary that proves GitHub runs this repo's workflows before any tag is pushed.
  - `testing-release.yml` on `testing-*`: tags are a sequence `testing-1`, `testing-2`, … and must match
    `^testing-[0-9]{1,10}$` (the mirror's shape) before anything builds. A prerelease, never "Latest". A lightweight
    testing tag builds without a changelog.
  - `release.yml` on `v*`: the tag must be annotated and equal `v<the built AssemblyVersion>`, both checked before any
    release exists; marked "Latest".
  - Asset tags (`backdrops-v<N>`) match no trigger.
- **Order in both release workflows:** checkout (`fetch-depth: 0`), staging Dalamud, the API-level check (the staging
  `Dalamud.dll` major must equal `DalamudApiLevel`), .NET, the changelog, the quick suite, the final Release build, the
  version, packaging (the SDK's own `latest.zip`), `tools/check-package.ps1`, the release, the plugins-repo dispatch.
- **Changelog:** the annotated tag's message, written to `GITHUB_ENV` with a random heredoc delimiter, so every later
  build (the test step rebuilds Milieu) packs the same text; the csproj's `Changelog` yields to the environment.
- **Package guards in one script, `tools/check-package.ps1`** (Windows PowerShell 5.1 and pwsh): at most 4 MB (else the
  largest entries are listed), root `Milieu.json` and `Milieu.dll`, `InternalName` `Milieu`, `AssemblyVersion` equal to
  the read version, and the changelog equal after normalising (CRLF/LF, trim; missing and empty are equal).
  `PackageCheckScriptTests` runs it against synthetic zips.
- **No injection, no leftover credentials:** scripts read the tag as `$env:GITHUB_REF_NAME`, never by pasting
  `${{ github.* }}` into a `run:` block; every checkout uses `persist-credentials: false` (nothing pushes, and the repo
  is public, so the tag fetch works without a token); only the two release workflows grant any write permission, and
  the dispatch runs only after the release exists.
- **No silent failures:** no `continue-on-error`, step-level `if:`, `catch`, `trap`, `|| true`, `-ErrorAction` (or
  its `-EA` alias), `$ErrorActionPreference` or `-SkipHttpErrorCheck`; every native command in a `run:` block is its last line or is
  followed by `if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }` (pwsh does not stop on a failing native command).
  `WorkflowTests` pins all of it.
- **Dispatch:** `repository_dispatch` `plugin-released` to `solona-m/plugins` with the secret `PLUGINS_REPO_TOKEN`
  (Proteus's name). A missing secret warns and exits 0, because the release exists and the nightly sync lists it; a
  rejected one fails the step. Only the owner sets the secret.
- **Version:** `ComputeMilieuVersion` in `Directory.Build.props` stamps `yyMM.<git rev-list --count HEAD>` (UTC) into
  `Version`, `AssemblyVersion` and `FileVersion`, as Proteus does; it fails clearly without git. `Plugin.BuildNumber`
  stays the in-game build identifier.
- **Mirror:** bundled scenes download from `https://dl.solona.info/milieu/<tag>/` first, then GitHub
  (`ResilientDownloader` unchanged: a 404 falls through at once, an unreachable mirror after 5 attempts with backoff).
- **Icon:** `https://dl.solona.info/milieu/icon.png` for both the csproj `IconUrl` (what an installed plugin shows) and
  the listing (the sync sets its own), so no `main` merge is needed for it.
- **Listing:** a `PLUGINS` row `Milieu|solona-m/milieu|Milieu.json|milieu/|https://dl.solona.info/milieu/icon.png` in
  the plugins `sync.yml`, plus a hand-seeded `repo.json` entry (the sync only updates existing entries), inserted as
  text, `IsTestingExclusive: true` until a stable release is verified. `tests/test_listing.py` in that repo checks the
  invariants, including that a non-testing-exclusive Milieu entry installs from a `v` build. The sync merges the whole
  manifest over the entry, so identity, the API levels, `ApplicableVersion` and `RepoUrl` are manifest-sourced too; the
  test pins the values the manifest carries today and must be updated with it (an API bump, for one). `IsHide`,
  `IsTestingExclusive` and `IconUrl` are repo.json's own. It deliberately does not pin what changes from release to
  release: `Description`, `Punchline` and the tags, and the sync's versions, `LastUpdate` and `DownloadCount`. The
  publish run checks the versions itself (both equal to the `testing-1` build's at P7). Run it with `python -B` so no
  `__pycache__` lands in that repo.
- **Publishing is a gated runbook,** run by the agent under the owner's authorisation ("No do it all for me. Notify on
  discord when done"; "Once this is done, push a main release as well"): the canary, `testing-1`, its verification, the
  listing and sync, then the full suite on the release commit, `main`, the stable tag, its verification and the stable
  listing. Each gate stops with a Discord notice on failure; nothing is forced, and no tag is deleted or reused.
- **What ships:** synced delivery (ADR 0006) is in both builds' code; until 2026-11-09 only testing and dev builds offered it,
  since then every build does (ADR 0006 amendment). It is untested in game, and the tag messages say so.

## Consequences

Easier: a release is one annotated tag; the zip, version, changelog and listing cannot drift apart without a check
failing; bundled scenes download through Cloudflare's cache.

Harder:
- A stable tag must be computed in the same UTC month as the CI build (a rollover fails the run; re-tag as it says).
- A re-created tag would serve its old zip from the mirror's immutable cache, so tags are never reused.
- Staging Dalamud breakage stops CI and releases until fixed; the API-level check names a level change.
- With the mirror down, each scene spends about 1.5–2.5 minutes on mirror retries before GitHub (accepted; rare).
- Any `test` push republishes the README page and icon; a bad README edit there is public within minutes.
- The two release workflows duplicate their setup steps (parity with Proteus); the guards live once, in the script.
- Tech debt (accepted for Proteus parity): each release workflow is one job, so the build and test steps run in a job
  whose `GITHUB_TOKEN` has `contents: write`, and the dispatch secret is in the same job's environment (only in its
  last step). Follow-up: split into a read-only build job that uploads `latest.zip` and a publish job that holds the
  write token and the secret.
- `release.yml` refuses a `v` tag whose commit is not on `main` (stable releases are cut from `main`).
