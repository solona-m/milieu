using System;
using System.Collections.Generic;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>
/// The synced registration lifecycle over <see cref="IPenumbraSync"/>: the real "Milieu Sync" mod, enabled in the local
/// player's collection, holding the target's IMC VFX id and the dome redirects (docs/adr/0006). Framework thread only.
/// <list type="bullet">
/// <item>The user's IMC base entries are read once per (collection, target) and cached only while a shown or pending
/// registration holds that key, so Milieu never reads its own entry back as a user override; every path that leaves
/// nothing registered drops the entry.</item>
/// <item>The entries Milieu registered per target this session are remembered, so exactly that stale entry seen on a
/// re-read (a removal Penumbra applied late) is ignored rather than taken for the user's; any other override is the user's.</item>
/// <item>A collection whose registration failed is kept as unsettled until a cleanup reaches it, because a failed enable
/// may have left the mod enabled there.</item>
/// <item>Every collection registered this session is remembered, so cleanup reaches temporary or deleted ones too.</item>
/// </list>
/// </summary>
public sealed class SyncedDelivery(IPenumbraSync sync, IGameData gameData, ILogSink log)
{
    public sealed record Registration(Guid Collection, bool IndividualSet, SyncTarget Target, SyncPlan Plan);

    private readonly Dictionary<(Guid, DeliveryMode), SyncBase> baseCache = [];
    private readonly Dictionary<DeliveryMode, IReadOnlyList<ImcEntry>> lastEntries = [];
    private readonly HashSet<Guid> sessionGuids = [];
    private readonly HashSet<Guid> unsettled = [];

    public Registration? Shown { get; private set; }

    public Registration? Pending { get; private set; }

    public bool HasRegistration => Shown != null || Pending != null;

    /// <summary>Something to take down on hide, logout or unload: a registration, or a collection a failed registration touched.</summary>
    public bool NeedsCleanup => HasRegistration || unsettled.Count > 0;

    /// <summary>Collections a failed registration left possibly enabled (cleared by RemoveAll and ClearEverywhere).</summary>
    public IReadOnlyCollection<Guid> Unsettled => unsettled;

    public IReadOnlyCollection<Guid> SessionGuids => sessionGuids;

    /// <summary>
    /// Plans and registers the synced mod for the staged files. Returns the failure, or None (then Pending is set).
    /// ShownLost: a failed rebuild in the shown registration's collection had already rewritten the shared mod, and putting
    /// the shown dome back failed too, so Shown was dropped (the caller must stop treating it as drawn).
    /// </summary>
    public (MessageKey Failure, StatusMessage? Message, bool ShownLost) Begin(PreparedTexture staged, string avfxDisk, SyncTarget target)
    {
        if (sync.PlayerCollection() is not { } player)
            return (MessageKey.SyncNoPlayerCollection, StatusMessage.Of(MessageKey.SyncNoPlayerCollection), false);
        var key = (player.Id, target.Mode);
        if (!baseCache.TryGetValue(key, out var b))
        {
            var own = lastEntries.TryGetValue(target.Mode, out var last) ? last : null;
            var (resolved, failure) = SyncPlanner.ResolveBase(target, gameData.ReadVanilla(target.ImcPath), sync.PlayerMeta(), own);
            if (resolved == null) return (failure, StatusMessage.Of(failure, ("item", target.DisplayName)), false);
            b = resolved;
            baseCache[key] = b;
            if (b.MetaUnreadable) log.Debug("[Milieu] Couldn't read the player's meta manipulations; using the game's IMC entries.");
        }
        var (plan, buildFailure) = SyncPlanner.Build(b, staged.GamePath, staged.DiskPath, avfxDisk);
        if (plan == null)
        {
            DropUnlessHeld(key);
            return (buildFailure, StatusMessage.Of(buildFailure, ("item", target.DisplayName)), false);
        }

        // A live registration in another collection (the player's collection changed) comes out of that collection first.
        var clearedOld = false;
        foreach (var old in new[] { Pending, Shown })
            if (old != null && old.Collection != player.Id) { sync.ClearSynced(old.Collection); clearedOld = true; }
        if (Pending != null && Pending.Collection != player.Id) { baseCache.Remove((Pending.Collection, Pending.Target.Mode)); Pending = null; }
        if (Shown != null && Shown.Collection != player.Id) { baseCache.Remove((Shown.Collection, Shown.Target.Mode)); Shown = null; }

        var code = sync.SetSynced(player.Id, plan.Paths, plan.Manips);
        sessionGuids.Add(player.Id);
        if (code != 0)
        {
            log.Warning($"[Milieu] Penumbra refused the synced backdrop (code {code}).");
            var shownLost = RecoverAfterRefusal(player.Id);
            DropUnlessHeld(key);
            if (clearedOld || shownLost) sync.RedrawPlayer();   // the item still carried the old dome: reload it without
            var message = shownLost
                ? StatusMessage.Of(MessageKey.SyncShownLost, ("code", code))
                : StatusMessage.Of(MessageKey.SyncRefused, ("code", code));
            return (MessageKey.SyncRefused, message, shownLost);
        }
        Pending = new Registration(player.Id, player.IndividualSet, target, plan);
        lastEntries[target.Mode] = plan.Entries;
        return (MessageKey.None, null, false);
    }

    /// <summary>Penumbra resolves both pending game paths, for the player, to the staged files.</summary>
    public bool PendingResolved()
    {
        if (Pending == null) return false;
        foreach (var (gamePath, disk) in Pending.Plan.Paths)
            if (!BackdropController.SamePath(sync.ResolvePlayer(gamePath), disk)) return false;
        return true;
    }

