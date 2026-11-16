# ADR 0006: Synced delivery through an item's VFX

## Status

Accepted (experimental). Offered on testing and dev builds only until 2026-11-09; since then on every build (see the kill switch amendment). First verified in game on 2026-11-16, which changed the delivery (see the 2026-11-16 amendment; it supersedes the temporary mod, the VFX id range and the Hidden folder below).

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
  coordinator is a follow-up. Synced VFX lights (ADR 0011: rig loading, the light-rebuild debounce and GPose hold,
  `SyncedLights`) add another reason.

## Amendment (2026-11-16): verified in game; a real mod, low VFX ids, height anchoring

Tested in game with the owner (cushion and ring). What changed, and why:

- **VFX ids 1..127, not 200+.** With the cushion at id 200 the game asked for `vfx/monster/eff/vm0200.avfx` (a shared
  folder), not the cushion's own path; the ring at 200 never loaded. Milieu now takes the lowest id from 1 to 127 that
  neither the vanilla IMC nor the user's overrides use (`SyncTarget.FirstVfxId`/`LastVfxId`).
- **A real mod, "Milieu Sync", replaces the `MilieuSync` temp mod** (owner's choice: it shows in Penumbra's list). The
  staging folder `<mod dir>\_milieu_sync` is no longer Hidden; Milieu writes `meta.json` + `default_mod.json` (v3,
  atomically; Penumbra migrates them to v4) and `SyncModRegistrar` calls `ReloadMod`/`AddMod`, `TrySetModPriority`
  (1,000,000) **then** `TrySetMod(true)` in the player's collection, then removes an older build's temp mod. Every
  call names the mod by its folder only (an empty mod name): with a name, Penumbra falls back to any mod of that name
  when the folder isn't registered, which could be a user's own "Milieu Sync". On a first
  show a failed priority enables nothing and a failed enable is disabled again (fail closed); on a rebuild the mod is
  already enabled, so a refusal is handled by putting back or taking down (below).
  - **Mod-management calls are new:** until now Milieu only made temporary-mod and read calls (stated in `PenumbraIpc`'s
    doc, not in an ADR). They are made on Milieu's own mod only, never on the user's mods.
  - **Empty at rest.** Hide, Teardown, logout and startup cleanup disable the mod and rewrite it with no files and no
    manipulations (`Neutralize`), then reload it; with Penumbra gone the files are still emptied on disk. An enabled state
    left behind *after* such a cleanup (a failed disable, an inheriting collection) then changes nothing even if Milieu
    never runs again. **Residue accepted:** a crash or a game exit *while a synced backdrop is shown* leaves the mod
    enabled with its content (the user's image included, which PSync can keep sharing) until Milieu's next startup
    cleanup, or for good if Milieu is removed. The upload notice was shown when the image was first shared.
  - **Unsettled collections.** A collection whose registration failed, or whose registration Penumbra's disposal made
    Milieu forget, is retried by the next Hide, logout, unload or startup cleanup. A failed rebuild puts back what the
    shared mod held before (the pending registration, else the shown one); a registration that can't be put back is
    dropped, and when nothing is left registered the mod is taken down (disabled and emptied) and the controller goes
    Hidden (`SyncShownLost`). The same holds when a pending registration times out and the shown one can't be put back.
  - **Penumbra's migration** turns Milieu's v3 files into v4 on every reload and deletes `default_mod.json` with a backup,
    so, depending on the user's Penumbra backup mode, `.bak` copies (game paths and IMC entries, no image) may appear in
    the folder; Milieu neither writes nor deletes them.
  - **Inheritance (accepted).** Unlike a temp mod, a real mod's enabled state is inherited: collections that inherit the
    player's collection show the dome too while it is shown. At rest the mod is empty, so leftovers are harmless.
  - **Evaluated alternative / fallback, not implemented:** Penumbra's temporary mod *settings* with a lock key
    (`SetTemporaryModSettings`, `TemporaryApi.cs`) keep the real mod's files (which PSync hashes) while saving nothing
    in the user's collections; its inheritance behaviour is unchecked.
- **Height anchoring.** Item VFX are anchored at **world height 0**, not at the item or the player: on an upper floor
  (world Y 18.25) the dome sat ~18 units low, hidden under the floor ("small domes don't show"). The synced build writes
  the player's world Y into the emitter's Pos Y (`DomeAvfxParams.EmitterBaseHeight`, |y| ≤ 10,000). It is baked at
  build time: after changing floors, pick the scene again (or change a setting) to move it (ND-4a, documented only).
  No camera binder is needed.
- **Redraw everything.** A player redraw doesn't reload the cushion (a separate object); synced rebuilds call
  `RedrawAll`, falling back to a player redraw.
- **Own-override rule.** An IMC override is skipped as Milieu's own only when it equals exactly the entry Milieu
  registered for that variant this session (`SyncPlan.Entries`); any other override, even one using Milieu's last id, is
  the user's and its id is avoided. A foreign override identical in every field can't be told apart (harmless: Milieu's
  priority mod wins the IMC entry and the path).
- **Mod conflicts (surfaced, not fixed).** With mods that replace the cushion's model or textures enabled, the dome didn't
  show on it; the cause is unknown. The cushion option's hint says so.

The rest of this ADR stands (player's collection only, kept IMC fields, baked placement, the notice, the kill switch).

## Implementation notes

- Internal signatures (as of the 2026-11-16 amendment): `SyncPlanner.ResolveBase(target, vanillaImc, string? playerMeta,
  IReadOnlyList<ImcEntry>? ownEntries)` and `Build(SyncBase, atexGamePath, atexDisk, avfxDisk)`; `SyncedDelivery.Begin`
  returns `(MessageKey Failure, StatusMessage? Message, bool ShownLost)`; `PenumbraMeta.ReadImcOverrides(string?)` plus
  `TryReadImcOverrides` for the unreadable-meta status.
- The ring's VFX path `chara/accessory/a0028/vfx/eff/va####.avfx` is shared by every a0028 slot. While synced, in the
  player's collection only, Milieu's redirect overrides a user mod's own `va####` at the chosen id (200 or the next
  free one; 1..127 since the 2026-11-16 amendment), and an IMC VFX id that a user mod gives another a0028 slot then loads
  Milieu's dome if it equals the chosen id. The chosen id skips every id the vanilla IMC and the user's overrides use for the right-ring part only.

## When the dome doesn't show

The dome is the item's own VFX, so it shows only while the game draws that item for that character:

- The item isn't drawn: the ring isn't equipped on the **right** hand (a left-hand Mad Bird Ring doesn't carry it), or
  glamour tools hide it; the cushion isn't out, or the game dismissed it (mounting, some zones and duties).
- The character isn't drawn: culled by distance or the game's character limit, or hidden by a plugin.
- Pairs: they paused the user in PSync, filter VFX in PSync's settings, or haven't synced since the change.
- The ornament has its own individual Penumbra collection: it resolves there, not in the player's collection that holds
  "Milieu Sync".
- A mod that changes the cushion's model or textures is enabled (seen in game; turn it off).
- An upper floor before the 2026-11-16 amendment: the dome was anchored at world height 0, under the floor.
- Penumbra didn't pick up the files in time: `SyncRedirectTimeout`; try again.

Verified in game (2026-11-16): the dome loads on the cushion and on the ring (sky mode on the ring, sky and room on the
cushion) once at the player's height. Still open (UNVERIFIED): it follows when walking and doesn't swing with emotes
(else try `PICd 2`); what a pair outside the dome radius sees (room mode); two synced Milieu users near each other; that
the kill switch hides the options when off; poses surviving a `RedrawAll` in GPose.
