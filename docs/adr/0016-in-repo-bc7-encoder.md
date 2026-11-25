# ADR 0016: User images are BC7-encoded by an in-repo opaque encoder

## Status

Accepted (2026-11-21). The visual result on the dome (local and synced), the speed as felt in game and the absence of a
hitch or crash while converting are UNVERIFIED in game. Release gate: flip `Plugin.FastConversionAvailable` to false
before a tag if the in-game visual check of converted user images fails.

## Context

Converting a user image was slow: about 2 minutes for an 8K JPEG on the 28-core build PC. A stage benchmark
(`forest.jpeg`, 8192x4096 → 8192x4096, `ToneMapper.Workers` = 14) put almost all of it in the BC7 encode:

| Stage | Time |
|---|---|
| decode | 1257 ms |
| seam check | 58 ms |
| resample | 222 ms |
| mips | 238 ms |
| BC7 mip 0 (BCnEncoder.Net 2.3.0, `CompressionQuality.Fast`, 14 tasks) | 84876 ms |
| BC7 remaining mips | 24817 ms |

A second measurement ruled out a cheap fix inside BCnEncoder. On a 1024x512 photo-like mip (32,768 blocks), progress
off: one thread 10875 ms (about 330 µs per block); 14 tasks 1384 ms (7.9x scaling); 14 hand-made parallel row strips
1878 ms. The cost per block is the bottleneck, not scaling or progress reporting.

## Decision

- **An in-repo encoder for opaque images** (`Milieu/Imaging/Bc7Block.cs`, one 4x4 block; `Milieu/Imaging/OpaqueBc7.cs`,
  a mip in parallel block rows on at most `ToneMapper.Workers` threads). Written from the Khronos Data Format
  Specification's BC7 section; the partition and anchor tables are format data; no code is ported.
  - Mode 6 for every block: principal axis (8 power-iteration steps), two least-squares refinements, then a ±1 local
    search per endpoint. Both p-bits are 1 and alpha endpoints 127, so alpha decodes to 255 (RGB endpoints are odd).
  - Mode 1 on the two best-ranked two-subset partitions (sum of subset variances; ties to the lower index) whenever mode 6
    is not exact, keeping the lower error. (Superseded: three partitions since `opaque-bc7-2`, amendment below.)
  - Solid blocks: the better of a mode-6 and a mode-5 table, each searched jointly under its one shared index, with the
    anchor rule (an upper index is stored as its mirror with the endpoints swapped). Black and colours with a 0 channel are
    exact through mode 5.
  - Index choice projects each pixel onto the endpoint line and compares that palette entry and its two neighbours.
  - Scalar code, no `unsafe`, nothing allocated per block, fixed iteration counts: the same input gives the same bytes on
    the same machine (not claimed across CPUs).
  - Cancellation lands between block rows as a bare `OperationCanceledException`; progress never decreases and ends at 1.
- **BCnEncoder stays as the reference encoder.** `tools/PrepareBackdrops atex` keeps it, so the published bundled
  textures and `BackdropPins.cs` stay valid with no re-publish (ADR 0005 amendment); the plugin uses it for user images
  only with the kill switch off; the tests use its decoder as an independent reader and its output as the quality bar.
- **The texture cache key carries the encoder's id** (`IBlockEncoder.CacheId`: `bcnenc-fast`, `opaque-bc7-1`; `opaque-bc7-2` since the amendment below), so one
  encoder's file is never served for another. Bump an encoder's id whenever its output changes; `ConverterVersion` stays
  for the shared pipeline. (The plugin wipes `sky_*.atex` at every load, so nothing is reused across builds anyway.)
