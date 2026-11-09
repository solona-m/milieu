# ADR 0006: Synced delivery through an item's VFX

## Status

Accepted (experimental; unverified in game). Offered on testing and dev builds only until 2026-11-09; since then on every build (see the kill switch amendment).

## Context

Local delivery (ADR 0002) spawns a static VFX nobody else sees. The owner wants PSync pairs to see the dome too.
PSync (the sync plugin the owner uses; source checked in `PSync/PlayerSync`) only sends what loads for the player's
own game objects, and only uploads files that live under Penumbra's mod directory or its own cache:

- `FileCacheManager` hashes only paths under the mod directory or PSync's cache; anything else gets an empty hash and
  is dropped.
- `.avfx`/`.atex` loaded for the player's objects are recorded as transient resources; the ornament at object index
  player+1 is tracked (MinionOrMount).
- The player's meta manipulations go along through `GetPlayerMetaManipulations` (the player collection only).

ADR 0002 rejected "the reference mod's trick (replace a monster's body VFX…)" because it needs an IMC manipulation and
conflicts with any other mod for that monster.

## Decision

An optional "Visible to" choice: **Only me** (default, ADR 0002 unchanged), **PSync: Plush Cushion** (ornament
m8058, body b0001) or **PSync: Mad Bird Ring (right hand)** (accessory a0028, RFinger).

- The dome becomes the item's own VFX: one Penumbra temporary mod, tag `MilieuSync`, priority 1,000,000, added with
  `AddTemporaryMod.V5` to the **player's collection only** (`GetCollectionForObject.V5(0)`), never `AddTemporaryModAll`
  (an IMC change in every collection would put the dome on every character wearing the item, pairs included).
- It carries an IMC manipulation (v0 JSON string) that sets the item part's VFX id on every variant to the first id
  from 200 up that neither the vanilla IMC nor the user's own IMC overrides use, plus two redirects: the VFX path
  (`…/m8058/obj/body/b0001/vfx/eff/vm####.avfx`, or `…/a0028/vfx/eff/va####.avfx`) → the generated dome, and the
  content-hashed `vfx/milieu/<key>/sky.atex` → the staged texture.
- Accessory VFX are a **Penumbra feature**, not game behaviour: Penumbra's resolve hook asks for
  `chara/accessory/a{id:D4}/vfx/eff/va{VfxId:D4}.avfx` (`Interop/Hooks/Resources/ResolvePathHooksBase.cs`). Pairs need a
  Penumbra with that hook (PSync requires Penumbra anyway).
