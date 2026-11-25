# Bundled backdrop credits

These images are the source of the bundled scenes. They ship pre-converted (BC7 .atex) as release assets under the `backdrops-v3` tag and are downloaded by Milieu on demand.

**Licence:** the repository owner confirmed on 2026-11-08 that the images' licence permits redistribution and does
not require attribution. On 2026-11-24 the owner confirmed the same for the four scenes added in `backdrops-v3`
(autumn, christmasstudio, christmasstudio2, snow). No attribution is therefore listed. Files written by
PrepareBackdrops carry no EXIF metadata; lodge.jpeg is the source file, unchanged.

| File | Changes |
|---|---|
| autumn.jpeg | Downscaled from 11744x5872 to 8192x4096 |
| christmasstudio.jpeg | Radiance HDR 8192x4096 (Christmas Studio.hdr), tone-mapped (ACES, EV 0, the plugin defaults) to 8-bit |
| christmasstudio2.jpeg | Radiance HDR 8192x4096 (Christmas Studio 2.hdr), tone-mapped (ACES, EV 0, the plugin defaults) to 8-bit |
| forest.jpeg | Downscaled from 9000x4500 to 8192x4096 |
| galaxy.jpeg | Downscaled from 10000x5000 to 8192x4096 |
| livingroom.jpeg | Downscaled from 12000x6000 to 8192x4096 |
| livingroom2.jpeg | Downscaled from 10000x5000 to 8192x4096 |
| lodge.jpeg | Unchanged (8192x4096) |
| moon.jpeg | Downscaled from 12000x6000 to 8192x4096 |
| snow.jpeg | Downscaled from 11800x5900 to 8192x4096 |
| sunset.jpeg | 21184x3848 strip scaled to 4096x744, mirror-tiled to 8192 wide so it wraps, horizon placed at eye level, sky and ground faded to an even colour |
| warehouse.jpeg | Downscaled from 11801x5900 to 8192x4096 |

Regenerate the processed images with
`dotnet run --project tools/PrepareBackdrops -c Release -- <source dir> Milieu/DefaultBackdrops`,
then rebuild the .atex, update `Milieu/Assets/BackdropPins.cs` and bump its `Tag` (ADR 0005).
The upload workflow refuses to publish while this file still contains a TODO placeholder in square brackets.
