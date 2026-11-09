# ADR 0002: Generated files reach the game through a Penumbra temporary mod

## Status

Accepted; partly superseded by ADR 0006 for synced delivery (experimental).

## Context

Milieu writes a texture (`.atex`) and a VFX (`.avfx`) at runtime and needs the game to load them. The game only
loads files by game path, so something must redirect those paths to Milieu's files on disk. The options were:

- Milieu's own resource hook (as VFXEditor does): duplicates Penumbra's most crash-prone interop, collides with
  Penumbra hooking the same functions, and adds signatures to keep alive across patches.
- The reference mod's trick (replace a monster's body VFX and spawn the monster): needs a spawned actor, an IMC
  manipulation, and conflicts with any other mod for that monster.
- A Penumbra temporary mod.

## Decision

Milieu registers one temporary mod through Penumbra IPC (`Penumbra.AddTemporaryModAll.V5`, tag `"Milieu"`,
priority 0, no manipulations), so it applies in every collection. A static VFX has no owning game object, so
Penumbra resolves its paths through the Base collection; registering in all collections makes the choice of
collection irrelevant.

- Game paths are content-hashed: `vfx/milieu/<16 hex>/sky.atex` and `vfx/milieu/<16 hex>/dome.avfx`. The game caches
  resources by path, so a changed file must get a new path.
- Milieu spawns only after `Penumbra.ResolveDefaultPath` returns its own files for both paths (120-tick timeout).
- Before registering, Milieu checks Penumbra's API version (breaking 5), that mods are enabled
  (`GetEnabledState`) and that a Base collection is assigned (`GetCollection(0xE0)`), so the user gets a specific
  message instead of a timeout.
- `RemoveTemporaryModAll("Milieu", 0)` runs on startup and on every `Penumbra.Initialized`, clearing leftovers.
- IPC is called by label through Dalamud's `GetIpcSubscriber`; the Penumbra.Api and Luna submodules are not used
  for five calls. `PenumbraIpcLabelTests` pins the labels.
- Penumbra is required; there is no fallback resource hook.

## Consequences

Easier: no resource hooking of Milieu's own; Penumbra already handles load-time redirection safely.
Harder: Milieu does nothing without Penumbra, and a user with mods disabled or no Base collection must fix that in
Penumbra first.
