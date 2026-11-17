# ADR 0013: Sun off while the backdrop is shown to you only

## Status

Accepted (2026-11-16). On by default, under a release gate (below). The in-game effect, the call frequency of the hooked
function and the Brio interaction are UNVERIFIED (see the list at the end).

## Context

The game's sun lights the scene and casts shadows through the dome. A VFX can't block it: dev builds 43-47 tried an opaque
shell at radius 40 and 3, particle clip far, clip base point = caster, and the Lotus VFX's root clip settings, and none
blocked the sun; the Lotus VFX itself adds its own VFX sun instead. Night while shown (ADR 0012) writes
`EnvManager.DayTimeSeconds`, which the game re-derives from its clock (seen with the owner, 2026-11-16). The owner asked:
"ok turn off the sun in "Only me" mode then".

Brio (GPL-3.0) overrides environment lighting by hooking the game's environment update,
`UpdateEnvironment(EnvState* a1, EnvState* a2)` (signature `0F 10 42 08 0F 11 41 08 F2 0F 10 4A 18`,
Game/World/EnvironmentService.cs), and writing saved fields into a1 after the original. Its `BrioEnvManager.cs` (credited
to Ktisis) lays out `EnvState` at EnvManager + 0x58, `EnvironmentLighting` at state + 0x20 (`SunlightColor` +0x00,
`MoonlightColor` +0x0C, `AmbientColor` +0x18) and another EnvState (`GPoseBrightness`) at + 0x448. Dalamud's bundled
ClientStructs names `EnvManager.EnvState` at 0x58 but has no lighting fields.

## Decision

A "Game sun" block with "Turn off the game's sun" (`Configuration.SunOffWhileShown`, **on by default**). While it is on,
the delivery is "Only me" (`DeliveryMode.Local`), a local dome is drawn (`LitBackdrop { Synced: false }`) and the game is
ready, Milieu zeroes the game's sun and moon light colour (24 bytes, state 0x20..0x37) after every environment update of
EnvManager's own state, in and out of GPose. Ambient light is left alone. Under a PSync delivery Milieu never touches the
sun (pairs would see it anyway).

- **Shape.** `SunOff` (`Milieu/Lighting/SunOff.cs`, ticked on the framework thread after night) decides, checks and logs.
  `SunOffInterop` (`Milieu/Interop/SunOffInterop.cs`) scans and hooks; its detour calls the original, then the static
  `SunOffPostStep.Run` (`SunOffPatch.cs`) on a preallocated `SunOffShared`: no closure, no allocation, no logging, only
  `Volatile`/`Interlocked` operations on the game's thread.
- **Where it writes.** Only when a1 == EnvManager + 0x58 (the function is a general EnvState copy: Brio's commented-out
  `UpdateEnvDetour` calls it on an environment-part object too), only while the flag is set, and only after the sample
  was approved. Brio writes into every a1, but writes a1's own pre-call values back; Milieu's zeros could linger in other
  copies, so it doesn't (`SunOffLayout.OwnStateOnly`).
