# ADR 0011: Synced scene lighting through VFX lights

## Status

Accepted (2026-11-16). Amends ADR 0009's "Lights are local in both modes" for synced delivery. The light tuning (brightness,
range, distance) and several in-game behaviours are UNVERIFIED (see the list at the end).

## Context

Scene lighting (ADR 0009, 0010) creates native lights: local only, and only in GPose. With synced delivery (ADR 0006)
PSync pairs see the dome but not its light, and the game's sun competes with the backdrop. The owner asked whether the
lighting could sync. PSync sends files, not runtime objects, so the light has to live in a file pairs receive: the synced
dome's `.avfx`.

Tested in game with the owner (testing builds 3-5 and dev builds 23-28, 2026-11-09..16):

- **VFX point lights (effector EfVT 0) light the character and cast shadows** from a synced item VFX, once the dome sits
  at the player's height (ADR 0006 amendment: item VFX anchor at world height 0).
- The working recipe is the game's own: a scan of every AVFX in the game found 131 world-lighting point lights that cast
  shadows; the most common setup (header RoOT 2, CCOT 0, bAOV 0, bAGm 1; Data Col {RGB, Bri}, DstS, Pos, Attn 2 cubic,
  shadow flags bSdw/bChS/bMpS 1 and bMvS 0, SCDN 0.5 / SCDF 15) cast shadows in game, as did a 0.1/30 variant. At Bri 5
  in red the shadow was faint next to the sun.
- **Directional effectors (EfVT 1) never lit anything**, even the game's exact world-lighting recipe (19 of the game's
  506 directional effectors have bAGm 1) on its own particle-less emitter.
- **Static VFX (local delivery) never run effectors**, not even the Pickles Venue's own file.
- An effector links from an emitter (`Emit.EfNo`), never from a timeline item; one effector per emitter.

## Decision

Under a synced delivery with scene lighting on, the synced dome carries Milieu's light rig as VFX point lights.

- **Planner.** `VfxLightPlanner` (pure) takes the rig (HDR or the LDR estimate, ADR 0009/0010) and the same selection as
  the native planner (`LightRigPlanner.Select`): the key if drawn, then fills (lamps before regions).
  - **Key:** a shadow-casting point light at `(0, LightRigLayout.AnchorHeight, 0) + keyDir × d`, Bri =
    `VfxLightLayout.KeyBrightness × strength` (meant to match the sun at strength 1), range `KeyRange`, shadows
    `KeyShadowNear`/`KeyShadowFar`.
  - **Fills:** soft coloured point lights without shadows, the strongest at `FillBrightness × strength`, the others by
    their share of luminance × type power.
  - **Distance:** `clamp(min(nominal, 0.9 × radius), 1.5, nominal)` (nominal 30 for the key, 8 for fills), so room-mode
    lights stay inside the dome. Brightness against distance is a constant (`DistanceCompensationExponent`, 0 = none).
  - Colour is the energy's hue with the largest channel at 1.
- **Positions are emitter-relative** and independent of the player's world height: the emitters carry that
  (`DomeAvfxParams.EmitterBaseHeight`).
- **Builder.** Light 0 links from the dome emitter (the link proven in game); lights 1..n-1 from particle-less emitters of
  their own (PrCn 0, no ItPr), each with its own timeline item (item chunk i holds items 0..i). At most 4 lights; lights
  are rejected for a local build; every value is validated; the self-check covers the counts and links. With no lights
  the output is byte-identical to before (golden matrix).
- **Fills off by default.** A particle-less emitter carrying a *point* light hasn't been loaded in game, so
  `VfxLightLayout.FillsOnOwnEmitters = false`: only the key ships until that is checked.
- **The rig travels with the synced texture.** The synced job loads it after staging (`PreparedTexture.Rig`); a failure
  logs and shows the dome without lights, never failing the backdrop.
- **Native lights step aside only when the drawn synced dome carries VFX lights** (`LitBackdrop.Synced &&
  VfxLightCount > 0`, status `InSyncedBackdrop`): no doubled light. A local dome under a synced config, or a synced dome
  with no VFX lights (no rig, no key with fills off), keeps native GPose lights.
- **Live changes.** The lighting toggle, strength and fill count go through `LightingViewModel.LightChange` (set, save,
  request). The controller replans 500 ms after the last change and rebuilds only if the lights differ. While in GPose
  the request is held and fires 500 ms after GPose ends, because a rebuild redraws everyone and can disturb posing;
  other synced rebuilds already plan with the current light settings.
- **Kill switch.** `Plugin.SyncedLightsAvailable()` (and `LightingAvailable()`): off means no rig loader, no effectors,
  and native lights under synced delivery as before.

Rejected: directional effectors (never lit); VFX lights for local delivery (static VFX don't run them); reusing the
native light colour magnitudes (effector Bri/DstS semantics differ); a camera binder (unneeded once the height is fixed).

## Consequences

- **Pairs should see the light**, in and out of GPose, without Milieu (they load the same file; checked so far only on
  the user's own screen, UNVERIFIED item 8). The user sees the same lights; under synced delivery there are no native
  GPose lights on top.
- **Turning lighting off in GPose** shows "Off" at once, while the VFX lights stay in the synced file until GPose ends
  (the rebuild is held); accepted.
- Every light change rebuilds the synced dome, redraws everyone and makes pairs re-download it after their next sync.
- Up to 4 extra lights per synced user; two synced Milieu users near each other add up (ADR 0006's open question).
- The tuning constants live in `VfxLightLayout`; tuning them needs a build (the test command is gone, issue #1).
- `DomeLight`, `VfxLightPlan` and the light chain are internal to the plugin.

UNVERIFIED in game:

1. A particle-less emitter carries a point light (then turn `FillsOnOwnEmitters` on).
2. Key brightness matches the sun at strength 1; key and fill range (`KeyRange`, `FillRange`) reach the player.
3. Key shadows at distance 30 (and 9 in room mode) with near 0.5 / far 45.
4. The light direction when the character turns: ICbR 0 sits in the dome emitter's ItPr and only stops the *particle*
   inheriting the emitter's rotation. The effectors hang off the emitters themselves, which follow the VFX root including
   its rotation, so the lights may swing with the carrier while the dome doesn't (follow-up if seen).
5. The key indoors under a real ceiling (map shadows may block it).
6. Poses survive a `RedrawAll` in GPose (non-light synced rebuilds still run there).
7. Brightness against distance in room mode (`DistanceCompensationExponent`).
8. A PSync pair without Milieu sees the key light and its shadow.
9. A key 30 units away isn't culled by the game (effector or emitter culling at distance).
10. A light emitter's lifetime: `LightEmitter` copies the dome emitter's Life (60) and creation curves, not those of the
    game's particle-less emitter it was modelled on; compare in VFXEditor before turning fills on (item 1).
