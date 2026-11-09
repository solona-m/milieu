# ADR 0008: OpenEXR is decoded by an in-repo bounded decoder

## Status

Accepted.

## Context

Users want to pick OpenEXR (`.exr`) panoramas wherever `.hdr` is accepted. StbImageSharp, which decodes `.hdr`, `.png`
and `.jpg`, has no EXR support. EXR files are untrusted input parsed inside the game process: a crash there takes the
game down, and a hostile header can claim huge sizes. The plugin zip must stay under 4 MB (`tools/check-package.ps1`)
and ships no native DLLs.

Options considered:
- Native wrappers (tinyexr / OpenEXR via P/Invoke, Magick.NET): native DLLs in a Dalamud plugin, large, and a parser
  crash kills the game.
- SixLabors.ImageSharp: no EXR codec in a released 3.x version, about 2 MB, a split licence, and only a sliver used.
- Small community managed packages: unmaintained, most lack PIZ, none documented as hardened against hostile input.

## Decision

- **An in-repo managed decoder in `Milieu/Imaging/Exr/`**, ported from the OpenEXR reference implementation (ImfHuf,
  ImfWav, ImfPizCompressor, ImfZip, ImfRle, ImfRleCompressor) and treated as a derivative work: THIRD_PARTY_NOTICES
  carries OpenEXR's BSD-3-Clause notice for the whole folder. No package, nothing new to ship. No `unsafe` code, so a
  missed check surfaces as a managed exception (mapped to "couldn't be read"), not a crash.
- **Supported matrix:** single-part, version 2; scanline or tiled (level 0 of ONE_LEVEL, MIPMAP or RIPMAP); HALF,
  FLOAT and UINT channels (mixed allowed), converted to float; compression NONE, RLE, ZIPS, ZIP and PIZ; any line order;
  `envmap` latlong or absent. The output is the dataWindow; the displayWindow is ignored. Colour channels: the
  unprefixed R/G/B, else the only RGB layer, else a `Combined`/`beauty`/`RGBA` layer, else an unprefixed Y as grey; a
  case-only duplicate (`R` + `r`) is ambiguous. Alpha and other channels are dropped.
- **Unsupported variants get one of two specific messages**: `UnsupportedExrCompression` (PXR24, B44, B44A, DWAA,
  DWAB, HTJ2K256, HTJ2K32, unknown ids) and `UnsupportedExrLayout` (multi-part, deep data, cube-map environment,
  luminance/chroma, subsampled channels, no colour channels, several RGB layers with no main one, very small tiles,
  very large tiles or too many channels). Both are found at probe, from the header alone.
- **EXR is HDR** (`ImageKind.Exr`, `IsHdr()`): the 8192x4096 cap, exposure, tone map and the cache key apply as for
  `.hdr`.
- **Caps (`ExrLimits`)**: names up to 255 bytes regardless of the long-names flag; header at most 16 MiB, unknown
  attributes skipped by seeking; chlist at most 64 KiB and 1024 channels; at most 1,048,576 level-0 tiles; at most
  64 MiB (`MaxBlockBytes`) unpacked per chunk. The offset table is read and fully validated before the float output is
  allocated; packed sizes are bounded by the file and the block; decompressors fill pre-sized buffers and fail on short
  or long output (zip-bomb safe); chunks are streamed one at a time and decode is cancellable per chunk.
- **Memory**: output 12 B/px plus a header-sized working set of at most `3 * MaxBlockBytes + 2 MiB` (194 MiB; the 2 MiB covers the
  fixed PIZ tables and the offset table): below
  the `.hdr` path's 24 B/px plus the file. `DecodeBudget` models EXR as `12 * px + WorkingSetBytes`, without the file
  length.
- **Testing exception:** besides synthetic files built in code, the tests check the decoder against tiny fixtures
  written by the official OpenEXR library (PyPI `OpenEXR` 3.5.3), committed as base64 code literals in
  `Milieu.Tests/Support/ExrOracleFixtures.cs` with the generator script in its header comment. Without them a shared
  misreading of the spec by the test writer and the decoder would go unnoticed. Expected values are recomputed from the
  generators, never stored. If the literals are removed the oracle tests skip.

## Consequences

- EXR panoramas from Poly Haven, Blender and most HDRI sources work with no new dependency; the DLL grows by tens of KB.
- The decoder is a new security surface (AGENTS.md Sensitive Areas): every check against `ExrLimits` is load-bearing,
  and changes need the hostile-input and fuzz tests to stay green.
- A tiny hostile file whose header claims up to 8192x4096 (and a valid-looking offset table) can make the decoder
  allocate the output and working set before a chunk fails. That allocation is bounded by the header-derived cap, the
  same as the `.hdr` path's; accepted.
- **Known lever, `MaxBlockBytes`:** an 8192-wide PIZ chunk (32 lines) of FLOAT channels passes 64 MiB above 64
  channels, so huge multilayer renders are refused with the "very large tiles or too many channels" message. If that
  matters, raise the cap (the working set grows 3x the increase) or stream a chunk per channel group.
- Not supported (they can be added later behind the same messages): PXR24, B44/B44A, DWAA/DWAB, HTJ2K, deep and
  multi-part files, mip levels above 0, choosing a layer in the UI. Decode is sequential; parallel chunk decode is a
  possible optimisation.