- The files are real copies in `<mod directory>\_milieu_sync\` (folder marked Hidden, which Penumbra's mod scan skips;
  ASCII, no bg/bgcommon/ui segment, which PSync's scan skips). Atomic writes; cleanup touches only Milieu's patterns.
- The game, not Milieu, places the item's VFX, so height and yaw are baked into the particle's Pos/Rot Y curves and the
  emitter is bound to its owner with `PICd 1` (InitialPosition_WithOptions), `ICbP 1`, `ICbR 0`, `ICbS 0`: follow the
  carrier's position, not its rotation. The alternative if that doesn't follow in game: `PICd 2` (AllAlways).
  `DomeAvfxBuilder.SyncedAttach` holds the values; local output is byte-identical (golden test).
- The user's IMC fields (material, decal, attributes, sound, animation) are kept: the base entry is the user's override
  (read from the player meta, Penumbra's v1 string) or else vanilla. Bases are cached only while a registration for
  that (collection, item) is live, so Milieu never reads its own entry back; a stale entry with this session's last
  VFX id is ignored.

Answers to ADR 0002's reasons for rejecting the reference trick: there is no spawned actor (the user's own item carries
it); the IMC manipulation is Milieu's own temp mod in one collection, not an edit of anyone's mod; the conflict with
other mods for the item is handled by priority and by keeping every IMC field but the VFX id.

**Kill switch:** the options are shown only when `IsTesting || IsDev`. On stable builds the group isn't drawn, a saved
synced choice loads as Only me, and the controller never takes the synced path. Reason: with synced mode on, PSync
pairs receive AVFX bytes that have never been loaded in game, without having opted in, and the "(experimental)" label
never reaches them. Enabling it on stable is one line (`Plugin.SyncedAvailable`) after an in-game check. Startup and
Penumbra re-init cleanup still run on stable (removal only; the staging folder is never created there).

**Amendment (2026-11-09):** the owner enabled synced delivery on stable builds before the in-game check, by owner
decision. `Plugin.SyncedAvailable` now returns true on every build and stays the kill switch: returning false restores
the behaviour above. The "(experimental)" label and the PSync notice are unchanged.

## Consequences

- Personal data: a user image is uploaded to the PSync server and sent to all pairs. Milieu shows a one-time notice
  before the first user image is shared (the image is only uploaded on the next click); bundled scenes switch at once.
- Shared collections: if the character has no individual collection (or uses Base), anyone else resolved to the same
  collection who wears the item shows the dome on the user's screen. Milieu warns.
- A mod that gives the item its own VFX loses that VFX while synced mode is on (Milieu sets the VFX id).
- If the user changes their ring/cushion mods while synced, the cached base stays until Hide → Show.
- Disk: one staged texture (~45 MB) and dome per scene under the mod directory, cleaned on Hide and at startup.
- Every change redraws the player; pairs download again after the next sync (~45 MB each, plus VRAM).
- If another plugin assigned a temporary collection to the player while the user has an individual collection,
  synced show fails closed with `SyncRedirectTimeout`.

- Switching from Only me to a PSync option while a synced blocker applies (e.g. no mod directory) leaves the local
  dome up until the blocker clears; accepted (nothing is half-registered).
- Wiring: the controller takes the synced dependencies as optional constructor parameters, and without them (or with
  synced delivery unavailable) it behaves exactly as local delivery: a fail-safe default.
- Tech debt: `BackdropController` now holds both deliveries' lifecycles; splitting the synced branch into its own
  coordinator is a follow-up.

## Implementation notes

- Internal signatures drifted from the plan, behaviour unchanged: `SyncPlanner.ResolveBase(target, vanillaImc,
  string? playerMeta, byte? ignoreVfxId)` and `Build(SyncBase, atexGamePath, atexDisk, avfxDisk)` (the target comes
  from the base, no separate parameter); `SyncedDelivery.Begin` returns `(MessageKey Failure, StatusMessage? Message)`;
  `PenumbraMeta.ReadImcOverrides(string?)` plus `TryReadImcOverrides` for the unreadable-meta status.
- The ring's VFX path `chara/accessory/a0028/vfx/eff/va####.avfx` is shared by every a0028 slot. While synced, in the
  player's collection only, Milieu's redirect overrides a user mod's own `va####` at the chosen id (200 or the next
  free one), and an IMC VFX id ≥ 200 that a user mod gives another a0028 slot then loads Milieu's dome if it equals
  the chosen id. The chosen id skips every id the vanilla IMC and the user's overrides use for the right-ring part only.

## When the dome doesn't show

The dome is the item's own VFX, so it shows only while the game draws that item for that character:

- The item isn't drawn: the ring isn't equipped on the **right** hand (a left-hand Mad Bird Ring doesn't carry it), or
  glamour tools hide it; the cushion isn't out, or the game dismissed it (mounting, some zones and duties).
- The character isn't drawn: culled by distance or the game's character limit, or hidden by a plugin.
- Pairs: they paused the user in PSync, filter VFX in PSync's settings, or haven't synced since the change.
- The ornament has its own individual Penumbra collection: it resolves there, not in the player's collection that holds
  `MilieuSync`.
- Penumbra didn't pick up the files in time: `SyncRedirectTimeout`; try again.

Open in-game questions (UNVERIFIED): the dome loads on the cushion and on the ring; it follows when walking and doesn't
swing with emotes (else try `PICd 2`); item VFX culling with a 20000-unit sky dome; what a pair outside the dome radius
sees (room mode); two synced Milieu users near each other; whether `IsTesting`/`IsDev` gate as expected.
