# Bundled backdrop credits

These images ship as release assets under the `backdrops-v1` tag and are downloaded by Milieu on demand. The
processed files carry no EXIF metadata, so this file is their attribution record.

**Before pushing the repository or tagging `backdrops-v1`, replace every `[TODO]` below.** The upload workflow
refuses to publish while any `[TODO]` remains. Confirm that each licence allows redistribution in a public GitHub
repository and as a downloadable release asset.

| File | Source / URL | Author | Licence | Changes |
|---|---|---|---|---|
| forest.jpeg | [TODO] | [TODO] | [TODO] | Downscaled from 9000x4500 to 8192x4096 |
| galaxy.jpeg | [TODO] | [TODO] | [TODO] | Downscaled from 10000x5000 to 8192x4096 |
| livingroom.jpeg | [TODO] | [TODO] | [TODO] | Downscaled from 12000x6000 to 8192x4096 |
| livingroom2.jpeg | [TODO] | [TODO] | [TODO] | Downscaled from 10000x5000 to 8192x4096 |
| lodge.jpeg | [TODO] | [TODO] (EXIF artist: "Ivan (HiV360) Tsyrkunovich") | [TODO] | Unchanged (8192x4096) |
| sunset.jpeg | [TODO] | [TODO] | [TODO] | 21184x3848 strip scaled to 4096x744, mirror-tiled to 8192 wide so it wraps, horizon placed at eye level, sky and ground faded to an even colour |
| warehouse.jpeg | [TODO] | [TODO] (EXIF artist: "Q'bot") | [TODO] | Downscaled from 11801x5900 to 8192x4096 |

Regenerate the processed images with
`dotnet run --project tools/PrepareBackdrops -c Release -- <source dir> Milieu/DefaultBackdrops`,
then update the pinned table in `Milieu/Assets/DefaultBackdropsDownloadService.cs` and bump `BackdropsTag`.
