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
- **Static VFX (local delivery) never run effectors**, not even the Pickles Venue's own file. *(Superseded 2026-11-17 by
  the amendment at the end: with this ADR's recipe and the sun off, a static local dome's key lights the character.)*
- An effector links from an emitter (`Emit.EfNo`), never from a timeline item; one effector per emitter.

Tuned in game with the owner after the merge (dev builds 32-41, 2026-11-16, Mad Bird Ring, sky dome):

- With bMvS 0 the key shadowed furniture (map) but never the character; **bMvS 1 adds character shadows**, so the
  shadowed key now sets all four shadow flags.
- **Characters shadow only within a few units of the light.** Range (DstS 8-60), shadow range (SCDN 0.1-15, SCDF
  15-70), falloff (Attn 1 or 2), brightness and a 15° elevation cap all left that unchanged; map shadows reach the whole
  scene. So a key at the dome wall lights the scene and shadows furniture but not characters. The owner chose that over a
  light that follows the character (a light near the player, untested). The Pickles Venue's character-shadowing pool
  lights are small lamps (DstS 1, Attn 1, bAOV 1, SCDF 20), consistent with this.
- **Brightness:** at 30 units with Attn 1, Bri 4000 was far too bright and Bri 200 looked right (`KeyBrightness`).
  The shadowed key uses Attn 1, the falloff that brightness was judged with; fills keep Attn 2.