- **Why sun and moon, not ambient.** The moon also lights and casts shadows; the dome replaces the whole sky. Ambient stays:
  when `LocalLightsAvailable` (and not latched) with scene lighting on, the backdrop's own lights light the scene outside
  GPose (ADR 0011's local amendment); otherwise Milieu has no lights there (ADR 0009 native lights are GPose-only), so
  zeroing ambient would black the scene out and the scene keeps only ambient light.
- **Plausibility check (per session) before the first zeroing.** On the first own-state call while writing, the detour
  claims the probe (CAS NotProbed → Capturing), copies the six floats and publishes Captured; nothing is zeroed on that
  call. The next tick logs them and checks: each finite, 0 or normal (no subnormals), within [0, 100]. Pass (at least one
  non-zero) approves; all zero is inconclusive (re-armed at most once per 60 ticks, logged once per episode, no latch);
  anything else latches Unavailable for the session. A heuristic that catches garbage, not a proof.
- **Diagnostics.** Every call is counted once (own state, GPose state, other, no manager) and the first 4 distinct raw
  deltas from the manager are kept; at episode tick 600 they are logged with the probe state, plus a Warning when fewer
  than 300 own-state calls arrived (the sun may take a moment to come back after hide). One in-game run tells where the
  rendered state is if the sun doesn't go off.
- **Restore = stop writing.** The detour writes only after the original refilled a1 on that call, so the next call after
  the flag drops leaves the game's values. No snapshot, no write-back.
- **Hook lifetime.** The constructor only scans (`ScanAllText`, exactly one match) and checks EnvManager's static pointer.
  The hook is created on the first activation (framework thread), assigned before `Enable()` so the detour always finds
  it, and stays enabled as a pass-through until dispose (Brio keeps its own enabled for its whole life). The detour calls
  `OriginalDisposeSafe`, so a call in flight during dispose reaches the game instead of throwing. `Install` and `Dispose`
  share one lock (the detour never takes it). A failed install latches Unavailable for the session.
- **Crash sentinel** (`sun-off-attempt.flag`, the ADR 0009 mechanism): set before the install and the flag; cleared at
  episode tick 600 if still waiting for approval (the install has survived); set again just before the first approval and
  cleared 600 ticks after it; cleared on stop, off→on and a failed check. Found at startup: CrashGuard until off→on.
- **Kill switch** `Plugin.SunOffAvailable()`: off means no scan, no hook, no `SunOff`, no block.
- **Dispose.** Teardown order lighting, night, sun off, controller; then `SunOffInterop.Dispose` closes the flag (no later
  `SetWriting(true)` can raise it: raising and closing each store with a full fence, `Interlocked.Exchange`, before
  reading the other side, a Dekker pair) before disposing the hook; `Install` refuses once closed.
- **Night (ADR 0012) unchanged.** The two don't interact. Recommendation: once sun off is verified in game, turn
  `NightAvailable()` off and retire night.

## Exceptions to earlier ADRs

- **Default on**, departing from ADR 0012's off-by-default precedent: the owner asked for the sun to be off under "Only me",
  and an off default wouldn't do that. Bounded by the lazy install, the plausibility check, the sentinel, the kill switch
  and the release gate.
- **Frozen offsets** (ADR 0009: "Layouts are ClientStructs' … no local mirrors"): ClientStructs has no EnvLighting fields,
  so 24 bytes at state 0x20..0x37 (and the 0x58/0x448 state offsets) are mirrored from Brio/Ktisis (`SunOffLayout`),
  guarded by the exact own-state address match and the plausibility check.
- **Static pointer on another thread** (ADR 0012's exception, framework thread only): the detour reads
  `EnvManager.StaticAddressPointers.ppInstance` on the game's calling thread: a data read, null-checked, no `Instance()`.
- **try/catch doesn't catch native access violations.** The detour's try/catch only stops a managed bug escaping into the
  game; the real guards are the null checks, the exact address match and the check before any write. Don't loosen the
  address check (`OwnStateOnly = false`) on the strength of the try/catch.

## Brio on the same function

Dalamud chains hooks at one address; the order isn't Milieu's to choose.

- A. Brio's environment override off: both pass through; the sun is off while shown and back after hide.
- B. Milieu outermost with Brio's EnvironmentLighting (or advanced) override on, or Brio's override turned on while Milieu
  is already active: Brio freezes the zeroed state, so after hide **the sun stays off until Brio's override is off**.
- C. Brio outermost with its override already on before Milieu activates: Brio writes its frozen colours back after
  Milieu's zeroing, so **the sun stays on while the dome is shown**, and Milieu's status still says it is on.

Accepted; Milieu can't detect Brio's override (no public API).

## Release gate

No `testing-<n>` or `v…` tag ships with `SunOffAvailable() => true` and the option on by default until UNVERIFIED 1, 2, 3
and 5 have passed in game. Otherwise flip the kill switch to false, or the default to off, before tagging. A process gate
(the owner tags), not a test.

## Consequences

- It patches a game function's behaviour on the user's client: a wrong signature after a patch disables it (exactly one
  match), a crash at install or in the first zeroing latches CrashGuard next session.
- Outside GPose the scene is lit by the backdrop's own lights when `LocalLightsAvailable` (ADR 0011's local amendment);
  otherwise it is darker (ambient only). The note follows the switch.
- Brio cases B and C above.
- Placement: `ISunSwitch`, `SunOffStatus` and `SunOff` in `SunOff.cs`; `SunOffScan`, `SunOffPlausibility` and
  `SunOffPostStep` in `SunOffPatch.cs`; `SunOffShared` in its own file; the mirrored native layout, `SunOffLayout`, in
  `Milieu/Interop/SunOffLayout.cs`.

UNVERIFIED in game:

1. Zeroing the sun (0x20) and moon (0x2C) colours removes the game's sun light and sun shadows on the character and the
   map, day and night; ambient stays; the logged sample looks like the game's colours.
2. The rendered update targets EnvManager + 0x58 (own-state count > 0 in the log, and the effect is visible).
3. About one own-state call per frame (no low-rate Warning); after hide, unticking, a zone change or logout the sun
   returns promptly.
4. In GPose the sun stays off; the +0x448 state doesn't bring it back.
5. No crash across install, capture and pass-through: show/hide cycles, zone changes, GPose, plugin reload, game exit.
6. No sun disc or flare through the opaque dome (else a `Fog.SunVisibility` follow-up).
7. Brio coexistence: case A works; B and C behave as described.
8. Interiors and fixed-lighting zones: no artefacts.
9. The dome's own brightness doesn't change (its particle is unlit, bLgt 0).
10. In GPose, the native-light rig (ADR 0009/0010 constants) still reads well with the sun off; else a retune follow-up.
