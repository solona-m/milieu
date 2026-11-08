# ADR 0004: Bundled backdrops are pinned release assets, downloaded on demand

## Status

Accepted; partly superseded by ADR 0005 (the scenes now ship pre-converted as .atex, published by a guarded local script instead of the workflow, downloaded per scene).

## Context

Milieu ships a starter set of panoramas (about 50 MB). Putting them in the plugin zip would make every user
re-download them on every update. Proteus solved the same problem for its effects and matcaps.

## Decision

Follow Proteus's matcap pattern:

- The processed images are committed in `Milieu/DefaultBackdrops/` and excluded from the plugin output.
- `.github/workflows/upload-backdrops.yml` publishes them as loose release assets when a `backdrops-v<N>` tag is
  pushed. It refuses to overwrite an existing release, refuses while `CREDITS.md` still has TODO placeholders, checks
  every file is a JPEG, and never marks the release "Latest". Actions are SHA-pinned.
- `DefaultBackdropsDownloadService` pins the tag plus (name, bytes, SHA-256) per file and downloads lazily into
  `<config>/DefaultBackdrops` the first time the Backdrops list is drawn, never at plugin load. A cancelled download
  keeps its `.tmp` for HTTP range resume. Logging is strings only and sanitised.
- Sources: `MilieuAssets.BaseUrls(tag)` = an optional mirror (empty for now) followed by
  `https://github.com/solona-m/milieu/releases/download/<tag>/`. The shared `dl.solona.info` worker lives in the
  Proteus repo; if Milieu is added there it gets its own path prefix (e.g. `/milieu/`).
- The images are pre-processed offline by `tools/PrepareBackdrops`, which links Milieu's own imaging code: fit to
  8192x4096 (lodge copied byte for byte). Sunset is a non-wrapping 5.5:1 strip with its horizon near the bottom, so it
  is mirror-tiled to wrap, its horizon placed on the equator and its sky and ground faded to an even colour. The tool
  refuses to write anything the seam detector flags.
- The JPEGs are plain git files, not LFS: `.gitattributes` has an LFS rule only for `*.tif`, and the pinned table, the
  workflow checksum step and the tests all need byte-stable files in the checkout.
- `BundledBackdropsTests` and the pinned-folder test read the committed images: an acknowledged exception to the
  synthetic-fixtures rule, because these are product assets, not fixtures.
- Licence: the owner confirmed the images may be redistributed and need no attribution (`CREDITS.md`).

## Consequences

Easier: a small plugin zip; images verified by size and hash; partial downloads resume.
Harder: changing an image means a new `backdrops-vN` tag, a new pinned table, and pushing the tag before the plugin
release that pins it. The repo carries about 50 MB of JPEGs in history.
