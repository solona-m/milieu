# ADR 0003: The dome VFX is authored in code, modelled on the reference mod

## Status

Accepted. The UV-mapping and lifetime questions below are open until the in-game checks are done.

## Context

The "Pickles Venue" mod shows a 360° panorama by drawing a model particle: a unit lat-long sphere (97x49 vertices,
normals inward) textured with an 8192x4096 BC7 equirectangular image. Its UV set 0 scales U by 2 and V by 1 (the
"UV doubling"); the particle is scaled to about 20000 x 10000 and raised 400 units. Milieu needs the same rendering
with any image and any dome size.

Embedding the reference's VFX elements would redistribute a third party's work; VFXEditor's blank template is only
a scheduler; patching a vanilla game VFX at runtime is unproven.

## Decision

`DomeAvfxBuilder` writes a minimal AVFX in code: one scheduler item → one timeline → one emitter → one model
particle → one texture and one model. Chunk tags and order follow VFXEditor's reader (AGPL-3.0; no code copied) and
every value mirrors the reference element chain (`Schd`, `TmLn#2`, `Emit#5`, `Ptcl#11`, `Modl#7`), except:

- Root clip-box culling off (`bCBC 0`, `bCul 0`): the reference's 25-unit box would cull a dome thousands of units
  across.
- Particle scale X/Z = radius, Y = radius × vertical scale; position and rotation are applied at runtime instead.
- Brightness defaults to 1.0 (reference 0.798).
- UV set 0 scale X = ±horizontal repeat (default 2.0, negative when flipped), Y = vertical repeat (default 1.0).
- Unused texture slots point nowhere (`TxNo -1`); the colour slot uses `TLst` (a byte list) as the reference does.
- Ground projection (on by default for both domes): vertices below the floor plane are pulled along their own ray onto
  it (`SphereMesh.Build(floorY)`), so the default mesh positions differ from model 7; UVs, normals, tangents, indices
  and winding stay the reference's. With projection off the mesh is the reference layout exactly.

The sphere is generated to the reference layout exactly (tested against model 7: positions and UVs to half precision, normal and tangent bytes within 1), so the seam,
U direction, poles and winding are the proven ones. Every generated file is parsed back before it is written.
Parity tests compare the structure with the reference when `MILIEU_REFERENCE_AVFX` is set.

BC7 encoding uses BCnEncoder.Net per mip; its `EncodeToRawBytesAsync` observes the cancellation token inside a mip
(`Bc7Tests.CancelInsideMip0_ReturnsPromptly`), so no strip-encoding fallback was needed.

Experiment (testing builds, 2026-11-09; remove or decide before the next stable merge): `/milieu vfxlight 1-5`
(`DomeAvfxBuilder.TestLightMode`, not saved) adds one VFX effector light, to test in game whether VFX lights can carry
scene lighting to synced viewers (ADR 0009 lights are local-only native objects). Modes 1-2: a point light linked from the
dome emitter (`Emit` `EfNo` 0), set up like the "Pickles Venue" lights (mode 2 with 10x range and 3x brightness), shadows
with near 0.0001 / far 20. Modes 3-4: a directional light on its own particle-less emitter with its own timeline item, set up
like the game's world-lighting directional lights (found by scanning the game's VFX: 19 of 506 directional effectors have
bAGm 1, and 18 of those 19 use bAOV 0, RGB + Bri and their own particle-less emitter, most (15) with black ambient); mode 3 copies the most
common one, mode 4 is red, brighter and tilted. Mode 5 is mode 4's light linked from the dome emitter (the 19th game light
is linked from a particle emitter), to tell whether the separate emitter matters.
Mode 0, the default, gives byte-identical output.

## Open questions (to record after in-game checks)

- **UV mapping:** if the game maps model UV −0.5..0.5 to the whole texture, U×2 shows the panorama twice and the
  right horizontal repeat is 1.0; if it maps UV as −1..1, U×2 is one wrap and the vertical repeat should be 2.0.
  Compare top/bottom coverage at U2/V1 against U2/V2 and record the answer here.
- **Lifetime:** the emitter lives 60 frames and the particle forever, as in the reference. If the game removes the
  dome on its own, Milieu's guard stops re-showing it after three early removals (`GameEndedVfx`).

Synced delivery (ADR 0006) is the one exception to runtime placement: there the game places the item's VFX, so height
and yaw are baked into the particle's Pos/Rot Y curves and the emitter uses `DomeAvfxBuilder.SyncedAttach` ItPr flags;
local delivery stays runtime-placed with the reference's flags, byte-identical.

## Consequences

Easier: any image, any size, no third-party VFX shipped, and the structure is tested.
Harder: a wrong chunk crashes the client without a managed exception, so the builder's tests and self-check are the
safety net, and a game patch that changes the AVFX format needs this file revisited.
