# Implementation Plan: Milieu HDRI backdrop dome (v7)

v7 is v6 restricted to **Parts A–C** with the plan-critic's v6 amendments applied. Part D (synced delivery via the m8058 cushion) is **moved to its own follow-up feature**. It builds on branch `feature/hdri-backdrop` at **HEAD 801371b**: 8 bundled scenes, BuildNumber 7, plugin icon, OpenConfigUi opens the window with Settings expanded, Height/Yaw respawn on release. Unchanged v5 content is restated briefly; changed or new items are marked **(v7)**.

## Delta vs v6
1. **Part D removed.**
   - Moved to the follow-up: AC-16 and EDGE-85 to EDGE-91, all `Milieu/Synced/*` files, the `PenumbraIpc` additions (AddTemporaryMod, RemoveTemporaryMod, GetCollectionForObject, GetModDirectory, RedrawAll), ADR 0006, the code-critic.md exception, the Delivery and Hide-cushion settings, and the synced messages, tests, risk flags and UNVERIFIED checks 21–25.
   - Removed from what stays: AC-10's "delivery mode", the config v2 `Delivery`/`HideCushion` fields, the Settings panel's Delivery controls, the synced dispose step, the D22/D23 synced notes, and the "writes into Penumbra's mod directory" risk flag.
   - **Seams kept for D** (smaller option, nothing new built):
     - `PreparedTexture.DiskPath` stays the single source path a delivery copies from.
     - The controller's spawn/remove/teardown stay in private methods (`SpawnDome`, `RemoveDome`, `TeardownCore`) behind the existing `IVfxSpawner`/`IPenumbraRedirects` seams, so D can put a delivery interface there.
     - `Configuration.Migrate` is written as ordered `if (Version < N)` steps, so v3 can be added.
     - The builder keeps emitting the reference's Ptcl Pos Y / Rot Y curves with value 0. The follow-up only needs to bake values, so `PositionY`/`YawRadians` parameters are **not** added now. A test pins Local = 0, labelled "seam for the synced follow-up".
2. **Height, yaw and re-centre apply on release by respawning**, new dome spawned first, then the old one removed. This replaces every "live while dragging" (AC-3, C.3, D8, EDGE-84). Reason, seen in game: a moved VfxObject doesn't move its world-anchored particle.
   - The method is renamed from `ApplyLive` to `ApplyPlacement` (behaviour as on the branch).
   - **Single-swap rule:**
     - With ground projection ON, a Height release does **only** `ApplyRebuild`: a new `.avfx` spawned at the new position, one swap.
     - With projection OFF, a Height release does `ApplyPlacement`.
     - Yaw and Re-centre do `ApplyPlacement`.
     - The **Sky/Room switch** and the **Ground projection toggle** do **only** `ApplyRebuild`. Today the switch posts both `ApplyRebuild` and `ApplyLive`.
3. **Publish guards** added to `tools/publish-backdrops.ps1`:
   - `git rev-parse <tag>^{commit}` equals `HEAD`
   - no local changes to `Milieu/Assets/BackdropPins.cs`
   - `artifacts/backdrops` holds exactly the pinned stems
   - `PrepareBackdrops verify` passes

   All asserted in `PublishBackdropsScriptTests.Guards_present`.
4. **Debounced re-conversion** (HDR settings, texture size) also applies while the selected user image is **Preparing or AwaitingRedirect**: cancel and restart, latest wins. EDGE-77 gains that case.
5. **Thread safety.** Every grid action (tile click, Add more, remove ×, None, `/milieu off`) and the pending auto-show go through `controller.Post`. `EntryCompleted` (download worker thread) only sets state that the tick and draw read. View-model tests assert the posted actions, not direct calls. New EDGE-92.
6. **OpenSettings kept:** `/milieu` (OpenMainUi) opens with Settings collapsed; Dalamud's config button (OpenConfigUi) opens with Settings expanded. View-model test. New EDGE-93.
7. **Eight scenes** (moon added in 801371b; `("moon.jpeg","Moon",4964229,"ae1bb9ba…")` in the v1 table).
   - Counts, totals and the header come from `BackdropPins`: 8 stems; 8 × 44,739,312 = 357,914,496 bytes = **341.3 MiB**.
   - Moon gets a thumbnail and is covered by migration rule 1. Tests pin 8 stems and the name "Moon".
   - The artifacts are regenerated after 801371b. `backdrops-v1` lacks moon; `backdrops-v2` must carry all 8 `.atex`.
8. **Housekeeping.**
   - Normalise or revert the CRLF-only change in `tools/PrepareBackdrops/Program.cs` before rewriting it, so the diff shows only real changes.
   - AGENTS.md: replace the architecture note "published by upload-backdrops.yml" and the "prepare bundled backdrops" command (pins now in `BackdropPins`; `atex`/`verify`/`thumbs`; publish script).
   - The workflow known issue's **cause is recorded as unknown**.
- New EDGE-94 (mode switch / projection toggle single swap). AC count drops from 16 to 15.

### Earlier history (summary)
- **v6:** A pre-converted bundled `.atex` (`backdrops-v2`, local publish, per-scene downloads, `bundled:<stem>`); B scene grid UI (click-to-apply, None, Add more, collapsed Settings, thumbnails); C sky height 400 and ground projection.
- **v5:** injectable backoff, deterministic cancel test, pinned fixture length.
- **v4:** Sunset offline, band fade, `NoWrap`, decode budget, resume.
- **v3:** bundled release assets, prep tool, band/stretch.
- **v2:** auto-mode decisions and critic amendments 1–12.

## Approval Summary
**Goal:** Click a preview to put an HDRI or panorama on a dome around your character, as a big sky or a small room, with no conversion wait for Milieu's own scenes and nothing cutting into the ground.

**Slug:** `hdri-backdrop`

**Acceptance Criteria:**
- AC-hdri-backdrop-1: Given Penumbra is running with mods enabled and a Base collection, and you are logged in, clicking a scene's preview puts an opaque dome textured with that image around your character. There is no Show button.
- AC-hdri-backdrop-2: Sky mode is a large dome (default radius 20000) and Room mode a small one (default radius 10), each remembering its own settings. The room hint says to keep the camera inside.
- AC-hdri-backdrop-3 **(v7)**: Yaw, height, radius and vertical scale apply when you release the slider. The dome is replaced (the new one appears before the old one goes), and nothing re-converts. "Re-centre on me" does the same at your current position.
- AC-hdri-backdrop-4: Horizontal repeat defaults to 2.0 and vertical repeat to 1.0, both adjustable. Flip and brightness are available too. All apply on release without re-converting.
- AC-hdri-backdrop-5: HDR images get exposure and a tone-map operator. PNG/JPG images are used as-is, with the HDR controls greyed out.
- AC-hdri-backdrop-6: Your own images convert in the background with a progress overlay on their tile, are cancellable, and keep the game rendering.
  - HDR up to 8192x4096.
  - PNG/JPG up to 134 MP within a 1.5 GB predicted decode memory, refused up front otherwise.
  - Output BC7 with mips, at most 8192x4096 (4096 option).
  - Cached, so re-selecting is instant.
- AC-hdri-backdrop-7: Clicking "None", `/milieu off`, or unloading the plugin removes the dome and every Penumbra redirect Milieu made. Nothing is left, including leftovers from an earlier session.
- AC-hdri-backdrop-8: Zone changes and cutscenes remove the dome safely and bring it back afterwards, re-centred after a zone change. It stays in GPose. Logout hides it.
- AC-hdri-backdrop-9: If Penumbra is missing, incompatible, has mods disabled or has no Base collection, or the game's VFX functions can't be found, the window says why and clicking a tile only shows that reason. The plugin never crashes.
- AC-hdri-backdrop-10 **(v7)**: Your selection, your added images, mode, per-mode dome settings (including ground projection), mapping, HDR settings and texture size survive a restart, including the upgrade from an earlier config. The dome is never shown automatically on load.
- AC-hdri-backdrop-11 **(v7)**: The grid shows previews of Milieu's 8 bundled scenes (Forest, Galaxy, Living Room, Living Room 2, Lodge, Moon, Sunset, Warehouse) before anything is downloaded, with the total download size (341.3 MB).
  - Nothing downloads until you click a scene or press "Download all".
  - A clicked scene downloads alone, with progress on its tile, and shows when done.
  - A failed download offers Try again.