    /// <summary>The pending registration becomes the shown one; the player is redrawn so the item reloads. Returns the sync warnings.</summary>
    public IReadOnlyList<StatusMessage> Commit()
    {
        if (Pending == null) return [];
        sync.RedrawPlayer();
        var previous = Shown;
        Shown = Pending;
        Pending = null;
        if (previous != null) DropUnlessHeld((previous.Collection, previous.Target.Mode));
        return SharedWarnings(Shown);
    }

    /// <summary>
    /// The pending registration didn't resolve (or a new Show supersedes it): take it out and put the shown one (if any)
    /// back. Returns true when the shown one couldn't be put back and was dropped too (the mod is then taken down and
    /// emptied; the caller must stop treating it as drawn).
    /// </summary>
    public bool AbortPending()
    {
        if (Pending == null) return false;
        var p = Pending;
        Pending = null;
        sync.ClearSynced(p.Collection);
        var shownLost = false;
        if (Shown == null) sync.RedrawPlayer();   // nothing to put back: reload the item without the pending dome
        else if (!Restore(Shown))
        {
            log.Warning("[Milieu] Penumbra refused restoring the shown synced backdrop; taking it down.");
            TakeDown(Shown.Collection);
            baseCache.Remove((Shown.Collection, Shown.Target.Mode));
            Shown = null;
            shownLost = true;
            sync.RedrawPlayer();
        }
        DropUnlessHeld((p.Collection, p.Target.Mode));
        return shownLost;
    }

    /// <summary>Hide, logout, unload, switching delivery: removes every registration (redrawing if asked) and the cache.</summary>
    public void RemoveAll(bool redraw)
    {
        var any = NeedsCleanup;   // an unsettled-only cleanup may disable a live dome too: redraw then as well
        var cleared = new HashSet<Guid>();
        foreach (var r in new[] { Shown, Pending })
            if (r != null && cleared.Add(r.Collection)) sync.ClearSynced(r.Collection);
        foreach (var id in unsettled)
            if (cleared.Add(id)) sync.ClearSynced(id);
        unsettled.Clear();
        Shown = null;
        Pending = null;
        baseCache.Clear();
        if (any && redraw) sync.RedrawPlayer();
    }

    /// <summary>The player's collection is no longer the one the shown registration lives in.</summary>
    public bool CollectionMoved() => Shown != null && sync.PlayerCollection() is { } p && p.Id != Shown.Collection;

    /// <summary>
    /// Startup or Penumbra re-init: disables the synced mod in every collection, including this session's and any
    /// unsettled one, and empties it. That settles the unsettled set.
    /// </summary>
    public void ClearEverywhere()
    {
        sync.ClearSyncedEverywhere([.. sessionGuids, .. unsettled]);
        unsettled.Clear();
    }

    /// <summary>
    /// Penumbra went away (or restarted): forget registrations and cached bases without any IPC. The real mod and its
    /// enabled state outlive Penumbra's session (unlike a temp mod), so the forgotten registrations' collections join the
    /// unsettled ones: the next hide, logout, unload or re-init cleanup still disables and empties the mod.
    /// </summary>
    public void Forget()
    {
        foreach (var r in new[] { Shown, Pending })
            if (r != null) unsettled.Add(r.Collection);
        Shown = null;
        Pending = null;
        baseCache.Clear();
    }

    private bool Restore(Registration r) => sync.SetSynced(r.Collection, r.Plan.Paths, r.Plan.Manips) == 0;

    /// <summary>
    /// After a refused registration in <paramref name="collection"/>: the one shared mod was already rewritten (and is still
    /// enabled there if a dome was live). Put back what it held before, as AbortPending does: the pending registration if
    /// there is one in this collection, else the shown one. One that can't be put back is dropped, and when nothing is left
    /// registered there the mod is taken down (disabled and emptied), so a "taken down" dome really is. True when the
    /// shown registration was lost.
    /// </summary>
    private bool RecoverAfterRefusal(Guid collection)
    {
        unsettled.Add(collection);   // a failed enable may have left the mod enabled: clean it up later too
        var shownLost = false;
        if (Pending != null && Pending.Collection == collection && !Restore(Pending))
        {
            log.Warning("[Milieu] Penumbra refused restoring the pending synced backdrop; dropping it.");
            Pending = null;
        }
        if (Pending == null && Shown != null && Shown.Collection == collection && !Restore(Shown))
        {
            log.Warning("[Milieu] Penumbra refused restoring the shown synced backdrop; taking it down.");
            baseCache.Remove((Shown.Collection, Shown.Target.Mode));
            Shown = null;
            shownLost = true;
        }
        var held = (Pending?.Collection == collection) || (Shown?.Collection == collection);
        if (!held) TakeDown(collection);
        return shownLost;
    }

    /// <summary>Disables and empties the mod in a collection; it stays unsettled (ClearSynced can't report failure).</summary>
    private void TakeDown(Guid collection)
    {
        sync.ClearSynced(collection);
        unsettled.Add(collection);
    }

    private IReadOnlyList<StatusMessage> SharedWarnings(Registration r)
    {
        var shared = !r.IndividualSet || sync.BaseCollection() == r.Collection;
        return shared ? [StatusMessage.Of(MessageKey.SyncSharedCollection, ("item", r.Target.DisplayName))] : [];
    }

    private void DropUnlessHeld((Guid, DeliveryMode) key)
    {
        if (Holds(Shown, key) || Holds(Pending, key)) return;
        baseCache.Remove(key);
    }

    private static bool Holds(Registration? r, (Guid Id, DeliveryMode Mode) key) => r != null && r.Collection == key.Id && r.Target.Mode == key.Mode;
}
