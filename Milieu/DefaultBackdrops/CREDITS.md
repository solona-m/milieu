# Bundled backdrop credits

These images are the source of the bundled scenes. They ship pre-converted (BC7 .atex) as release assets under the `backdrops-v2` tag and are downloaded by Milieu on demand.

**Licence:** the repository owner confirmed on 2026-11-08 that the images' licence permits redistribution and does
not require attribution. No attribution is therefore listed. The processed files carry no EXIF metadata.

| File | Changes |
|---|---|
| forest.jpeg | Downscaled from 9000x4500 to 8192x4096 |
| galaxy.jpeg | Downscaled from 10000x5000 to 8192x4096 |
| livingroom.jpeg | Downscaled from 12000x6000 to 8192x4096 |
| livingroom2.jpeg | Downscaled from 10000x5000 to 8192x4096 |
| lodge.jpeg | Unchanged (8192x4096) |
| moon.jpeg | Downscaled from 12000x6000 to 8192x4096 |
| sunset.jpeg | 21184x3848 strip scaled to 4096x744, mirror-tiled to 8192 wide so it wraps, horizon placed at eye level, sky and ground faded to an even colour |
| warehouse.jpeg | Downscaled from 11801x5900 to 8192x4096 |

Regenerate the processed images with
`dotnet run --project tools/PrepareBackdrops -c Release -- <source dir> Milieu/DefaultBackdrops`,
then rebuild the .atex, update `Milieu/Assets/BackdropPins.cs` and bump its `Tag` (ADR 0005).
The upload workflow refuses to publish while this file still contains a TODO placeholder in square brackets.
