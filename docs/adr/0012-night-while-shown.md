# ADR 0012: Night while the backdrop is shown

## Status

Accepted (2026-11-16). Off by default. Whether writing the time of day takes visible effect, and how the game updates the
field, are UNVERIFIED in game.

## Context

The owner asked whether the dome could block the game's sun. It can't: an AVFX particle has no shadow-casting setting
(only effector lights do), so the sun still lights the scene through the backdrop and fights its lights (ADR 0009, 0011).
The game exposes its local time of day (`EnvManager.DayTimeSeconds`, FFXIVClientStructs
Client/Graphics/Environment/EnvManager.cs), as weather and time plugins use; setting it to night removes the sun on the
user's own screen. The sun's colour and strength aren't exposed, so dimming it partway isn't possible this way.
(Superseded by ADR 0013: Brio/Ktisis lay out the sun and moon colour, and sun off zeroes them through a hook.)

## Decision

An option, "Night while the backdrop is shown" (off by default, `Configuration.NightWhileShown`), in a "Time of day" block.
While it is on, a dome is drawn (`BackdropController.IsDrawn`) and the game is ready, `NightWhileShown` (ticked after scene
lighting) writes 01:00 ET (`NightTime.Seconds` = 3600) into `DayTimeSeconds` every tick. Only the user sees it.

- **Seam.** `IEnvTime` (read the field, read the game clock, write the field); `EnvTimeInterop` implements it, framework
  thread only.
- **Cross-check before the first write.** The read must be finite and in [0, 86400) (else latched Unavailable until the
  option is unticked and ticked), and within 300 ET s (`CrossCheckTolerance`, wrap-aware) of the game clock
  (`Framework.ClientTime`: `EorzeaTimeOverride` when `IsEorzeaTimeOverridden`, else `EorzeaTime`, mod 86400). A mismatch
  means this zone keeps its own time: "not in this zone", no write, checked again after the next territory change (not
  latched). This also guards against a wrong-but-mapped field after a ClientStructs lag.
- **Ownership.** The field is Milieu's while it reads within `min(2 + 20.57 × elapsed × 1.5, 600)` ET s of night, where
  `elapsed` is the real time since Milieu's last write: tight right after a write (another plugin's nearby value isn't
  claimed), following a field the game advances between frames, capped for long gaps.
- **Restore = write the value derived from the current game clock, only if the field is still Milieu's.** If the game
  recomputes the field every frame the write is harmless; if it doesn't, stopping the writes alone would leave night on
  forever, and a snapshot taken at the start would be stale by however long night lasted. The cross-check showed the clock
  is what this zone uses. Rejected: stop-writing only; a captured snapshot.
- **Release paths.** Restore within the call: option off, no dome drawn, `Hidden` (Hide, None, `/milieu off`, Penumbra
  disposed, a lost synced dome), teardown. Forget (no write now): territory change and logout set a pending restore that
  the first ready tick performs before anything else (a territory change also clears "not in this zone"). On logout the
  night is forgotten *before* the controller hides the backdrop, so its Hidden event doesn't restore during the logout.
  A restore whose reads come back empty (the object momentarily absent) is kept for the next ready tick (until teardown).
  While the game exits (`IsFrameworkUnloading`): no read or write at all.
- **Writes.** `Ok`, `Skipped` (the EnvManager instance isn't there that tick: no write, no latch) or `Failed` (a refusal or
  a bad value: latched Unavailable). An off-thread refusal logs at Error, as LightInterop's does.
- **Crash guard.** A sentinel (`<config>/night-attempt.flag`, the ADR 0009 mechanism) is set before an episode's first
  write and cleared after 600 stable ticks, on release or forget, and on off→on; found at startup, night stays off until
  the option is ticked again.
- **Kill switch.** `Plugin.NightAvailable()`: off means nothing night-related is constructed, resolved or drawn.
- **Exception to ADR 0009's "scanned function pointer only, never the generated statics".** Night reads two static-address
  pointers that ClientStructs resolves at startup, `EnvManager.StaticAddressPointers.ppInstance` and
  `Framework.StaticAddressPointers.ppInstance`, with null checks: data reads only, no game function is called and the
  generated `Instance()` (which throws when unresolved) is never used. A null outer pointer means unavailable this session;
  a null inner pointer means not created yet (null reads, no latch). Why the resolved pointer: it reuses ClientStructs'
  maintained signature rather than a second scan, and the null checks replace exception handling.
- **Non-hook fallback (not implemented):** set `ClientTime.EorzeaTimeOverride` with `IsEorzeaTimeOverridden`, so the game
  itself computes night (as Brio reads it). Its effect on server-time-driven systems is unknown.

## Consequences

- The sun stops competing with the backdrop on the user's screen; pairs keep their own sun.
- It writes game memory: a wrong field after a patch is caught by the plausibility and clock cross-check, which also stops
  it in zones with fixed time.
- While night is on, Milieu writes 01:00 every tick, so it overrides another plugin's (or Brio's) time control for as long
  as the backdrop is shown; only at release does it leave a field that something else changed alone.
- Placement: `NightTime`, `IEnvTime` and `EnvWrite` live in `NightWhileShown.cs`; the night sentinel reuses
  `FileLightingSentinel` with its own file and log label.

In-game result (owner, 2026-11-16): writing `DayTimeSeconds` doesn't drive the lighting; the game re-derives the time
and its lighting from the clock. ADR 0013 (sun off, on by default, departing from this ADR's off-by-default precedent)
turns the sun off instead; recommendation: once sun off is verified in game, turn `NightAvailable()` off and retire night.

UNVERIFIED in game (before the result above): that writing `DayTimeSeconds` in the framework update takes visible effect (else the override above);
whether the game recomputes or latches the field; zones where the cross-check reports "not in this zone".