- The game's own sun still lights and shadows the scene through the dome: a shadow seen at distance came from it.
- **Room dome:** with the key 8.4 units out, Bri 200 was too bright; after trying strengths the owner asked for about 40%
  (Bri 80). `DistanceCompensationExponent` 0.72 gives (8.4 / 30)^0.72 ≈ 0.40 (≈ 0.42 at the default radius's 9 units);
  the sky dome (key at the nominal 30) is unchanged.

## Decision

Under a synced delivery with scene lighting on, the synced dome carries Milieu's light rig as VFX point lights.

- **Planner.** `VfxLightPlanner` (pure) takes the rig (HDR or the LDR estimate, ADR 0009/0010) and, for keyed rigs only,
  the same selection as the native planner (`LightRigPlanner.Select`): the key if drawn, then fills (lamps before
  regions); a rig with no key gets a stand-in (below).
  - **Key:** a shadow-casting point light at `(0, LightRigLayout.AnchorHeight, 0) + keyDir × d`, Bri =
    `VfxLightLayout.KeyBrightness × strength` (meant to match the sun at strength 1), range `KeyRange`, shadows
    `KeyShadowNear`/`KeyShadowFar`.
  - **Fills:** soft coloured point lights without shadows, the strongest at `FillBrightness × strength`, the others by
    their share of luminance × type power.
  - **Distance:** `clamp(min(nominal, 0.9 × radius), 1.5, nominal)` (nominal 30 for the key, 8 for fills), so room-mode
    lights stay inside the dome. Brightness against distance is a constant (`DistanceCompensationExponent`, 0 = none; 0.72 since the room-dome tuning).
  - Colour is the energy's hue with the largest channel at 1.
- **Positions are emitter-relative** and independent of the player's world height: the emitters carry that
  (`DomeAvfxParams.EmitterBaseHeight`).
- **Builder.** Light 0 links from the dome emitter (the link proven in game); lights 1..n-1 from particle-less emitters of
  their own (PrCn 0, no ItPr), each with its own timeline item (item chunk i holds items 0..i). At most 4 lights; lights
  are rejected for a local build; every value is validated; the self-check covers the counts and links. With no lights
  the output is byte-identical to before (golden matrix).
- **Fills off by default.** A particle-less emitter carrying a *point* light hasn't been loaded in game, so
  `VfxLightLayout.FillsOnOwnEmitters = false`: only the key ships until that is checked.
- **No sun in the image → a stand-in key** (owner's test, 2026-11-16: the bundled Sunset, whose estimate has no key,
  shipped no light at all). When the rig has no key, the first drawn region (regions are radiance-ordered by both
  estimators; or the first drawn lamp, for a hand-edited rig with lamps but no key) is the key, with shadows, at the key's
  brightness formula (including the distance compensation) × `StandInKeyScale` (1; item 11). A sun that exists but isn't
  drawn on the dome still gives no key (no light from a direction the image doesn't show there). The native planner is
  unchanged, so the two differ for no-key rigs; under synced delivery in GPose a no-key image trades its native fills for
  the one stand-in. Night and space HDRs, whose stars ADR 0009's key test deliberately keeps from becoming the key, now get a sun-strength stand-in from
  their brightest drawn region under synced delivery: accepted for now (strength, synced only), with a known-issue
  follow-up for a scale-free contrast gate. Once fills ship, the stand-in takes one fill slot (item 1).
- **The rig travels with the synced texture.** The synced job loads it after staging (`PreparedTexture.Rig`); a failure
  logs and shows the dome without lights, never failing the backdrop.
- **Native lights step aside only when the drawn synced dome carries VFX lights** (`LitBackdrop.Synced &&
  VfxLightCount > 0`, status `InSyncedBackdrop`): no doubled light. A local dome under a synced config, or a synced dome
  with no VFX lights (no rig; a key that isn't drawn, or one with no positive channel, with fills off; or nothing of the
  rig drawn on the dome), keeps native GPose lights.
- **Live changes.** The lighting toggle, strength and fill count go through `LightingViewModel.LightChange` (set, save,
  request). The controller replans 500 ms after the last change and rebuilds only if the lights differ. While in GPose
  the request is held and fires 500 ms after GPose ends, because a rebuild redraws everyone and can disturb posing;
  other synced rebuilds already plan with the current light settings.
- **Kill switch.** `Plugin.SyncedLightsAvailable()` (and `LightingAvailable()`): off means no rig loader, no effectors,
  and native lights under synced delivery as before.

Rejected: directional effectors (never lit); VFX lights for local delivery (static VFX don't run them; superseded by the
amendment below); reusing the
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

1. A particle-less emitter carries a point light (then turn `FillsOnOwnEmitters` on). When fills turn on, revisit the
   stand-in's fill slot (EDGE-synced-lighting-54: fill count N gives the stand-in + N − 1 fills).
2. Key brightness at strength 1 (seen in the sky dome at 30 units: Bri 200 looks right); in room mode, and fill range
   (`FillRange`) reaching the player.
3. Key map shadows at distance 9 in room mode (seen at 30 in the sky dome). Character shadows: only near the light (above).
4. The light direction when the character turns: ICbR 0 sits in the dome emitter's ItPr and only stops the *particle*
   inheriting the emitter's rotation. The effectors hang off the emitters themselves, which follow the VFX root including
   its rotation, so the lights may swing with the carrier while the dome doesn't (follow-up if seen).
5. The key indoors under a real ceiling (map shadows may block it).
6. Poses survive a `RedrawAll` in GPose (non-light synced rebuilds still run there).
7. Brightness against distance in room mode (`DistanceCompensationExponent` 0.72): set from one room radius (key at 8.4);
   other radii (1-33, e.g. about 0.18× at radius 3, 0.93× at 30) and fill compensation are UNVERIFIED.
8. A PSync pair without Milieu sees the key light and its shadow.
9. A key 30 units away isn't culled by the game (effector or emitter culling at distance).
10. A light emitter's lifetime: `LightEmitter` copies the dome emitter's Life (60) and creation curves, not those of the
    game's particle-less emitter it was modelled on; compare in VFXEditor before turning fills on (item 1).
11. The stand-in key on an image with no sun (Mad Bird Ring, synced, Sunset, strength 1): a shadowed light on the
    character; direction plausible (Sunset's first region, s 0.776, t 0.193, about 55° elevation); full key brightness
    doesn't read as midday sun on a sunset (else tune `StandInKeyScale`). If the *direction* is wrong, the follow-up is
    ADR 0010's hand-tuned Sunset rig override (it fixes native and synced), not `StandInKeyScale`.

## Amendment (2026-11-17): local domes outside GPose

**Context.** With sun off (ADR 0013) under "Only me", the scene outside GPose kept only ambient light: native lights are
GPose-only (ADR 0009) and static VFX were recorded as never running effectors. That finding came from the 2026-11-09
experiment (testing builds 3-5): venue-style point lights (DstS 1-10, intensity 1-3, bAOV 1), seen with the game's sun on.
The owner chose "Dome lights outside GPose". A spike (branch `spike/local-dome-lights`, fd9d993, build 54) baked this ADR's
proven key recipe (Attn 1, all four shadow flags, Bri 200, bAOV 0) into the static local dome: the owner reported "the
lights work now" (2026-11-17). The formal evidence is the positive control on build 55: with the sun off, outside GPose,
unticking scene lighting darkens the character and re-ticking it relights it about 500 ms later (pending until run).

**Decision.**
- **D1, vehicle:** the same effectors, baked into the local static dome (light 0 links from the dome emitter; `LocalAttach`
  stays). Rejected: a lights-only actor VFX bound to the player (new signatures and hooks, likely PSync attribution); the
  whole dome as an actor VFX (the user's image would be uploaded); native lights outside GPose (ADR 0009's safety choice).
- **D2, outside GPose only.** In GPose native lights cast better character shadows; both together would double the light.
- **D3, the switch** is a local rebuild and respawn (new dome first, then the old one removed), 500 ms after the last GPose
  edge, with no hold: a local swap redraws nobody.
- **D4, local frame.** The static root carries yaw and height, so lights are planned at yaw 0 and shifted down by the
  height offset (`VfxLightPlanner.PlanLocal`), from the radius and height the dome was built with. Yaw changes respawn
  the same file; a height change on a lit dome rebuilds.
- **D5, kill switch** `Plugin.LocalLightsAvailable()` (with `LightingAvailable()`); off means no local rig loader, no
  effectors in the local file and today's panel texts.
- **D6, independent of sun off**, as synced: with the sun on there are two light sources.
- **D7, a light problem never costs the backdrop:** a rig load failure shows the dome without lights; `PlanLocal` returns
  no lights when any position would exceed the builder's bound (so the compared plan always equals the built file), with a
  build backstop; a lit dome the game ends early latches local lights off (Warning) until scene lighting is ticked off and
  on (ADR 0009's convention), and the dome is rebuilt without them.
- **D8, no doubling:** native lights step aside whenever the drawn dome, synced or local, carries VFX lights
  (`LightingStatus.InLocalBackdrop`); native lights start only after the unlit dome is in and the usual settle.
- **D9, no sentinel:** no native light call is made.
- **D10, bounded retries:** a failed or timed-out local swap records the GPose state it was for; the level check doesn't
  retry that state until the next GPose edge or a successful commit (one attempt per edge).
- **D11, the rig always loads** with the local texture while the loader is present (as synced), whatever the toggle, so
  ticking lighting later needs no re-show. Usually a rig-store hit; a full decode only when the rig was trimmed or never
  stored, which then delays that show.

**Release gate.** Flip `LocalLightsAvailable()` to false before tagging if the positive control fails or local dome
lights regress.

**Tech debt.** `BackdropController` grows by the local light state (pending and shown plans, the GPose flags, the latch and
the edge and level-check bookkeeping), mirroring the synced light state beside it; extracting a small `LocalLightState` is
a behaviour-free follow-up. The build backstop (a lit build that fails is rebuilt unlit) is unreachable with valid
settings and kept as defence in depth, untested.

UNVERIFIED in game (local domes):

12. Brightness parity with a synced dome at the same strength (the owner judges; no tuning).
13. The light direction at dome yaw 90°: the VFX key's shadow outside GPose matches the native key's in GPose (does the
    effector follow the static root's rotation?). Yaw 0 was seen working in the spike.
14. The GPose enter/exit swap, also with GPose motion paused: no flicker, poses undisturbed, native lights after the
    settle, no doubled light.
15. The game doesn't end a lit static dome early; no crash across show/hide, zone change, GPose, plugin reload, game exit.
16. Room-dome brightness, and the light staying inside the dome (items 2 and 7).
17. Walking away from the dome centre: the light stays with the dome (Recenter restores both).
18. With sun off unticked, the dome key plus the game's sun read acceptably.
