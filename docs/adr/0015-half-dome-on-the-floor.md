# ADR 0015: Half dome on the floor

## Status

Accepted (2026-11-21). Everything seen in game is UNVERIFIED (see the end), and a release gate applies: no tag ships with
`Plugin.HalfDomeAvailable()` returning true until the worst-case check below has passed.

## Context

The owner: "miliu is projecting the images onto a sphere, which distorts the bottom. They should be projected onto a half
circle dome where the bottom is level with the floor (player's feet) or the horizon", with a screenshot of a room
panorama whose floor smeared into radial streaks converging below the player, where a tiny copy of the fireplace arch
appeared.

Ground projection (ADR 0003) was a sphere centred at the camera height with its lower vertices pulled onto a floor plane.
Two defects made the screenshot:

1. **One slider was both the dome centre and the capture height.** To bring the dome's bottom down to the floor the owner
   had lowered it (the lodge to 0.07; seven other backdrops to −0.15, which made the floor inactive and sank a full sphere
   with the image's horizon at the feet). With the capture point 0.07 above the floor the image's floor squashes into a
   disc about 0.2 units across (the tiny arch) and the band just below the horizon stretches across the whole room (the
   streaks).
2. **The floor was too coarse.** The reference's 3.75° rings left one triangle band between the horizon and the first floor
   ring covering most of the floor; interpolating across it misplaces the image by about 2° (lodge) to 3.5° (Sky default).

## Decision

- **D1, a half dome standing on the floor.** With ground projection on, the dome is a hemisphere centred at the feet: its
  rim on the floor at radius R, its top R·vs above the floor ("Radius" now sets the wall). The new per-dome-set setting
  `DomeSettings.Bottom` chooses what lies beneath it:
  - **Image floor** (the sky default; see the amendment for rooms): the image's lower half on a flat floor disc. Each vertex is cast from the capture point (the
    dome centre, camera height h above the floor) along its image direction to the exit of the solid "unit ball centred at
    (0, −f, 0), cut at y ≥ −f" (f = h / (R·vs)): t = min(−f·d.y + √(1 − f²(1 − d.y²)), f / −d.y when d.y < 0). The image's
    horizon sits at camera height; the camera height is now only that.
  - **Open (horizon at your feet)**: only the image's upper half, on the same hemisphere, the image's horizon on the rim at
    the feet, nothing drawn below so the game's own ground shows. The capture point is the hemisphere's centre, so the dome
    centre moves to the feet (local spawn height 0, synced `BakedHeight` 0) and the mesh is rows 0..24 of the reference: a
    byte prefix of model 7. This matches the owner's own −0.15 approximation and the literal "bottom level with the
    horizon". In a room it puts the drawn horizon about atan(eye/R) below eye level (≈9° at R = 10) and drops the image's
    floor; since the amendment it is the room default (a game room already has a floor).
  - Rejected for "or the horizon": a rim at eye level with vertical walls down to an image floor (looks nearly the same as
    Image floor and doesn't match the owner's approximation). Rejected for Open: keeping the centre at camera height and
    pulling the upper half onto a floor-standing hemisphere (vertices leave their rays, the horizon misses the rim, and the
    lighting's inverse map would need a new form).
- **D2, every vertex on its own ray** from the dome centre, for every mesh (sphere, legacy floor, image floor, open bottom),
  so `DomeProjection` and every light direction are unchanged; v still follows elevation linearly.
- **D3, the image-floor rings** (`DomeShape.RingElevations`): 1.875° steps above the horizon (twice the reference); uniform
  rings down to the exact floor/wall crease at −atan f; then square-root-spaced floor rings (48 steps of s =
  (√(π/2) − √θc)/48 in √θ), each at most a 1.25× step in tan θ (floor distance) while within 20° of the horizon. The floor
  sequence ends at exactly −90° once the next ring would fall within s/2 of it (no float equality, no sliver ring); a cap
  of 256 rings throws. Columns, U, normals, tangents and winding keep the reference's formulas; **under projection the rows,
  V values and indices differ from model 7**, not just the positions (Open is a prefix of it). Counts: Room default 102 rows
  / 9,894 vertices; worst allowed (Sky, vs 2, h 0.25: f = 6.25e-6) 130 rows / 12,610 vertices; Open 2,425; the reference
  4,753. Rejected: uniform 1.875° rings (0.9–3.5 px vertical error near the crease, a 50 px Sky shear) and the taper bound
  down to the nadir (double the vertices for nothing visible).
- **D4, error bounds** (interpolation only, Half quantisation excluded, as for the reference): within 0.045° vertically
  (about 1 texel at 4096 rows) and 0.25° horizontally (× cos elevation; edges touching a pole excluded) for camera height
  ≤ 0.9 × dome height, 0.11° vertically up to 0.99. The ~0.21° horizontal error near the crease comes from the 1.25 taper
  along triangle diagonals: the taper ratio is the lever if straight floor lines near the horizon zigzag in game.
- **D5, the camera height** is at least `MinCameraHeight` (0.25) for the image floor: below it the floor is inactive (the
  plain sphere at that height, as before), the slider's minimum is 0.25 while projection is on, and ticking Ground
  projection on with a lower height sets the mode's default (1.6). With Open the slider is disabled (unused).
- **D6, migration v5 → v6**: a dome set (working copy and every backdrop entry) with projection on and 0 < height < 0.25
  goes back to 1.6. Under the new meaning such a height only puts the camera a few centimetres off the floor, which is the
  reported smear; it was the old way to lower the dome. Heights ≤ 0 are left alone (inactive: the same full sphere before
  and after), as are projection-off sets and heights ≥ 0.25. This changes values the user set, which `Migrate`'s policy
  otherwise avoids; it is the one exception, documented there.
- **D7, light containment** (amends ADR 0011's distance rule) for the two half-dome meshes: each VFX light sits at
  clamp(min(nominal, 0.9 × the exit distance through the wall along its direction), 1.5, nominal) from the anchor (feet +
  1), the wall being the ellipsoid (semi-axes R, R·vs, R) centred at the feet, with no floor plane. Above the horizon a
  light stays inside the actual half dome (lower than the old sphere for room domes). Below the horizon it keeps about its
  old place: at vs 1 its distance is ≥ the old 0.9R once |dir.y| ≥ 1/(2R), and at most about 6% shorter just below the
  horizon in small rooms (0.9·√(R² − 1): 2.55 against 2.70 at R = 3, under 0.5% for R ≥ 10). Sky domes (R ≥ 200, vs ≥
  0.25: exit ≥ 49 everywhere) are unchanged. An anchor outside the dome (R·vs ≤ 1) falls back to 1.5. Rejected: a floor
  plane in the bound (below-horizon keys would hug the floor near the player). With Open, light sources in the undrawn
  lower half are skipped (`DomeView.LowerHalfHidden`; ADR 0011's "not drawn, not lit" rule).
- **D8, the spawn height comes from the built file**: a local dome spawns at the centre height of the build that produced
  it (pending or shown), except a sphere, whose height changes respawn without a rebuild (live height). So a failed rebuild
  after a Bottom change never respawns the old file at the new shape's height.
- **D9, synced** builds the same mesh with the same shape and bakes its centre height (h, or 0 for Open); the synced `.avfx`
  grows by at most about 290 KB (the texture is about 32 MB).
- **D10, guards**: the builder rejects an unknown mesh kind, a floor outside (0, 1) for the floor kinds and a floor for the
  others; the self-check adds a 15,000-vertex cap, finite positions and an index count matching the rows; ring-cap and
  monotonicity failures throw `InvalidDataException`. Every guard is an `ArgumentException` or `InvalidDataException`, so
  the controller reports "could not write" and nothing reaches the game.
- **D11, kill switch** `Plugin.HalfDomeAvailable()`: false builds the pre-0015 floor again, byte-identical
  (`SphereMesh.BuildLegacyFloor`, golden `floor0.16_r10`), local and synced; the Bottom choice is hidden and an open entry
  draws that floor.

## Amendment (2026-11-21): the room dome defaults to the open bottom

Tried in game, the owner wanted the room dome to show "the picture only on the hemisphere, leaving the game floor in
place", with the picture's horizon at the feet (chosen over keeping the horizon at eye level without the floor disc).
That is the open bottom, so `DomeSettings.RoomDefaults()` now has `Bottom = Open`; the Sky default stays the image floor.

- **Prior art.** HDRI viewers draw the environment by default on an infinitely distant sphere with no floor (Substance 3D
  Painter's Iray "infinite sphere"); a projected floor is an opt-in extra (Iray's finite sphere with ground, Unreal's HDRI
  Backdrop, Blender ground-projection add-ons, each with the projection centre at the capture height). The image floor is
  that extra; a room in the game already has a floor.
- **Migration v6 → v7**: every room set with the image floor (working copy and every entry) becomes open. v6 existed only in
  dev builds for a few hours and wrote the image floor for every room set, so no room choice is lost; a room image floor
  chosen from v7 on is kept. A v5 file has no bottom and reads the new room default.
- This amendment supersedes D1's "suits skies" note and the room lines under Consequences. The −0.15 room entries become
  open half domes at the feet (their v6 image floor flips to open; the camera height is unused). A corrupted bottom repairs
  to the dome's default (open for a room, image floor for the sky), like the other fields.
- AC-half-dome-11: new configs, v5 files and v6 room sets get the open bottom for the room dome; the sky dome keeps the
  image floor; a v7 room image floor stays.

## Amendment 2 (2026-11-21): the open bottom uses the camera height

Tested in game, the open bottom with its horizon at the feet put half the room's picture below eye level. The owner asked
for the camera height back, so the open bottom now uses it:

- The open bottom is the reference sphere centred at the camera height h and cut where it meets the floor. Its unit cut is
  f = h / (R·vs). It keeps the reference rows above elevation −asin(f), then adds one exact rim ring at −asin(f)
  (v = asin(f)/π), so the rim lies on the floor and the game's floor shows below. The image's horizon sits at eye level.
  The room default (h 1.6, R 10) has 28 rows, 2,716 vertices; at most 49 rows, 4,753 vertices, under the image floor's cap.
- At or below the feet (h ≤ 0, including the old −0.15 entries v7 turned open), the mesh is the uncut upper half centred
  at the feet, byte-identical to before. At or above the dome's top (h ≥ R·vs) projection shows as inactive and the plain
  sphere is drawn, like the image floor.
- The camera-height slider is enabled with the open bottom and goes down to 0. A height change rebuilds the file (a new
  cut), like the image floor. The kill switch is unchanged: off, an open entry builds the legacy floor.
- Lights: the image is undrawn below v = asin(f)/π (`DomeView.CutV`), and the wall bounding the VFX lights is the
  ellipsoid centred at h (`DomeBounds.CentreHeight`); the image floor's wall stays centred at the feet. Synced domes bake
  h as their height, like the image floor.
- No config migration: v7 open entries keep their camera height, which now places the horizon. The room default's 1.6 is
  eye level.
- This amendment supersedes, for Open: D1's Open bullet (centred at the feet), D5's "With Open the slider is disabled",
  D7's wall centre (the ellipsoid is centred at h), D9's "0 for Open" (h > 0 is baked), and D10's floor rule (the builder
  accepts an Open floor value, the cut, only in (0, 1)).
- AC-half-dome-12: the open bottom's horizon sits at camera height, its rim on the floor, the slider enabled.
- EDGE-half-dome-23: an open camera height at or below the feet builds the uncut upper half; at or above the dome's top it
  builds the inactive sphere; a cut on a reference row replaces that row.

## Release gate

No testing or stable tag ships with `HalfDomeAvailable()` true until the worst-case half dome (Sky, vertical scale 2,
camera height 0.25: 12,610 vertices, about 74k indices against the reference's 4,753 / 27,648) has loaded offline in
VFXEditor and then shown in game, locally and for a PSync pair, without a crash. If it fails, flip the switch to false
before tagging.

## Consequences

- Room backdrops draw the open bottom by default (amendments): the picture on the dome cut at the floor, its horizon at
  camera height, the game's floor in place;
  the image floor (a flat floor at the feet; the lodge's camera height migrates to 1.6) is one click away.
- The −0.15 room entries (projection on, v6 image floor) become open half domes at the feet through v7 (amendment): the
  look they approximated. Raising their camera height lifts the horizon (amendment 2). Sky sets at −0.15 stay full spheres.
- Building the dome stays on the framework thread, as before, but is up to about 2.5× the work: measured at about 4 ms a
  build on average and about 12 ms for the largest half dome (including the tests' own parse-back), so at most a one-frame
  hitch when a slider is released. Moving `DomeAvfxBuilder.Build` off the framework thread is the lever if it shows.
- An older build reading a v6 config ignores `Bottom` (it reads as the image floor) and keeps the migrated heights.

## UNVERIFIED in game

1. The worst-case mesh (release gate), locally and for a PSync pair.
2. The lodge floor reads flat after the migration; no streaks near the horizon.
3. Open reads as a dome standing on the ground, the game's floor visible below; with camera height 1.6 the picture's
   horizon is at eye level and its rim meets the floor (amendment 2).
4. The game's own sky visible below the rim where there is no ground (hills, void edges), including with Sun off and Night
   while shown.
5. The Sky dome's horizon band looks right.
6. Room light brightness with the capped distances (ADR 0011 item 16).
