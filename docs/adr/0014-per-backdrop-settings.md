# ADR 0014: Settings remembered per backdrop

## Status

Accepted (2026-11-17). The switch feel, persistence across a restart, the upgrade of a real v4 config and the
hide-on-undownloaded flow are UNVERIFIED in game.

## Context

Every setting was global: Sky and Room each had one shared dome set, and the look, lighting, sun and night settings applied
to whatever was shown. A room scene and a sky scene need different settings, so switching between them meant re-tuning by
hand. The owner: "we need to remember settings per backdrop", for all four groups: dome size and placement, image look,
scene lighting, and sun and night.

## Decision

- **D1, a working copy plus a map.** The flat per-backdrop fields of `Configuration` (mode, both dome sets, repeats, flip,
  brightness, exposure, tone map, scene lighting, strength, fills, night, sun off) stay, as the selected backdrop's working
  copy: every reader is unchanged. `Backdrops` holds one `BackdropSettings` per backdrop. Rejected: reading every setting
  through a selected entry (touches every reader; an older build would lose all settings) and write-through setters
  (Newtonsoft sets properties in any order, and `DomeSettings` is mutated through its own setters).
- **D2, the key** is the existing `ImagePath` id: `bundled:<stem>` (stable across backdrop tags) or the user path exactly as
  in `UserImages`, compared case-insensitively. Rejected: content hashes (a full read of large HDRs) and normalised paths
  (would split one tile across keys).
- **D3, renames and removal.** A renamed file is a new backdrop; its old tile keeps its entry while the tile exists.
  Removing a tile (×) forgets its entry.
- **D4, global settings:** texture size and "Visible to" (a tile click must never start a PSync upload, ADR 0006), plus
  bookkeeping (`SyncUploadNoticeShown`, `UserImages`, `ImagePath`, `Version`).
- **D5, the stored set** is those fourteen fields; both dome sets are kept per backdrop (a Sky/Room flip keeps the other),
  with ground projection inside each.
- **D6, first use** copies the current working copy, so preferences carry over to a new image.
- **D7, migration v4 → v5.** The step only bumps the version. `Clamp` seeds every known backdrop (the selection, the user's
  images, the bundled scenes) from the clamped working copy whenever the store is empty, so each starts exactly as it
  looked before the update and seeds never carry invalid values.
- **D8, one switch.** A tile click runs select, save, then `Show()` once (one prepare, one registration and spawn, one
  redraw when synced). `Show()` and `Hide()` drop any reconvert still queued for the previous selection.
- **D9, edits land in the selected entry:** every save stores the working copy into it first (`Plugin.SaveConfig`, under
  the config's lock). This needs `ImagePath` to change only inside `Configuration` (a source-scan test pins it).
- **D10, a switch isn't a retry.** Scene lighting, night, sun off and the local-lights latch treat a flag's off→on change as
  the user retrying (clearing crash guards). A switch that ticks the flag bumps `SelectionGeneration`, and those detectors
  ignore an off→on across it.
- **D11, on load the working copy wins** over the selected entry (it is what the user last saw).
- **D12, bounds:** `BackdropSettings.Clamp` holds the global bounds once; entries are clamped, null or unknown-key entries
  dropped, and keys of no known backdrop pruned.
- **D13, hide when the selection can't show.** Clicking a scene not yet downloaded, or an image whose file is missing,
  hides the current backdrop (lights go, night and the sun are restored), so the drawn dome never carries another
  backdrop's settings. A downloaded scene then shows as before. Rejected: keeping the old dome with the new settings.

## Consequences

- An older build loads a v5 file with the last selected backdrop's settings. If it saves, per-backdrop settings are
  forgotten: back on this build, every backdrop starts from the last selected one's settings. Accepted (no sidecar file).
- Clicking an undownloaded or missing tile now hides the current backdrop, where it used to stay up. Under a PSync delivery
  an undownloaded click redraws pairs twice (the hide, then the show). An unpublished or failing scene leaves nothing shown
  until another tile is clicked.
- The README line about Sky/Room remembering their own settings is stale until the testing-6 release.
- `Configuration` grows by the selection and store bookkeeping; a separate store helper is a behaviour-free follow-up.