- **Kill switch** `Plugin.FastConversionAvailable()`.
- Rejected: a native encoder (bc7enc, ISPC bc7e, DirectXTex): a native fault kills the game (ADR 0008's precedent),
  per-CPU binaries, package size. GPU compute: no reliable device access from a plugin. BC1: changes the `.atex` format
  and bands skies. Tuning BCnEncoder: see the measurements.

### Measured (build PC, `Bc7QualityTests`)

Speed, best of 3 after a warm-up, 1024x512, 14 workers: foliage-like synthetic 5211 ms reference vs 76 ms (68.8x); random
noise 6026 ms vs 70 ms (86.7x). Full 8K Show path of `forest.jpeg` through `BackdropPreparer` (decode, light estimate,
thumbnail, encode, write): 5.8 s, down from about 2 minutes.

Quality, RGB PSNR (reference → in-repo), gate: no more than 0.25 dB lower, p99.9 block MSE at most 1.5x + 1:

| Input | Reference | In-repo |
|---|---|---|
| sky (synthetic) | 51.50 | 51.85 |
| night (synthetic) | 54.46 | 54.44 |
| foliage (synthetic) | 31.36 | 31.66 |
| shapes (synthetic) | 30.32 | 31.12 |
| forest.jpeg tiles | 46.70 | 47.53 |
| galaxy.jpeg tiles | 50.81 | 51.86 |
| livingroom.jpeg tiles | 51.09 | 51.77 |
| livingroom2.jpeg tiles | 49.81 | 50.53 |
| lodge.jpeg tiles | 44.76 | 45.70 |
| moon.jpeg tiles | 52.70 | 52.58 |
| sunset.jpeg tiles | 49.25 | 50.88 |
| warehouse.jpeg tiles | 49.79 | 50.33 |

The bit layout is checked three ways: hand-derived golden blocks from the spec (`Bc7GoldenBlocks`), BCnEncoder's decoder,
and the encoder's own packing; every encoded block's reported error equals its decoded error on uniform and structured
fuzz blocks.

### Amendment (2026-11-24): three mode-1 partitions, `opaque-bc7-2`

The quality corpus is every committed bundled JPEG, so adding Autumn (`backdrops-v3`, ADR 0005 amendment) added it to the
gate, and at two partitions its dense foliage failed the worst-block bound: p99.9 block MSE 188.38 against 1.5 x 117.75 + 1
= 177.6 (PSNR was fine, 38.54 → 38.88). Mode 1 now tries the **three** best-ranked partitions
(`OpaqueBc7.DefaultMode1Partitions`), and the encoder id is `opaque-bc7-2`, so every user image re-converts once and
synced staging re-keys. The gates themselves are unchanged. Measured on the build PC with FFXIV running (realistic load):

| Partitions | Autumn PSNR | Autumn p99.9 (gate 177.6) | 8K forest Show, alone (budget 10 s) | Show, with other test classes |
|---|---|---|---|---|
| 2 | 38.88 | 188.38 (fails) | 6.94 s | not measured |
| 3 | 39.16 | 155.56 | 7.81 s | 8.37 s (Bc7 test set) |
| 4 | 39.39 | 147.60 | 9.80 s | 12.69 s (fails) |

Four partitions was rejected: its speed margin is 2% alone and it fails when the test runner's other classes share the
cores (no player scenario does; the plugin converts one image at a time, but a release-time suite would be flaky).

At three partitions, speed 1024x512: foliage 5408 ms reference vs 98 ms (55.0x), noise 7083 ms vs 92 ms (77.2x).
Quality, RGB PSNR reference → in-repo (p99.9 block MSE reference → in-repo):

| Input | Reference | In-repo |
|---|---|---|
| sky (synthetic) | 51.50 (0.90) | 51.87 (0.75) |
| night (synthetic) | 54.46 (0.92) | 54.45 (0.69) |
| foliage (synthetic) | 31.36 (94.98) | 31.81 (85.29) |
| shapes (synthetic) | 30.32 (1485.96) | 32.03 (1218.98) |
| autumn.jpeg tiles | 38.54 (117.75) | 39.16 (155.56) |
| christmasstudio.jpeg tiles | 48.28 (15.58) | 49.68 (10.40) |
| christmasstudio2.jpeg tiles | 45.43 (18.56) | 47.52 (16.77) |
| forest.jpeg tiles | 46.70 (19.19) | 47.62 (15.15) |
| galaxy.jpeg tiles | 50.81 (15.58) | 51.91 (11.92) |
| livingroom.jpeg tiles | 51.09 (10.10) | 51.88 (6.21) |
| livingroom2.jpeg tiles | 49.81 (10.77) | 50.59 (5.46) |
| lodge.jpeg tiles | 44.76 (24.54) | 45.89 (24.25) |
| moon.jpeg tiles | 52.70 (15.25) | 52.64 (12.81) |
| snow.jpeg tiles | 47.53 (20.29) | 48.56 (17.25) |
| sunset.jpeg tiles | 49.25 (22.77) | 51.05 (20.44) |
| warehouse.jpeg tiles | 49.79 (14.06) | 50.45 (11.00) |

The release gate above (the in-game visual check of converted user images, `FastConversionAvailable`) applies to
`opaque-bc7-2` output and is UNVERIFIED. The Show-path speed test is sensitive to the test runner's own parallelism (see
the 4-partition row); at three partitions it measured 7.27 s inside a full `dotnet test` run (2,265 passed, 10 skipped),
so the timing tests stay in the normal parallel run.

## Consequences

Easier: a user image shows in seconds, not minutes; the encoder is plain managed code with a kill switch.
Harder: the repo owns a BC7 encoder. Its output also reaches PSync partners through synced delivery (ADR 0006); staging is
keyed by the texture key, so the new key re-stages automatically. A change to the encoder means bumping its `CacheId` and
re-running `Bc7QualityTests` (Slow) and `OpaqueBc7Tests`. Bundled textures stay on the reference encoder until the owner
regenerates and re-publishes them for another reason.
