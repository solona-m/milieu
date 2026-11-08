# Milieu

A Dalamud plugin for FFXIV that swaps the backdrop for an HDRI or panorama image, projected onto a dome around you.
Use the large sky dome for outdoor skies, or the small room dome to put yourself inside a photographed room.

Type `/milieu` in game to open the window, and `/milieu off` to hide the backdrop.

## Requirements

- [Penumbra](https://github.com/xivdev/Penumbra), with mods enabled and a Base collection assigned. Milieu hands
  the game its generated files through a Penumbra temporary mod.

## Using it

1. Pick an image: **Browse…** for your own `.hdr`, `.png` or `.jpg`, or open **Backdrops** and pick one of the
   bundled panoramas (downloaded the first time you open the list).
2. Choose **Sky dome** or **Room dome**.
3. Press **Show**. The first time, the image is converted in the background (an 8K image takes a little while);
   after that it is cached.

While the dome is up:

- **Yaw** and **Height** move it live.
- **Radius**, **Vertical scale**, **Horizontal/Vertical repeat**, **Flip** and **Brightness** apply when you let go
  of the slider.
- **Re-centre on me** moves the dome to where you stand.
- For HDR images, **Exposure** and **Tone map** apply the next time you press **Apply**.

The dome follows you through zone changes (re-centred) and stays up in GPose. It is hidden during cutscenes and on
logout, and never shown automatically when the plugin loads.

### Room dome

The room dome is a small sphere (radius 10 by default, centred at eye height). It only looks right with the camera
inside it, near the centre: that is where the photo was taken from.

### Images

- Equirectangular panoramas (2:1) work best. Wider strips are placed as a band around the horizon with the sky and
  ground faded to an even colour; taller images are stretched (with a warning). A panorama whose edges don't meet
  gets a seam warning.
- HDR images up to 8192x4096; PNG/JPG up to 134 megapixels when the conversion fits in memory.
- The texture the game sees is BC7, 8192x4096 (or 4096x2048), power-of-two on both sides.

## Building

- `dotnet build Milieu/Milieu.csproj -c Release`
- `dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release`

See `AGENTS.md` for the development workflow and `docs/adr/` for the design decisions.

## Licence

AGPL-3.0-or-later. Third-party notices are in `THIRD_PARTY_NOTICES.txt`; the bundled backdrops' terms are in
`Milieu/DefaultBackdrops/CREDITS.md`.