- AC-hdri-backdrop-12: Panoramas wider than 2:1 become a horizon band with faded sky and ground. Non-wrapping images get a seam warning. Images taller than 2:1 are stretched with a warning.
- AC-hdri-backdrop-13: A downloaded bundled scene shows within a couple of seconds with no conversion step. The settings note that texture size and HDR settings don't apply to bundled scenes.
- AC-hdri-backdrop-14 **(v7)**: The window shows a preview grid at the top (None, bundled scenes, your images) with the selected tile highlighted. Below it are "Add more…" (adds your image as a tile, selects it and shows it), the Sky/Room radios, and a "Settings" panel. The panel is collapsed when the window is opened with `/milieu` and expanded when it's opened from the plugin's settings button. A "×" removes one of your images from the grid without deleting the file.
- AC-hdri-backdrop-15: The panorama no longer cuts into the ground.
  - The sky dome sits 400 above you by default (existing untouched configs are moved up).
  - With "Ground projection" (on by default for the room dome), the image's floor lies flat on the ground at your feet, with its horizon at camera height.
- *(AC-hdri-backdrop-16 moved to the synced-delivery follow-up.)*

**Key decisions:**
- Penumbra temporary mod for all collections at content-hashed `vfx/milieu/...` paths; static VFX with a remove hook and respawn guard. Placement changes respawn (new first, then remove old); geometry changes rebuild. One swap per change.
- Code-authored `.avfx` (the reference's minimal chain, 96x48 sphere) with optional ground projection. Parse-back and parity tests.
- User images: memory-predicted decode, in-house tone mapping, resampling and banding, managed BC7. Bundled scenes: pre-converted pinned `.atex` release assets (`backdrops-v2`, 8 files), built by the plugin's own conversion code, not committed, published by a guarded local script, downloaded per scene, used without conversion.
- Click-to-apply preview grid. Every action is posted to the framework thread. Embedded bundled thumbnails; user thumbnails as a by-product of conversion. Settings collapsed by default.

**Decided (auto mode):** everything in v5, plus:
- **(A)** pre-converted, uncommitted `.atex` (no LFS, no CI); `backdrops-v2` holds all 8 `.atex`; workflow retired for a local publish script; per-scene downloads; `bundled:<stem>` with migration.
- **(B)** None replaces Hide; `/milieu off` = None; auto-show after a download only if still selected; 500 ms debounce for HDR/size re-conversion, including while Preparing or AwaitingRedirect; embedded bundled thumbnails; user thumbnails from conversion mips as raw RGBA; "×" never deletes files.
- **(C)** Sky height default 400 with migration; ground projection Room ON / Sky OFF; one Height field doubling as camera height.
- **(v7)** placement applies on release by respawn; single-swap rule; Part D split out.

**Risk flags:**
- security surface: yes (game-file writers, native hooks/calls, Penumbra IPC, outbound downloads, game-loaded downloaded textures)
- new persisted field: yes (`UserImages`, `GroundProjection`)
- sensitive-category data: yes (image paths incl. `UserImages`; never logged)
- schema migration: yes (config v1 → v2)
- new dependency: no new runtime package (BCnEncoder.Net and CommunityToolkit added to the tool only)
- new/changed route: no

**NEEDS_DECISION:** None. Left to the user outside this run: push the merged commit and tag `backdrops-v2`, then run `tools/publish-backdrops.ps1`.

*(The summary above is what the developer approves; everything below is the detailed contract it stands on.)*

## Source
- User prompt, follow-ups and the v6 requests A–C (verbatim in Requirements). The Part D requests move to the follow-up.
- **(v7)** Branch `feature/hdri-backdrop` at HEAD 801371b:
  - Moon added: 8 scenes, `CREDITS.md` updated, BuildNumber 7, plugin icon.
  - `Plugin.cs`: `OpenMainUi += Toggle`, `OpenConfigUi += OpenSettings`.
  - `BackdropController.ApplyLive` respawns new-then-remove (commit 24ebf3f).
  - `MainWindow`: the mode switch posts `ApplyRebuild` and `ApplyLive`; yaw/height respawn on release.
  - AGENTS.md lines 59 and 72.
- Code read for v6: `DefaultBackdropsDownloadService`, `MilieuAssets`, `BackdropPreparer`, `BackdropRequest`, `Abstractions`, `CacheStore`, `DomeSettings`, `BackdropController`, `Configuration`, `MainWindow`, the view models, `Bc7`, `SphereMesh`, `DomeAvfxBuilder`, `tools/PrepareBackdrops`, `upload-backdrops.yml`, `.gitattributes`, ADR 0004; `gh release view backdrops-v1` (7 JPEGs, no moon).
- Dalamud `ITextureProvider` as used by Proteus: `GetFromManifestResource(...).GetWrapOrDefault()`, `CreateFromRaw(RawImageSpecification.Rgba32(w,h), bytes)`.
- Everything else as in v5: reference mod analysis, `E:\hdri`, Proteus download pattern, VFXEditor, Penumbra, BCnEncoder 2.3.0, repo docs, critiques (incl. the v6 critique).

### Facts established from the reference, used below
These are unchanged from v5: the element chain TmLn#2 → Emit#5 → Ptcl#11; Schd layout; Emit and Ptcl values (Pos Y 400, Scl X 20000 / Y 10000, ACT 3); root; Model 7's layout and biased bytes; curve keys; the `Sky1.atex` header (44,739,312 bytes at 8192x4096, 11 mips); Penumbra resolution.

**Seen in game (24ebf3f):** the dome's one particle is emitted at spawn and stays where it was born, so moving the VfxObject leaves the visible dome behind. Placement changes therefore need a respawn.

## Context & Decisions
Decisions 1–7, 9, 12–16, 18, 20, 21, 26, 28 and 31 are unchanged from v5. Each is restated briefly here; the value tables are kept as in v5.

1. **Delivery:** Penumbra temporary mod for all collections at `vfx/milieu/<key>/sky.atex` and `.../dome.avfx`. Tag `"Milieu"`, priority 0.
2. **Penumbra IPC by label:** `ApiVersion.V5`, `GetEnabledState`, `GetCollection` (0xE0), `AddTemporaryModAll.V5`, `RemoveTemporaryModAll.V5`, `ResolveDefaultPath`, events `Initialized`/`Disposed`.
3. **Penumbra required**, with a blocked-state order.
4. **Static VFX spawning** (VFXEditor `StaticVfx`), sources as in v5.
5. **Native string lifetimes.**
6. **Remove hook** with an Interlocked dead flag.
7. **Early-removal guard.**
8. **(v7) Baked vs placement settings.**
   - **Baked into the `.avfx`** (`ApplyRebuild` = new `.avfx`, register, spawn new, remove old): radius, vertical scale, repeats, flip, brightness, ground projection and its floor.
   - **Placement** (`ApplyPlacement` = spawn the same `.avfx` at the new position/yaw, then remove the old): yaw, height (when projection is off), re-centre.
   - **Single-swap rule:**
     - A change that affects the mesh triggers only `ApplyRebuild`, whose spawn already uses the current placement: Height with projection on, the Sky/Room switch, the projection toggle.
     - A change that only moves the dome triggers only `ApplyPlacement`.
     - Never both.
   - Applies on release (sliders) or on change (checkbox, radio).
   - Rejected: moving the VfxObject live (doesn't move the particle; seen in game) and runtime `Scale`.
9. **Code-authored `.avfx`.**
10. **(v7) Deliberate differences from the reference:** as in v5, plus an optional ground-projected mesh (off = reference layout). Ptcl Pos Y / Rot Y stay 0; that is the seam for the synced follow-up, pinned by a test.
11. **(C) Sphere mesh:** `SphereMesh.Build(segments, rings, float? floorY = null)`. When `floorY` is set (0 < floorY < 1), a vertex with d.y < 0 gets position d · min(1, floorY / (−d.y)). UVs, normals (−d), tangents and indices are unchanged. Null is byte-identical to v5.
12. **Image input** for user images (limits as in v5).
13. **Panorama shape.**
14. **Band fill.**
15. **Seam detection.**
16. **Tone mapping.**
17. **(A) Encoding:** user images use the shared `TextureConversion` (resample → mips → BC7 → `AtexWriter`), with hooks `onMip0` and `onThumb` (the 256x128 mip). BCnEncoder observes cancellation inside a mip (as on the branch).
18. **Memory budget.** Bundled scenes decode nothing.
19. **Cache:** `<config>/cache` as in v5. Thumbnails in `<config>/cache/thumbs` (`thumb_*.rgba`) have their own scoped cleanup. Bundled `.atex` files in `<config>/DefaultBackdrops` are never touched by cache cleanup.
20. **Threading.** **(v7)** UI actions are `Post`ed to the framework tick (decision 34).
21. **Redirect wait.**
22. **Lifecycle:** readiness, events, a 30-frame respawn delay, GPose kept, cutscenes suspend.
23. **Dispose order:**
    1. conversion job (2 s)
    2. download service (cancel the queue, 2 s, dispose HttpClient)
    3. teardown (5 s; leak on timeout)
    4. hook, events, Update, command, windows
    5. pool string
    - **(v7)** Thumbnail texture wraps are disposed with the window.
24. **(v7) Persistence:** `Configuration` **Version 2**.
    - `ImagePath`: null (None), `bundled:<stem>`, or a user path.
    - `UserImages: List<string>`, unique by case-insensitive full path, in the order added.
    - `DomeSettings.GroundProjection`.
    - `Migrate(string configDirectory)` as ordered steps, so v3 can follow. `if (Version < 2)`:
      1. An `ImagePath` directly inside `<configDirectory>/DefaultBackdrops` whose stem is pinned (all 8, incl. moon) becomes `bundled:<stem>`.
      2. Any other non-null `ImagePath` is appended to `UserImages`.
      3. `Sky.HeightOffset == 0` becomes 400.
      4. `Room.GroundProjection = true`, `Sky.GroundProjection = false`.
      5. Version = 2.
    - User-set values are never overwritten.
    - `Clamp()`: an unknown `bundled:` id → null; drop empty or duplicate `UserImages`.
    - Never auto-shown.
25. **(A, v7) Pre-converted per-scene downloads.**
    - **`BackdropPins`** (pure; linked into the tool): `Tag = "backdrops-v2"`; `(Stem, DisplayName, Bytes = 44,739,312, Sha256)[]` for forest, galaxy, livingroom, livingroom2, lodge, **moon**, sunset, warehouse; `Count` (8); `TotalBytes` (357,914,496 = 341.3 MiB); `CreditsUrl` at `/blob/backdrops-v2/`; `RemoteName`; `TryParseId`/`IdOf`; `Verify(dir, tag)`.
    - **`DefaultBackdropsDownloadService`:**
      - `Request(stem)`/`RequestAll()`; one FIFO worker.
      - Entry states: NotDownloaded, Queued, Downloading(bytes), Downloaded, Failed, Unpublished.
      - A 404/410 marks the entry and moves on. Any other failure fails the entry and drains the queue.
      - `CleanupStale()` at startup on a background task (deletes `*.jpeg`, `*.jpeg.tmp`, and unpinned `*.atex`/`*.atex.tmp` at the top of `<config>/DefaultBackdrops`).
      - `MarkCorrupt`; implements `IBundledBackdrops`.
      - **`EntryCompleted(stem)` fires on the worker thread and only bumps the service `Version`.** Consumers read state on the tick or draw.
      - Resume, pins, the non-retried move failure, sanitised logs and the 2 s Dispose wait are unchanged.
    - **Lazy:** no network until a bundled tile is clicked or "Download all" is pressed.
26. **Downloader port and logging.**
27. **(A/B, v7) `tools/PrepareBackdrops`.**
    - The CRLF-only diff in `Program.cs` is normalised or reverted first.
    - Commands:
      - `<srcDir> <dstDir>`: produces the committed JPEGs; the plan table includes moon.
      - **`atex <jpegDir> <outDir>`**: `TextureConversion.ConvertToAtex` at 8192x4096; prints the pin lines.
      - **`verify <atexDir> --tag <tag>`**
      - **`thumbs <jpegDir> <outDir>`**: 256x128 JPEG q85 into `Milieu/Resources/Thumbs/<stem>.jpg`.
    - Links: TextureConversion, EquirectResampler, EquirectLayout, BandFill, SeamDetector, MipChain, Bc7, ToneMapper, AtexWriter, BackdropPins. Packages: StbImageSharp, StbImageWriteSharp, BCnEncoder.Net 2.3.0, CommunityToolkit.HighPerformance 8.4.2.
28. **Licence:** confirmed by the owner (`CREDITS.md`, moon included).
29. **UI:** see decision 34.
30. **(C) Defaults:**
    - Sky: radius 20000 (200–20000), vertical scale 1 (0.25–2), **height 400** (−2000..2000), yaw 0, projection **off**.
    - Room: radius 10 (1–100), vertical scale 1, height 1.6 (−10..10), yaw 0, projection **on**.
    - The rest is unchanged.
31. **Privacy.**
32. **(A) Bundled show path:**
    - `BackdropPreparer` gets `IBundledBackdrops`. For `bundled:<stem>`:
      - not downloaded → `BundledNotDownloaded`
      - verify size, header (`AtexWriter.ExpectedHeader(8192,4096)`) and SHA (once per session per path+length+mtime)
      - on failure: `MarkCorrupt` and `BundledCorrupt`
      - otherwise `PreparedTexture(Key = sha[..16], DiskPath = PathOf(stem), GamePath = vfx/milieu/<sha16>/sky.atex, 8192, 4096, Jpeg, [], Preconverted = true)`
    - No decode, no encode, no copy. MaxWidth, EV and tone map are ignored. Status `LoadingBundled`.
33. *(Synced delivery moved to the follow-up.)*
34. **(B, v7) Scene grid UI.**
    - **`SceneGridViewModel`** (pure), constructed with an `Action<Action> post` (= `controller.Post`).
    - `Tiles`: `Tile(Id, Kind {None, Bundled, User}, Label, ThumbKey, Selected, State {Idle, Hidden, Shown, NotDownloaded, Queued, Downloading(pct), Converting(pct), Unpublished, Failed, Missing}, CanRemove)`. Order: None, the 8 bundled in `BackdropPins` order, then user images in the order added.
    - **Every action is posted; nothing calls the controller directly:**
      - **Click None:** post { `ImagePath = null`; save; `Hide()` }.
      - **Click bundled, downloaded:** post { select; save; `Show()` }.
      - **Click bundled, not downloaded:** post { select; save; `Request(stem)` }, and set `pendingShow = stem`.
      - **Click user:** post { select; save; `Show()` }. If the file is missing, post { select; save; status `FileMissing` }.
      - **Click the selected tile:** post `Show()` when Hidden or failed; nothing when Shown, Preparing or AwaitingRedirect.
      - While a blocker is active, the posted action selects and surfaces the blocker only.
    - **Pending auto-show:** on draw, if `pendingShow` is downloaded and still the selection, post `Show()` and clear `pendingShow`.
    - **`AddMore(path)`:** post { append unless present (case-insensitive full path), select, save, `Show()` }.
    - **`Remove(tile)`:** post { remove from `UserImages` (file untouched); if selected → `ImagePath = null`, save, `Hide()` }.
    - **`/milieu off`:** posts the same action as clicking None.
    - **`DownloadAll()`; `TryAgain(stem)`.**
    - **`NotifyConversionSettingChanged()`:** the controller re-runs `Show()` 500 ms after the last call when the selection is a user image in state Shown, Suspended, **Preparing or AwaitingRedirect** (`Show()` cancels the in-flight job; latest wins). Hidden or bundled → save only.
    - **Settings panel:** `SettingsOpen` is false when opened via `Toggle` (`/milieu`, OpenMainUi) and true via `OpenSettings` (OpenConfigUi). Applied once with `ImGui.SetNextItemOpen`.
      - Panel contents: dome sliders (radius, vertical scale, height / camera height, yaw), ground projection, mapping (repeats, flip, brightness), conversion (EV, tone map, size; disabled with `BundledNote` for bundled), Re-centre on me, Credits link.
    - **Removed:** Show/Apply/Hide buttons and the Browse… row ("Add more…" replaces it).
    - **Thumbnails:**
      - Bundled: embedded `Milieu.Resources.Thumbs.<stem>.jpg` via `GetFromManifestResource`.
      - User: `ThumbStore` (pure: key = hash16(path|length|mtime), save, cleanup) plus a thin `ThumbnailCache` (`CreateFromRaw` wraps, disposed on removal and in Dispose; placeholder with the file name when missing).
      - The thumbnail is written by `BackdropPreparer` from the 256x128 mip through `onThumb` (raw RGBA, 131,072 bytes, atomic).

## Requirements
User prompt (verbatim):
"I want to make a new ffixv plugin. You can look at proteus as an example. This will be in it's own repo. What it will do is change backdrops using hdri files. An example of a mod that does this is "C:\Users\solon\OneDrive\Desktop\Pickles Venue.pmp". I think that mod doubles the uv or something to make the image crisp so do it like that. If i understand correctly, the skybox the hdri is projected on is a big dome. That's fine for outside, but I'd like to potentially have a small sized one that project room images. Init the repo with the /feature workflow and use that to build the plugin."

Follow-ups (verbatim):
- "The plugin name should be Milieu"
- "I've added some licenced hdri images to e:\hdri. Look at how proteus does effect and matcap downloads for how these should be shared."

v6 requests in scope (verbatim):
- "oh conversion of the default images takes a long time. we should upload the converted images instead"
- "The top should show previews of each scene to select. Add more: browse should be below that. They should take effect after clicking a preview with no show button. Sky dome and room dome are fine. Put the rest of the settings in a collapsed panel."
- "the images cut into the ground too much. they should be positioned higher."

(The cushion/PSync requests move to the synced-delivery follow-up.)

Caller constraints: small, testable units, tests first; only targeted tests; in-game results UNVERIFIED (build into `E:\repos\Milieu\Milieu\bin\Release\win-x64`); smaller and safer scope.

Developer and coordinator decisions (auto mode), all binding:
- v2–v5 decisions as recorded in v5 (incl. the corrected 1,227,959,552).
- v6 A–C decisions as in v6.
- **v7:**
  1. Part D split out.
  2. Placement applies on release by respawn (new first), with the single-swap rule.
  3. Publish guards (tag at HEAD, clean `BackdropPins.cs`, exact stems, verify).
  4. Debounce also while Preparing or AwaitingRedirect.
  5. All grid actions and the auto-show posted; `EntryCompleted` only sets state.
  6. OpenSettings: collapsed via `/milieu`, expanded via the config button.
  7. Eight scenes (341.3 MiB).
  8. Housekeeping (CRLF, AGENTS.md lines, workflow cause unknown).
- Plan-critic amendments 1–12 from v1 remain requirements.

AGENTS.md rules: BuildNumber bump; warnings-as-errors; one `*Tests.cs` per unit; env-gated file tests; Slow trait.

## Contract
- **Routes:** None. Outbound HTTPS GETs only (bundled scenes).
- **Chat commands:** `/milieu` toggles the window (Settings collapsed); `/milieu off` = select None (hides, persists None). Dalamud's config button opens the window with Settings expanded.
- **Game files:** `vfx/milieu/<16 hex>/sky.atex` and `.../dome.avfx`, redirected for all collections. User textures live in `<config>/cache`; bundled textures are keyed by the pinned SHA16 and live in `<config>/DefaultBackdrops/<stem>.atex`.
- **Penumbra:** tag `"Milieu"`, priority 0, all collections. Read-only: `GetEnabledState`, `GetCollection(0xE0)`.
- **Bundled assets:** tag `backdrops-v2`, assets `<stem>.atex` for the 8 stems (BC7 8192x4096, 11 mips, 44,739,312 bytes). Display names as in AC-11. Embedded thumbnails `Milieu.Resources.Thumbs.<stem>.jpg` (256x128).
- **Config schema (Version 2):**
  - `string? ImagePath`, `List<string> UserImages`, `DomeMode Mode`
  - `DomeSettings Sky`, `DomeSettings Room` {`Radius`, `VerticalScale`, `HeightOffset`, `YawDegrees`, `GroundProjection`}
  - `float HorizontalRepeat`, `float VerticalRepeat`, `bool FlipHorizontal`, `float Brightness`
  - `float ExposureEv`, `ToneMapOperator ToneMap`, `int MaxTextureWidth`
  - Migration per D24.
- **Messages:** v5 keys unchanged, plus:
  - `BundledNotDownloaded` "{name} isn't downloaded yet."
  - `BundledCorrupt` "{name} was damaged on disk; download it again."
  - `LoadingBundled` "Loading {name}…"
  - `BundledNote` "Bundled scenes come pre-converted at 8192x4096, so the texture size and HDR settings don't apply."
  - Tile overlays: "Not downloaded", "Queued", "{pct}%", "Converting {pct}%", "Not published yet", "Failed – click to retry", "Missing", "Click to show".
  - Grid header, from `BackdropPins`: "Scenes (8 bundled · 341.3 MB to download)". "Download all" while any bundled scene is missing.
  - "Height" becomes "Camera height" when ground projection is on; tooltip "Distance from the floor to the dome centre."
  - Blocker priority: unchanged.

## Approach
v5 steps are as on the branch. v7 steps are test-first, with a commit at each pipeline step, in the order A → C → B.

- **A0 (housekeeping).** Normalise or revert the CRLF-only diff in `tools/PrepareBackdrops/Program.cs` (`git diff --ignore-cr-at-eol` must show nothing before the rewrite).
- **A1.** Extract `TextureConversion` (behaviour-preserving) and add `AtexWriter.ExpectedHeader`.
- **A2.** Add `BackdropPins` (8 stems). Add the tool commands `atex`/`verify`/`thumbs`; add `artifacts/` to `.gitignore`. Regenerate from HEAD's 8 JPEGs, paste the pins, run verify.
- **A3.** Download-service rework (per-entry queue, `CleanupStale`, `MarkCorrupt`, `IBundledBackdrops`, `EntryCompleted` that only bumps `Version`).
- **A4.** Preparer bundled path; controller `LoadingBundled`.
- **A5.** Add `tools/publish-backdrops.ps1 -Tag <tag> [-Dir artifacts/backdrops]` with these guards, in order:
  1. tag regex `^backdrops-v\d+\z`
  2. `git rev-parse "<tag>^{commit}"` equals `git rev-parse HEAD`
  3. `git diff --quiet HEAD -- Milieu/Assets/BackdropPins.cs` (no local changes)
  4. `git ls-remote --exit-code --tags origin refs/tags/<tag>`
  5. no existing `gh release view <tag>`
  6. the `*.atex` names in `<dir>` equal the pinned stems exactly (the script asks `PrepareBackdrops verify`, which also checks this)
  7. `dotnet run --project tools/PrepareBackdrops -c Release -- verify <dir> --tag <tag>` passes
  8. `gh release create <tag> --verify-tag --latest=false` with only `<dir>/*.atex`, then print the checksum table
  - Delete `.github/workflows/upload-backdrops.yml` and `Milieu.Tests/UploadBackdropsWorkflowTests.cs`.
- **C1.** `SphereMesh` floor projection; `DomeAvfxParams.FloorY?`. The builder keeps Pos Y / Rot Y = 0 (seam test).
- **C2.**
  - `DomeSettings.GroundProjection` and defaults (Sky height 400).
  - The controller computes `FloorY = HeightOffset / (Radius·VerticalScale)` when on (null when HeightOffset ≤ 0 or ≥ Radius·VerticalScale).
  - Rename `ApplyLive` → `ApplyPlacement` (same respawn behaviour).
  - Single-swap routing in one pure helper, `BackdropViewModel.ApplyOf(setting, groundProjection)` → `Placement | Rebuild`: Height → Rebuild if projection on, else Placement; Yaw → Placement; Radius, VerticalScale, repeats, flip, brightness, Mode, GroundProjection → Rebuild.
  - `MainWindow` posts exactly one action per change.
- **C3.** `Configuration` v2 `Migrate(configDirectory)` (ordered steps) and `Clamp`; `Plugin.cs` passes `ConfigDirectory`.
- **B1.** `ThumbStore` and the preparer's `onThumb`; run the tool's `thumbs` command and commit the 8 thumbnails; csproj `EmbeddedResource`.
- **B2.** `SceneGridViewModel` (posting, pending auto-show) replaces `BackdropListViewModel`. `BackdropViewModel` is trimmed to status, settings and `ApplyOf`. Controller debounce `RequestReconvert()` (Shown, Suspended, Preparing, AwaitingRedirect).
- **B3.** `MainWindow` rebuilt as grid → Add more… → radios → Settings (collapsed via `Toggle`, expanded via `OpenSettings`); `ThumbnailCache`; `/milieu off` → the posted None action; background `CleanupStale` and thumbnail cleanup at startup.
- **Docs.**
  - ADR 0005 (pre-converted assets; LFS / CI / local-publish trade-offs; retired workflow, **cause of the failure to trigger unknown**).
  - ADR 0004 "Partly superseded by ADR 0005".
  - README (grid, Add more, settings, publishing).
  - AGENTS.md:
    - architecture line → "DefaultBackdrops/ committed source JPEGs (NOT shipped; pre-converted .atex are built into artifacts/backdrops and published with tools/publish-backdrops.ps1)"
    - Commands → "prepare bundled backdrops (rarely): `dotnet run --project tools/PrepareBackdrops -c Release -- <source dir> Milieu/DefaultBackdrops`, then `-- atex Milieu/DefaultBackdrops artifacts/backdrops`, paste the pins into `Milieu/Assets/BackdropPins.cs` and bump its tag, `-- verify artifacts/backdrops --tag <tag>`, `-- thumbs Milieu/DefaultBackdrops Milieu/Resources/Thumbs`; publish (user): `pwsh tools/publish-backdrops.ps1 -Tag <tag>`"
- **Build.** BuildNumber bump and Release build.
  - Optional staging for in-game tests: copy `artifacts/backdrops/*.atex` into `%AppData%\XIVLauncher\pluginConfigs\Milieu\DefaultBackdrops\`.
  - Run the pre-review targeted set.

## Files
v5 files are as on the branch. **(v7)** changes:
- **A:**
  - NEW `Milieu/Backdrop/TextureConversion.cs`, `Milieu/Assets/BackdropPins.cs`
  - EDIT `Milieu/Assets/DefaultBackdropsDownloadService.cs` (per-entry queue; pins moved out), `Milieu/Backdrop/BackdropPreparer.cs`, `BackdropRequest.cs` (`Preconverted`), `Abstractions.cs` (`IBundledBackdrops`), `Messages.cs`, `BackdropController.cs`, `Milieu/GameFiles/AtexWriter.cs` (`ExpectedHeader`)
  - EDIT `tools/PrepareBackdrops/Program.cs` (line endings normalised first; `atex`/`verify`/`thumbs`) and `.csproj`
  - NEW `tools/publish-backdrops.ps1`
  - DELETE `.github/workflows/upload-backdrops.yml`, `Milieu.Tests/UploadBackdropsWorkflowTests.cs`
  - EDIT `.gitignore` (`artifacts/`)
  - NEW `docs/adr/0005-pre-converted-bundled-backdrops.md`; EDIT ADR 0004 status
- **B:**
  - NEW `Milieu/Gui/SceneGridViewModel.cs`; DELETE `Milieu/Gui/BackdropListViewModel.cs`
  - EDIT `Milieu/Gui/BackdropViewModel.cs` (status, settings, `ApplyOf`), `Milieu/Gui/MainWindow.cs` (new layout; `OpenSettings` kept)
  - NEW `Milieu/Gui/ThumbnailCache.cs`, `Milieu/Backdrop/ThumbStore.cs`
  - NEW `Milieu/Resources/Thumbs/{forest,galaxy,livingroom,livingroom2,lodge,moon,sunset,warehouse}.jpg`
  - EDIT `Milieu/Milieu.csproj` (`EmbeddedResource Include="Resources\Thumbs\*.jpg"`)
  - EDIT `Milieu/Plugin.cs` (`/milieu off` = posted None, background cleanups, wiring, BuildNumber bump)
- **C:**
  - EDIT `Milieu/GameFiles/SphereMesh.cs` (`floorY`), `Milieu/GameFiles/DomeAvfxBuilder.cs` (`FloorY`)
  - EDIT `Milieu/Backdrop/DomeSettings.cs` (`GroundProjection`, Sky height 400)
  - EDIT `Milieu/Configuration.cs` (Version 2, ordered migration, `UserImages`)
  - EDIT `Milieu/Backdrop/BackdropController.cs` (`ApplyPlacement` rename, `FloorY`, debounce)
- **Shared:** EDIT `AGENTS.md`, `README.md`.
- **Tests:**
  - NEW: `TextureConversionTests`, `BackdropPinsTests`, `BundledAtexTests`, `PublishBackdropsScriptTests`, `SceneGridViewModelTests` (replaces `BackdropListViewModelTests`), `ThumbStoreTests`, `BundledThumbsTests`
  - EDIT: `DefaultBackdropsDownloadServiceTests`, `BackdropPreparerTests`, `BackdropControllerTests`, `BackdropViewModelTests`, `ConfigurationTests`, `DomeAvfxBuilderTests`, `SphereMeshTests`, `BundledBackdropsTests` (8 stems, Moon), `Bc7Tests`, `AtexWriterTests`, `PluginCommandTests`, `Support/Fakes.cs`

## Out of Scope
All v5 exclusions still apply. In addition:
- **Synced delivery via the m8058 cushion (follow-up feature):** AC-16, EDGE-85 to EDGE-91, the PSync findings, the V0 manipulation string, the cushion-hide texture, the Milieu-owned folder in Penumbra's mod directory, and the code-critic rule exception.
- **(A)** native BC7 encoder; 4096 or HDR bundled variants; re-publishing JPEGs; LFS; CI regeneration; fixing the retired workflow; changing `backdrops-v1`; creating the `backdrops-v2` tag or release in this run.
- **(B)** reordering or renaming tiles; deleting user files; thumbnails before a user image's first conversion; per-tile settings.
- **(C)** ground projection for bands beyond the band fill; horizon detection; per-image heights; live dragging of placement.

## Edge Cases
EDGE-1 to EDGE-43, -47, -51, -52 and -54 to -60 are unchanged from v5. EDGE-44 to EDGE-84 are as in v6, except where marked **(v7)**. EDGE-85 to EDGE-91 are moved to the synced follow-up.
- EDGE-hdri-backdrop-44: An asset answers 404/410. "Not published yet" on that tile and the queue continues. Credits is hidden if any scene is unpublished and none is downloaded.
- EDGE-hdri-backdrop-45: Transport failure. The tile is Failed and the queue drains; the `.tmp` is kept.
- EDGE-hdri-backdrop-46: Unload mid-download. Cancelled (not a failure), `.tmp` kept, 2 s wait, resume later.
- EDGE-hdri-backdrop-48: Lazy. No network at load, at window open, or when the grid is drawn.
- EDGE-hdri-backdrop-49: Selected bundled `.atex` deleted. "Not downloaded"; clicking re-downloads it and then shows it.
- EDGE-hdri-backdrop-50 **(v7)**: Publish safety. The script refuses:
  - a malformed tag, or a tag not at `HEAD`
  - local changes to `BackdropPins.cs`
  - a tag not pushed to origin, or an existing release
  - an artifact set that isn't exactly the 8 pinned stems
  - any `verify` problem

  It uploads only `*.atex` with `--latest=false` and `--verify-tag`.
- EDGE-hdri-backdrop-53: Duplicate requests fetch each file once.
- EDGE-hdri-backdrop-61: A not-downloaded bundled scene downloads first, then shows. An early `Show()` is a no-op with `BundledNotDownloaded`.
- EDGE-hdri-backdrop-62: A damaged bundled file is deleted, `BundledCorrupt` is shown, and nothing is registered or spawned.
- EDGE-hdri-backdrop-63: `CleanupStale` scope (v1 JPEGs incl. `moon.jpeg`).
- EDGE-hdri-backdrop-64 **(v7)**: v1 migration: a bundled path → `bundled:<stem>` for all 8 stems incl. moon; a user path → selection and `UserImages`.
- EDGE-hdri-backdrop-65: Unknown bundled id → None.
- EDGE-hdri-backdrop-66: Second bundled click while one downloads. Both download in order; only the last-clicked scene auto-shows (if still selected).
- EDGE-hdri-backdrop-67: A bundled selection ignores size and HDR; `BundledNote` is shown and the size combo disabled.
- EDGE-hdri-backdrop-68: Deterministic encode on one machine.
- EDGE-hdri-backdrop-69: The tool and preparer produce identical bytes.
- EDGE-hdri-backdrop-70: None and `/milieu off` hide the dome, clear redirects and persist null.
- EDGE-hdri-backdrop-71: Clicking the selected tile re-shows when Hidden or failed; no-op when Shown, Preparing or AwaitingRedirect.
- EDGE-hdri-backdrop-72: Add more with a duplicate path selects the existing tile.
- EDGE-hdri-backdrop-73: A missing user file shows "Missing"; clicking gives `FileMissing`.
- EDGE-hdri-backdrop-74: "×" on the selected tile → None and Hide; the file is untouched.
- EDGE-hdri-backdrop-75: A download completes after reselection. No auto-show.
- EDGE-hdri-backdrop-76: A missing user thumbnail shows a placeholder.
- EDGE-hdri-backdrop-77 **(v7)**: HDR or texture-size changes re-run conversion once, 500 ms after the last change, for a selected user image that is Shown, Suspended, **Preparing or AwaitingRedirect**.
  - An EV change during Preparing cancels the running job and restarts with the new EV; only the last result registers.
  - Hidden or bundled → save only.
- EDGE-hdri-backdrop-78 **(v7)**: Settings is collapsed when the window is opened via `/milieu` and expanded via OpenConfigUi.
- EDGE-hdri-backdrop-79: No Show, Apply or Hide button and no Browse… row remain.
- EDGE-hdri-backdrop-80: Thumbnail cleanup scope.
- EDGE-hdri-backdrop-81: Ground projection on flattens only below the floor; UVs, normals and indices are unchanged; off is identical to the reference.
- EDGE-hdri-backdrop-82: `FloorY` is null when Height ≤ 0 or ≥ Radius × VerticalScale.
- EDGE-hdri-backdrop-83: Migration sets Sky height 0 → 400 and keeps user values; projection defaults are applied.
- EDGE-hdri-backdrop-84 **(v7)**: Height release.
  - Projection on → exactly one `ApplyRebuild` (new `.avfx` spawned at the new position, then the old one removed); no `ApplyPlacement`.
  - Projection off → exactly one `ApplyPlacement` (new spawn first, then remove the old).
  - Dragging without releasing changes nothing in game.
- EDGE-hdri-backdrop-92 **(v7)**: Thread safety. Tile clicks, Add more, "×", None, `/milieu off` and the pending auto-show only enqueue actions through `controller.Post`, which run on the framework tick. `EntryCompleted` from the download worker only bumps `Version`; no controller or Penumbra call happens off the framework thread.
- EDGE-hdri-backdrop-93 **(v7)**: Reopening the window via `/milieu` after OpenConfigUi doesn't keep forcing Settings open: the expand request is one-shot.
- EDGE-hdri-backdrop-94 **(v7)**: The Sky/Room switch and the ground-projection toggle while shown cause exactly one `ApplyRebuild` (one swap at the new mode's settings and placement), never a second respawn.

## Test Strategy
All v5 entries stay as on the branch, except these:
- `UploadBackdropsWorkflowTests` is removed.
- `BackdropListViewModelTests` becomes `SceneGridViewModelTests`.
- `Pinned_list_matches_the_committed_folder` becomes `BackdropPinsTests.Pins_cover_exactly_the_committed_jpeg_stems`.
- Show-button assertions move to posted grid actions.
- Part D tests move to the follow-up.

**Part A**
- [AC-hdri-backdrop-11] `SceneGridViewModelTests.BundledTiles_EightWithDisplayNamesAndEmbeddedThumbKeys` (incl. "Moon") and `Header_DerivedFromPins` ("Scenes (8 bundled · 341.3 MB to download)", computed from `BackdropPins.Count` and `TotalBytes`).
- [AC-hdri-backdrop-11, EDGE-48] `DefaultBackdropsDownloadServiceTests.Nothing_downloads_until_an_entry_is_requested`; `SceneGridViewModelTests.DrawingGrid_PostsNothing`.
- [AC-hdri-backdrop-11] `DefaultBackdropsDownloadServiceTests.Request_fetches_only_that_entry_with_pinned_size_and_sha` (base URL ends `/backdrops-v2/`).
- [AC-hdri-backdrop-11] `DefaultBackdropsDownloadServiceTests.RequestAll_downloads_missing_one_at_a_time_in_table_order`, `Per_entry_progress_is_reported`.
- [AC-hdri-backdrop-11] `BackdropPinsTests.Pins_cover_exactly_the_committed_jpeg_stems`: exactly the 8 committed stems incl. moon/"Moon"; Bytes = `Layout(8192,4096).TotalLength`; `TotalBytes == 357,914,496`; tag `backdrops-v2`; 64-hex SHAs; `CreditsUrl` at `/blob/backdrops-v2/`.
- [AC-hdri-backdrop-11] `BundledAtexTests.Reference_PinnedAtexMatchesArtifacts`: gated on `MILIEU_BACKDROP_ATEX_DIR`, skipping with "set MILIEU_BACKDROP_ATEX_DIR to the PrepareBackdrops atex output; the .atex files are not committed (ADR 0005)". `Verify` is clean for 8 files.
- [AC-hdri-backdrop-11] `BundledBackdropsTests.CommittedImages_ObeyLimitsAndShapes` (8 images, all 8192x4096) and `Credits_ListsEveryPinnedStem`.
- [AC-hdri-backdrop-13] `BackdropPreparerTests.Bundled_UsesDownloadedAtex_NoDecodeNoEncode`, `Bundled_IgnoresMaxWidthAndHdrSettings` *(also EDGE-67)*; `BackdropControllerTests.Show_Bundled_RegistersDownloadedFileDirectly_WithLoadingStatus`; `BackdropViewModelTests.Bundled_ShowsNote_DisablesSizeCombo`.
- [AC-hdri-backdrop-6] `TextureConversionTests.ConvertToAtex_HeaderEqualsLayout`; `AtexWriterTests.ExpectedHeader_MatchesWrittenHeader`.
- [EDGE-hdri-backdrop-44] `DefaultBackdropsDownloadServiceTests.Http_404_or_410_marks_entry_unpublished_and_continues`, `Unpublished_pack_when_any_404_and_none_downloaded`; `SceneGridViewModelTests.CreditsLink_HiddenWhileUnpublished`.
- [EDGE-hdri-backdrop-45] `DefaultBackdropsDownloadServiceTests.Transport_failure_fails_entry_and_drains_queue_TryAgain_refetches`.
- [EDGE-hdri-backdrop-46] `DefaultBackdropsDownloadServiceTests.Dispose_mid_download_cancels_queue_without_failing_and_waits_bounded`; `ResilientDownloaderTests.Cancelled_fetch_keeps_tmp_and_next_fetch_resumes_with_Range`.
- [EDGE-hdri-backdrop-49] `SceneGridViewModelTests.SelectedBundledDeleted_ShowsNotDownloaded_ClickPostsRequestThenShow`.
- [EDGE-hdri-backdrop-50] `PublishBackdropsScriptTests.Guards_present`. The script text must contain:
  - `'^backdrops-v\d+\z'`
  - `git rev-parse` with `^{commit}` compared to `git rev-parse HEAD`
  - `git diff --quiet HEAD -- Milieu/Assets/BackdropPins.cs`
  - `git ls-remote --exit-code --tags origin`
  - `gh release view`, `exit 1`
  - `-- verify`, `--tag`
  - `gh release create`, `--verify-tag`, `--latest=false`, `*.atex`
  - no `*.jpeg`
- [EDGE-hdri-backdrop-50] `BackdropPinsTests.Verify_reports_missing_extra_size_sha_header_and_tag_problems` (an extra or missing stem is reported).
- [EDGE-hdri-backdrop-53] `DefaultBackdropsDownloadServiceTests.Duplicate_requests_fetch_once`.
- [EDGE-hdri-backdrop-61] `SceneGridViewModelTests.ClickNotDownloadedBundled_PostsRequest_AutoShowPostedAfterCompletion`; `BackdropControllerTests.Show_BundledNotDownloaded_IsNoOpWithMessage`.
- [EDGE-hdri-backdrop-62] `BackdropPreparerTests.Bundled_BadHeaderOrSha_MarksCorrupt_NoRegistration`, `Bundled_ShaVerifiedOncePerSession`.
- [EDGE-hdri-backdrop-63] `DefaultBackdropsDownloadServiceTests.CleanupStale_deletes_v1_jpegs_and_unpinned_atex_keeps_pinned_and_others` (incl. `moon.jpeg`).
- [EDGE-hdri-backdrop-64] `ConfigurationTests.Migrate_v1_bundled_jpeg_path_to_bundled_id` (theory over all 8 stems incl. moon) and `Migrate_v1_user_path_added_to_UserImages`.
- [EDGE-hdri-backdrop-65] `ConfigurationTests.Clamp_UnknownBundledId_ToNull`.
- [EDGE-hdri-backdrop-66] `SceneGridViewModelTests.SecondBundledClick_QueuesBoth_OnlyLastSelectedAutoShows`.
- [EDGE-hdri-backdrop-68] `Bc7Tests.Deterministic_SameInputSameBytes`, `TextureConversionTests.Deterministic_ConvertToAtexTwice`, and `BundledAtexTests.RegeneratedFromCommittedJpegs_MatchesPins` `[Trait("Speed","Slow")]` (gated on `MILIEU_VERIFY_ATEX=1`; 8 files).
- [EDGE-hdri-backdrop-69] `TextureConversionTests.PreparerAndToolPathProduceIdenticalAtex`.

**Part B** (the view model gets a recording `post`; tests run the posted actions against a fake controller and assert the recorded calls)
- [AC-hdri-backdrop-1, AC-hdri-backdrop-14] `SceneGridViewModelTests.ClickUserTile_PostsSelectSaveShow` and `ClickDownloadedBundledTile_PostsSelectSaveShow` (nothing happens until the posted action runs).
- [AC-hdri-backdrop-14] `SceneGridViewModelTests.TileOrder_NoneThenBundledThenUserImagesInAddedOrder`.
- [AC-hdri-backdrop-14] `SceneGridViewModelTests.SelectedTileHighlighted_AndStateOverlays`.
- [AC-hdri-backdrop-14] `SceneGridViewModelTests.AddMore_PostsAppendSelectShow`.
- [AC-hdri-backdrop-14] `SceneGridViewModelTests.Remove_PostsListRemovalOnly_NeverDeletesFile`.
- [AC-hdri-backdrop-14, EDGE-78] `SceneGridViewModelTests.SettingsCollapsedViaToggle_ExpandedViaOpenSettings`.
- [EDGE-93] `SceneGridViewModelTests.OpenSettingsExpandIsOneShot`.
- [AC-hdri-backdrop-14, EDGE-79] `SceneGridViewModelTests.NoShowApplyHideActionsExposed`: a reflection check, plus the `MainWindow` source has no "Show", "Apply", "Hide" or "Browse" button labels.
- [AC-hdri-backdrop-14] `ThumbStoreTests.KeyFromPathLengthMtime_SaveLoadRoundTrip_131072Bytes`; `BackdropPreparerTests.Conversion_WritesThumbFrom256x128Mip`.
- [AC-hdri-backdrop-14] `BundledThumbsTests.EveryPinnedStemHasEmbedded256x128Thumb` (8 resources incl. `Milieu.Resources.Thumbs.moon.jpg`).
- [AC-hdri-backdrop-7, EDGE-70] `SceneGridViewModelTests.NoneTile_PostsHideAndPersistsNull`; `PluginCommandTests.MilieuOff_PostsNoneAction`.
- [AC-hdri-backdrop-9] `SceneGridViewModelTests.ClickWhileBlocked_PostedActionSelectsAndSurfacesBlockerOnly`.
- [AC-hdri-backdrop-10] `ConfigurationTests.RoundTrip_AllFieldsSurvive` (incl. `UserImages`, `GroundProjection`).
- [EDGE-hdri-backdrop-71] `SceneGridViewModelTests.ClickSelected_PostsShowWhenHidden_NothingWhenShownOrPreparing`.
- [EDGE-hdri-backdrop-72] `SceneGridViewModelTests.AddMore_DuplicatePathSelectsExisting`.
- [EDGE-hdri-backdrop-73] `SceneGridViewModelTests.MissingUserFile_TileMissing_ClickPostsFileMissingNoShow`.
- [EDGE-hdri-backdrop-74] `SceneGridViewModelTests.RemoveSelected_PostsNoneAndHide`.
- [EDGE-hdri-backdrop-75] `SceneGridViewModelTests.DownloadCompletesAfterReselect_NoAutoShowPosted`.
- [EDGE-hdri-backdrop-76] `SceneGridViewModelTests.UserTileWithoutThumb_UsesPlaceholderLabel`.
- [EDGE-hdri-backdrop-77] `BackdropControllerTests.ConversionSettingChange_DebouncedReconvert`, using a fake clock:
  - three changes within 500 ms → one `Show()`
  - Hidden or bundled → no `Show()`
  - **an EV change while Preparing → the job's token is cancelled and a new job starts with the new EV after 500 ms; only the second result is registered**
  - the same while AwaitingRedirect
- [EDGE-hdri-backdrop-80] `ThumbStoreTests.Cleanup_OnlyOwnPatternNotInUserImages`.
- [EDGE-hdri-backdrop-92] `SceneGridViewModelTests.AllActionsGoThroughPost` (with a `post` that only records, no controller or config mutation happens until the recorded actions run). `DefaultBackdropsDownloadServiceTests.EntryCompleted_OnlyBumpsVersion` (subscribers get no controller access; the fake controller sees no calls from the worker thread). `SceneGridViewModelTests.PendingAutoShowIsPostedFromDraw`.

**Part C**
- [AC-hdri-backdrop-15] `ConfigurationTests.Defaults_SkyHeight400_RoomGroundProjectionOn_SkyOff`.
- [AC-hdri-backdrop-15, EDGE-83] `ConfigurationTests.Migrate_SkyHeight0To400_KeepsUserValue_SetsGroundProjectionDefaults`.
- [AC-hdri-backdrop-15, EDGE-81] `SphereMeshTests.GroundProjection_FlattensBelowFloor_KeepsUvsNormalsIndices`; `SphereMeshTests.NoProjection_IdenticalToV5`; the existing `Reference_VerticesAndIndicesMatch` (reference) keeps passing.
- [AC-hdri-backdrop-15] `DomeAvfxBuilderTests.GroundProjection_ChangesOnlyVertexPositions`.
- [AC-hdri-backdrop-15] `DomeAvfxBuilderTests.PosAndRotYAreZero_SeamForSyncedFollowUp`.
- [AC-hdri-backdrop-15] `BackdropControllerTests.RoomDefaults_BuildWithFloorY_HeightOverRadius` (0.16; spawn at feet + 1.6).
- [EDGE-hdri-backdrop-82] `BackdropControllerTests.FloorYNull_WhenHeightNotBelowRadiusOrNonPositive`.
- [AC-hdri-backdrop-3, EDGE-84] `BackdropViewModelTests.ApplyOf_SingleSwapRouting`: Height → Rebuild with projection on and Placement with it off; Yaw → Placement; Radius, VerticalScale, repeats, flip, brightness, Mode and GroundProjection → Rebuild.
- [AC-hdri-backdrop-3, EDGE-84] `BackdropControllerTests.ApplyPlacement_SpawnsNewBeforeRemovingOld_SameAvfx` and `HeightReleaseWithProjection_OneRebuildNoPlacement` (exactly one spawn and one removal in the fake spawner; a new avfx path).
- [AC-hdri-backdrop-3] `BackdropControllerTests.Recenter_RespawnsAtPlayer_NewFirst`.
- [EDGE-hdri-backdrop-94] `BackdropControllerTests.ModeSwitch_And_ProjectionToggle_OneRebuildOnly` (one spawn and one removal; no extra placement respawn).

## Environment & Preconditions
- The Dalamud dev SDK at `%AppData%\XIVLauncher\addon\Hooks\dev`.
- **Housekeeping first:** `git diff --ignore-cr-at-eol -- tools/PrepareBackdrops/Program.cs` must be empty before rewriting the file (normalise the CRLF-only change).
- **Generate assets (agent, this run, from HEAD ≥ 801371b):**
  - `dotnet run --project tools/PrepareBackdrops -c Release -- atex Milieu/DefaultBackdrops artifacts/backdrops` (8 files, several minutes), then paste the pins.
  - `... -- verify artifacts/backdrops --tag backdrops-v2`
  - `... -- thumbs Milieu/DefaultBackdrops Milieu/Resources/Thumbs`, then commit the 8 thumbnails.
- **Pre-review targeted run (required before code-critic):**
  - Env: `MILIEU_REFERENCE_AVFX=C:\Users\solon\AppData\Local\Temp\claude\831a6479dbf102b5\6550b6d9-a80f-4212-9ead-9e7e100d8823\scratchpad\pickles\vfx\ve0002.avfx`, `MILIEU_REFERENCE_ATEX=C:\Users\solon\AppData\Local\Temp\claude\831a6479dbf102b5\6550b6d9-a80f-4212-9ead-9e7e100d8823\scratchpad\pickles\vfx\Sky1.atex`, `MILIEU_HDRI_SOURCE_DIR=E:\hdri`, `MILIEU_BACKDROP_ATEX_DIR=E:\repos\Milieu\artifacts\backdrops`, `MILIEU_VERIFY_ATEX=1`.
  - Run `dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release --filter "FullyQualifiedName~Reference|FullyQualifiedName~WhenSourcePresent|FullyQualifiedName~BundledBackdropsTests|FullyQualifiedName~BundledAtexTests|FullyQualifiedName~BundledThumbsTests|FullyQualifiedName~CancelInsideMip0"`.
  - Report pass and skip counts.
- **Optional staging for in-game tests before publishing:** copy `artifacts/backdrops/*.atex` into `%AppData%\XIVLauncher\pluginConfigs\Milieu\DefaultBackdrops\`.
- **Release output:** the dev-load path with BuildNumber bumped (> 7); no `.jpeg` or `.atex` in the output (thumbnails are embedded).
- **Known issue (recorded):** the tag-triggered `upload-backdrops.yml` never ran for `backdrops-v1`; GitHub registered no workflow, and **the cause is unknown**. v1 was released by hand and lacks moon. v7 retires the workflow.

### Left to the user (hard stops in auto mode; the final report must state these)
1. Merge and push the commit that pins `backdrops-v2`.
2. `git tag backdrops-v2 <that commit> && git push origin backdrops-v2`. The script requires the tag to point at the checked-out `HEAD`.
3. `pwsh tools/publish-backdrops.ps1 -Tag backdrops-v2`, from that checkout with `artifacts/backdrops` (all 8 `.atex`).

Until then, bundled tiles show "Not published yet", except for files staged by hand.

### In-game checks (UNVERIFIED; only the user can do these)
Checks 1, 2 and 4–15 as in v5. Check 3 is replaced; 12 and 16–20 are as below:
- **3. (v7)** Releasing the Yaw or Height slider, or "Re-centre on me", replaces the dome at the new placement with no flicker (new dome first). Dragging changes nothing until release. Switching Sky/Room or toggling ground projection does a single swap.
- **12.** Nothing downloads until a scene tile is clicked; the clicked tile shows a percentage, then the dome appears. "Download all" fetches the rest one at a time; interrupting resumes.
- **16.** A downloaded or staged bundled scene shows within about 2 s with no "Converting" step.
- **17.** A v5 config opens with the same scene selected (as a tile), Sky height raised to 400, and old `.jpeg` files gone.
- **18.** The grid shows previews for all 8 bundled scenes (incl. Moon) before any download. Clicking applies immediately. None hides. "Add more…" adds, selects and shows. "×" removes the tile and leaves the file. Settings is collapsed via `/milieu` and expanded via the plugin's settings button.
- **19.** A user image's tile gets its thumbnail after its first conversion. Changing EV during a conversion restarts it once with the new value.
- **20.** Sky: the panorama no longer cuts into the ground at the default height of 400. Room with ground projection: the image's floor lies on the ground at your feet, the horizon sits at camera height, and there's no visible crease at normal distances.

## NEEDS_DECISION
None. All items were decided in auto mode (Approval Summary). Publishing `backdrops-v2` is the user's step.

## Risks
All v5 risks still apply. **(v7)**:
- **(A)** Cross-machine determinism is unproven, so CI regeneration was rejected; the release assets are the store of record. The publish script now pins the tag to `HEAD` and requires a clean `BackdropPins.cs`, so the pinned table and the uploaded files can't drift. The full set is 341.3 MiB, mitigated by per-scene downloads. Textures are verified (SHA, header) before the game loads them.
- **(B)** ImGui layout is UNVERIFIED (check 18). Thumbnail wraps are owned by `ThumbnailCache`. Every action is posted to the framework thread (EDGE-92), so draw-thread or worker-thread races can't reach game state. A mis-click can start a 45 MB download; acceptable.
- **(C)** The ground-projection crease is small with 48 rings (UNVERIFIED, check 20). Each placement change briefly holds two domes (new before old); 230 KB VFX, no texture reload.
- **Migration** (sensitive area): ordered steps (`ImagePath`, `UserImages`, Sky height, projection defaults), each tested; user values are never overwritten; ready for a v3 step from the synced follow-up.
- **Seams for the synced follow-up:** `PreparedTexture.DiskPath`; private `SpawnDome`/`RemoveDome`/`TeardownCore` behind `IVfxSpawner`/`IPenumbraRedirects`; ordered `Migrate`; Pos Y / Rot Y curves present at 0 (seam test). The follow-up adds baking parameters and a delivery interface without reshaping this code.
- **SRP watch:** `BackdropController` (placement, rebuild, debounce, bundled status) is a candidate to extract `ReconvertDebouncer`. Split the download service into `BackdropDownloadQueue` / `BundledBackdropStore` if it grows. `SceneGridViewModel` stays pure.
- **Size:** 15 ACs, above the ~10 guideline. A–C share files (UI, migration, builder), so they stay together.

**Paths:**
- Repo: `E:\repos\Milieu` (HEAD 801371b); approved v5 plan: `E:\repos\Milieu\.workflow-log\plan-approved.md`.
- Code this delta changes:
  - `E:\repos\Milieu\Milieu\Backdrop\BackdropController.cs` (ApplyLive → ApplyPlacement, about line 164)
  - `E:\repos\Milieu\Milieu\Gui\MainWindow.cs` (mode switch posts both actions today)
  - `E:\repos\Milieu\Milieu\Assets\DefaultBackdropsDownloadService.cs`
  - `E:\repos\Milieu\Milieu\Configuration.cs`
  - `E:\repos\Milieu\tools\PrepareBackdrops\Program.cs`
  - `E:\repos\Milieu\AGENTS.md` (lines 59 and 72)
  - `E:\repos\Milieu\.github\workflows\upload-backdrops.yml` (to delete)
- Generated output (gitignored): `E:\repos\Milieu\artifacts\backdrops`
- Part D research for the follow-up:
  - `E:\repos\PSync\PlayerSync\FileCache\FileCacheManager.cs` (lines 58–65)
  - `E:\repos\PSync\PlayerSync\FileCache\TransientResourceManager.cs` (line 20)
  - `E:\repos\PSync\PlayerSync\PlayerData\Pairs\VisibleUserDataDistributor.cs` (lines 142–170)
  - `E:\repos\Penumbra\Penumbra\Api\Api\MetaApi.cs` (lines 230–260, 507–512)
  - the reference cushion texture `C:\Users\solon\AppData\Local\Temp\claude\831a6479dbf102b5\6550b6d9-a80f-4212-9ead-9e7e100d8823\scratchpad\pickles\default group\chara\monster\m8058\obj\body\b0001\texture\v01_m8058b0001_n.tex`

## Amendment v7.1 (plan-critic v7 check, applied by the planner)
- Decision 8 single-swap rule: Height is decided by the controller (`ApplyHeight()`): it stores `shownFloorY`; on a Height release it computes `newFloorY` (null when projection off, HeightOffset ≤ 0, or ≥ Radius·VerticalScale); if `newFloorY != shownFloorY` → `ApplyRebuild`, else `ApplyPlacement`. Never both.
- C2 routing: `BackdropViewModel.ApplyOf(setting)` → `Placement | Rebuild | Height`; Yaw → Placement; Radius, VerticalScale, repeats, flip, brightness, Mode, GroundProjection → Rebuild; Height → Height (MainWindow posts `controller.ApplyHeight()`).
- EDGE-84: projection on + FloorY changed → one ApplyRebuild; projection on + FloorY unchanged → one ApplyPlacement; projection off → one ApplyPlacement; dragging changes nothing.
- Tests: `ApplyOf_SingleSwapRouting` (Height → Height); `HeightRelease_ProjectionOn_FloorChanged_OneRebuildNoPlacement` (Room r10, 1.6 → 2.0); NEW `HeightRelease_ProjectionOn_FloorUnchanged_DoesPlacement` (Room r10, 12 → 15, same avfx path, no Set); `HeightRelease_ProjectionOff_DoesPlacement`.
