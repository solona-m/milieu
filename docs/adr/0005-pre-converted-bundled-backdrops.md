# ADR 0005: Bundled scenes ship pre-converted, published by a guarded local script

## Status

Accepted. Partly supersedes ADR 0004 (what is published and how; the pinned, on-demand download pattern stays).
The workflow facts (Actions enabled, but no workflow ever registered) and the plugin release workflows are in ADR 0007;
`MilieuAssets.MirrorBase` is now set, so downloads go mirror first.

## Context

Converting an 8K JPEG to BC7 with the managed encoder takes minutes on the player's machine (about 2.5 minutes per
scene on the build PC). Seen in game: picking a bundled scene felt broken while it converted. The textures are the
same for everyone, so converting them once, ahead of time, is strictly better for players.

A BC7 8192x4096 texture with 11 mips is 44,739,312 bytes, so the 8 bundled scenes are 341.3 MiB, versus about 55 MB
of JPEGs. The tag-triggered `upload-backdrops.yml` workflow never ran for `backdrops-v1`: GitHub registered no
workflow for the repository. The cause is unknown.

## Decision

- `tools/PrepareBackdrops atex` converts the committed JPEGs to `.atex` with `TextureConversion`, the same code the
  plugin uses for user images, so the published bytes are exactly what the plugin would produce. Same-machine runs are
  byte-identical (`TextureConversionTests.Deterministic_ConvertToAtexTwice`; the opt-in Slow
  `BundledAtexTests.RegeneratedFromCommittedJpegs_MatchesPins`).
- The `.atex` files are **not committed** (git-ignored `artifacts/backdrops`). Release assets are the store of record,
  pinned by size and SHA-256 in `Milieu/Assets/BackdropPins.cs` with the tag (`backdrops-v2`).
  - Rejected: Git LFS (341 MiB per version against the free LFS quota, and LFS bandwidth on every checkout).
  - Rejected: regenerating in CI (byte identity across CPUs is unproven: float/SIMD paths in the encoder).
- `tools/publish-backdrops.ps1 -Tag <tag>` replaces the workflow. It refuses unless the tag is well formed, points at
  the checked-out HEAD, `BackdropPins.cs` has no local changes, the tag is on origin, no release exists yet, and
  `PrepareBackdrops verify` confirms exactly the pinned files at their size, header and SHA-256. It uploads only
  `*.atex`, with `--verify-tag --latest=false`. `PublishBackdropsScriptTests` pins these guards.
- The plugin downloads a scene **only when its tile is clicked** (or "Download all"), one at a time, into
  `<config>/DefaultBackdrops/<stem>.atex`, and uses it **as-is**: no decode, no encode. Before registering it with
  Penumbra it checks the pinned size, the exact header and (once per session per file version) the SHA-256; a damaged
  file is deleted and downloaded again. Texture size and HDR settings don't apply to bundled scenes.
- The grid's bundled previews are 256x128 JPEGs (`PrepareBackdrops thumbs`) embedded in the plugin, so they show
  before anything is downloaded.
- The mirror route `dl.solona.info/milieu/` exists (the Proteus repo's worker); downloads go through it once
  `MilieuAssets.MirrorBase` is set (the release feature), with GitHub as the fallback. Until then: GitHub only.

## Consequences

Easier: a bundled scene shows within a couple of seconds of downloading; players never wait for BC7.
Harder: a new or changed scene means regenerating the textures on one machine, pasting the new pins, tagging a new
`backdrops-vN` at that commit and running the publish script from that checkout. A full set is 341 MiB per version
on GitHub (per-scene downloads keep what each player fetches small).
