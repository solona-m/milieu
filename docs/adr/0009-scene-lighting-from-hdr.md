# ADR 0009: Scene lighting from HDR backdrops

## Status

Accepted. The projection constants and the light powers are UNVERIFIED until the in-game checks are done.

## Context

HDR panoramas (`.hdr`, and `.exr` since ADR 0008) store real radiance, and users want that light on their character.
FFXIV has no image-based lighting, but it can spawn spot and point lights, as Brio does in GPose. Spawning game lights
is native interop: a wrong layout, a call on a freed light or a call during game exit can crash the client. Lights are
also local: PSync pairs never see them.

## Decision

- **An approximation, on by default.** "Light the scene from the image" (`Configuration.SceneLighting`, on by default
  since the owner's 2026-11-09 change, also for configs saved without the field; the user can turn it off) adds
  a spot **key** light from the image's sun or brightest region (character shadows on) and up to three coloured point
  **fill** lights from a median cut of the rest, all at one distance (`LightRigLayout.LightDistance`) from the real
  local player (object 0, not a GPose actor copy moved by Brio or Ktisis). Strength and fill count are live settings.
  Only HDR, non-bundled images carry the light; LDR and bundled scenes show a note and get no lights.
- **Extraction is pure** (`Milieu/Lighting/`): a 256x128 radiance probe, the key region (within 6°, ≥ 10% of the peak;
  a key only if ≥ 5% of the energy and ≥ 10x the median of the *covered* texels, so band panoramas work), then a
  depth-2 median cut. The rig is stored as JSON in `cache/lights/`, keyed on the file and `LightExtractor.Version`;
  every HDR conversion stores one, and a texture cache hit decodes once more only if no rig exists.
- **The dome as drawn.** Directions come from `BackdropController.Lit`: the image of the texture actually shown (bound
  when the texture is assigned, never a pending or failed request) and the drawn VFX's yaw, flip, vertical scale and
  repeats, captured where the VFX is built and spawned. `DomeProjection` inverts `SphereMesh`'s UV layout. Two constants
  carry the unverified part: `DomeProjection.GameUvScale` = (0.5, 1.0), on the hypothesis that model UV is measured in
  texture-height units (so U×2 is one wrap and V×1 the full height, matching the shipped repeat defaults); and
  `LightRigLayout.SpotForward` = +Z, from the FFXIVClientStructs docs. A texel with no drawn copy (repeat < 1, or a
  vertical copy outside t ∈ [0,1]) gives no light. **After the in-game check, update ADR 0003's open UV question.**
- **Native calls: FFXIVClientStructs through the scanned pointer only.** `LightInterop` scans
  `Scene.Light.Addresses.Create` and calls only that pointer (never the generated static), with a null pool name;
  virtual functions go through the instance's own vtable (the generated members). Layouts are ClientStructs' as bundled
  with Dalamud (no local mirrors, so no frozen offsets). No hooks: each light's vtable is recorded at create and compared
  before every later call (a heuristic for a light the game freed). The lifecycle is adapted from Brio (GPL-3.0,
  credited in THIRD_PARTY_NOTICES): render transform bound to the object's transform, per-frame
  UpdateRender/UpdateCulling/UpdateMaterials, CleanupRender then Dtor.
- **GPose only, after a settle delay.** Lights exist only while the option is on, the spawner is available, nothing is
  latched, an HDR image is drawn, the game is ready, in GPose, with a local player, and the framework is not unloading;
  and only after 30 consecutive ticks of that. **The counter resets to 0 on any tick where it doesn't hold** (the one
  reset rule), and the lights are removed in that same tick. `SceneLighting` ticks after the controller, and the
  controller's `Hidden` event removes them within the Hide/None/`/milieu off` call.
- **Failures latch** until the user turns the option off and on (an image change also clears a read failure):
  the light function missing (the panel says so, controls disabled), a light not created (any already made are
  destroyed), a light found dead, the rig unreadable. No retry loop. `Plugin.LightingAvailable` is the kill switch:
  off, nothing lighting-related runs (no SceneLighting, no light function resolved, no rig extracted, stored or trimmed)
  and the section is hidden. A failure extracting a rig during a conversion is logged and never fails the backdrop.
- **Crash sentinel** (`<config>/lighting-attempt.flag`): set before each create batch; cleared after 600 ticks with
  lights alive, on the off→on toggle, and in RemoveAll/Teardown **only once every light was destroyed** (a refused
  destroy keeps the flag until a later RemoveAll destroys the rest), and when a plan leaves no light alive. The flag is
  cleared only by the session that set it: a CrashGuard flag found at startup survives until the user's off→on, even
  across a clean exit (RemoveAll, Teardown and the unloading path don't clear a flag this session didn't set). Found at startup, lighting starts latched
  (CrashGuard) and says why. Its scope is creation and the first 600 ticks only: a per-frame crash later is not caught,
  by design. Every game client on the machine shares the config directory, so one client's attempt in progress can make
  another show a false CrashGuard; off→on recovers.
- **Game exit.** While `IFramework.IsFrameworkUnloading`, no light call is made at all: Tick does nothing and
  RemoveAll/Teardown drop the handles (leaked on purpose, as Brio does) and clear the sentinel (an exit is not a crash).
- **Per-frame refresh and hidden lights.** Every live light gets UpdateRender/UpdateCulling/UpdateMaterials each tick,
  except one whose DrawObject visibility flags are 0 (hidden, e.g. through Brio after it adopted the light): it stays
  live but isn't refreshed, as Brio does. That skip is UNVERIFIED in game (the interop has no unit-test seam).
- **Plugin unload.** Teardown runs on the framework thread through `RunOnFrameworkThread` with the same bounded wait as
  the controller's (the existing `ShutdownSequence` pattern); after it, SceneLighting's Tick and RemoveAll are
  inert. The rig is cached in memory for the last image path only (re-showing an earlier image reads it from disk).
- **Synced delivery.** Lights are local in both modes and the panel says pairs don't see them. Two assumptions: the
  item VFX's world yaw is the baked yaw (ICbR 0; ADR 0006's PICd 2 fallback would need the character's yaw), and a
  committed synced registration counts as drawn even if the item isn't (the lights then stay).
- **Brio coexistence.** Brio lists Milieu's lights as world lights and its DestroyAll doesn't free them; edits made in
  Brio last until Milieu's next planned change.
- **Deferred:** an ambient WorldLight (untested `Create(WorldLight)`, exterior only) and the environment-lighting
  override (needs a per-frame hook on a Brio-only signature and would fight Brio). No calibration slider.

## Consequences

Easier: an HDR backdrop lights the character in GPose with no setup (one checkbox turns it off); the maths is unit-tested on synthetic
panoramas and the lifecycle on fakes, and the native surface is one small class behind `ILightSpawner`.
Harder: the light powers, distance and ranges are starting values to tune in game; `GameUvScale` and `SpotForward` can
mirror or misplace the lights until checked (no crash); a ClientStructs lag after a patch disables the feature (the
panel says so; backdrops are unaffected); and there is no protection against a game-side free beyond the vtable check,
GPose-only, the settle delay, same-tick teardown and the sentinel.
