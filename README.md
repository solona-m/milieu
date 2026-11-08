# Milieu

A Dalamud plugin for FFXIV that swaps the backdrop for an HDRI or panorama image, projected onto a dome around you.
Use the large sky dome for outdoor skies, or the small room dome to put yourself inside a photographed room.

Type `/milieu` in game to open the window, and `/milieu off` to hide the backdrop.

## Install

Milieu needs [Penumbra](https://github.com/xivdev/Penumbra), with mods enabled and a Base collection assigned
(Milieu hands the game its files through a Penumbra temporary mod).

1. In game, type `/xlsettings` and open **Experimental**.
2. Under **Custom Plugin Repositories** (the first time, read the warning and click **Ok, I have read and
   understood this warning** once it unlocks), paste `https://dl.solona.info/repo.json` into the empty row and
   click **+**. Check that **Enabled** is ticked, then click **Save**.
3. While Milieu is in testing, also turn on **Get plugin testing builds** on the same tab.
4. Type `/xlplugins`, search for **Milieu**, and install it.

Only you see the backdrop; other players see the normal game world.

> Milieu isn't listed in the repository just yet. The first testing build is on its way; until then step 4 won't
> find it.

## Using it

The window shows a grid of scenes. **Click a scene to show it**; there is no Show button.

- **None** turns the backdrop off.
- **Bundled scenes** (Forest, Galaxy, Living Room, Living Room 2, Lodge, Moon, Sunset, Warehouse) come pre-converted.
  A scene downloads (about 43 MB) the first time you click it, then shows within a second or two. **Download all**
  fetches the rest.
- **Add more…** adds your own `.hdr`, `.png` or `.jpg` as a tile and shows it. Your images are converted on your PC
  the first time (an 8K image takes a while), then cached; their tile gets a preview once converted. **×** removes a
  tile from the grid without touching the file.
- **Sky dome / Room dome** picks the dome. Each remembers its own settings.

**Settings** (collapsed by default; the plugin's settings button in `/xlplugins` opens it expanded):

- **Ground projection** (on by default) flattens the bottom of the dome into a floor at your feet, so the image's
  ground lies on the ground. **Camera height** (1.6 by default) is how far the image's horizon sits above that floor;
  the line under the checkbox tells you if the floor is inactive and why.
- **Radius**, **Vertical scale**, **Camera height** and **Yaw** apply when you let go of the slider (the dome is
  replaced, new one first, so there is no flicker). **Re-centre on me** moves the dome to where you stand.
- **Horizontal/Vertical repeat**, **Flip** and **Brightness** adjust the mapping.
- For your own images, **Texture size** (and, for HDR images, **Exposure** and **Tone map**) re-convert
  automatically shortly after you change them.

The dome follows you through zone changes (re-centred) and stays up in GPose. It is hidden during cutscenes and on
logout, and never shown automatically when the plugin loads.

### Room dome

The room dome is small (radius 10 by default). It looks right with the camera inside it, near the centre: that is
where the photo was taken from.

### Images

- Equirectangular panoramas (2:1) work best. Wider strips are placed as a band around the horizon with the sky and
  ground faded to an even colour; taller images are stretched (with a warning). A panorama whose edges don't meet
  gets a seam warning.
- HDR images up to 8192x4096; PNG/JPG up to 134 megapixels when the conversion fits in memory.
- The texture the game sees is BC7, 8192x4096 (or 4096x2048), power-of-two on both sides.

## Troubleshooting

- **"Penumbra isn't running"**: install or enable Penumbra.
- **"This Penumbra version isn't supported"**: update Penumbra.
- **"Milieu can't find the game's VFX functions"**: a game patch changed things; wait for a Milieu update.
- **"Mods are disabled in Penumbra"**: turn mods back on in Penumbra's settings.
- **"Base collection is set to None"** or **"Penumbra didn't pick up the backdrop files"**: assign a Base
  collection in Penumbra, then click the scene again.
- **A download keeps failing**: click the scene again; it resumes where it stopped.

Problems or ideas: [open an issue](https://github.com/solona-m/milieu/issues).

## Building (developers)

- `dotnet build Milieu/Milieu.csproj -c Release`
- `dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release`

See `AGENTS.md` for the development workflow and `docs/adr/` for the design decisions. Bundled scenes are prepared
and published as described in ADR 0005.

## Licence

AGPL-3.0-or-later. Third-party notices are in `THIRD_PARTY_NOTICES.txt`; the bundled scenes' terms are in
`Milieu/DefaultBackdrops/CREDITS.md`.
