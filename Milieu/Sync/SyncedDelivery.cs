using System;
using System.Collections.Generic;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>
/// The synced registration lifecycle over <see cref="IPenumbraSync"/>: one "MilieuSync" temp mod in the local player's
/// collection holding the target's IMC VFX id and the dome redirects. Framework thread only.
/// <list type="bullet">
/// <item>The user's IMC base entries are read once per (collection, target) and cached only while a shown or pending
/// registration holds that key, so Milieu never reads its own entry back as a user override; every path that leaves
/// nothing registered drops the entry.</item>
/// <item>The last VFX id chosen per target this session is remembered, so a stale Milieu entry seen on a re-read (a
/// removal Penumbra applied late) is ignored rather than taken for the user's.</item>
/// <item>Every collection registered this session is remembered, so cleanup reaches temporary or deleted ones too.</item>
/// </list>
/// </summary>
public sealed class SyncedDelivery(IPenumbraSync sync, IGameData gameData, ILogSink log)
{
    public sealed record Registration(Guid Collection, bool IndividualSet, SyncTarget Target, SyncPlan Plan);

    private readonly Dictionary<(Guid, DeliveryMode), SyncBase> baseCache = [];
    private readonly Dictionary<DeliveryMode, byte> lastVfxId = [];
    private readonly HashSet<Guid> sessionGuids = [];

    public Registration? Shown { get; private set; }

    public Registration? Pending { get; private set; }

    public bool HasRegistration => Shown != null || Pending != null;

    public IReadOnlyCollection<Guid> SessionGuids => sessionGuids;

    /// <summary>Plans and registers the synced temp mod for the staged files. Returns the failure, or None (then Pending is set).</summary>
    public (MessageKey Failure, StatusMessage? Message) Begin(PreparedTexture staged, string avfxDisk, SyncTarget target)
    {
        if (sync.PlayerCollection() is not { } player) return (MessageKey.SyncNoPlayerCollection, StatusMessage.Of(MessageKey.SyncNoPlayerCollection));
        var key = (player.Id, target.Mode);
        if (!baseCache.TryGetValue(key, out var b))
        {
            byte? ignore = lastVfxId.TryGetValue(target.Mode, out var last) ? last : null;
            var (resolved, failure) = SyncPlanner.ResolveBase(target, gameData.ReadVanilla(target.ImcPath), sync.PlayerMeta(), ignore);
            if (resolved == null) return (failure, StatusMessage.Of(failure, ("item", target.DisplayName)));
            b = resolved;
            baseCache[key] = b;
            if (b.MetaUnreadable) log.Debug("[Milieu] Couldn't read the player's meta manipulations; using the game's IMC entries.");
        }
        var (plan, buildFailure) = SyncPlanner.Build(b, staged.GamePath, staged.DiskPath, avfxDisk);
        if (plan == null)
        {
            DropUnlessHeld(key);
            return (buildFailure, StatusMessage.Of(buildFailure, ("item", target.DisplayName)));
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
            DropUnlessHeld(key);
            if (clearedOld) sync.RedrawPlayer();   // the item still carried the old dome: reload it without
            return (MessageKey.SyncRefused, StatusMessage.Of(MessageKey.SyncRefused, ("code", code)));
        }
        Pending = new Registration(player.Id, player.IndividualSet, target, plan);
        lastVfxId[target.Mode] = plan.VfxId;
        return (MessageKey.None, null);
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

    /// <summary>The pending registration didn't resolve: take it out and put the shown one (if any) back.</summary>
    public void AbortPending()
    {
        if (Pending == null) return;
        var p = Pending;
        Pending = null;
        sync.ClearSynced(p.Collection);
        if (Shown == null) sync.RedrawPlayer();   // nothing to put back: reload the item without the pending dome
        else if (sync.SetSynced(Shown.Collection, Shown.Plan.Paths, Shown.Plan.Manips) != 0)
            log.Warning("[Milieu] Penumbra refused restoring the shown synced backdrop.");
        DropUnlessHeld((p.Collection, p.Target.Mode));
    }

    /// <summary>Hide, logout, unload, switching delivery: removes every registration (redrawing if asked) and the cache.</summary>
    public void RemoveAll(bool redraw)
    {
        var any = HasRegistration;
        var cleared = new HashSet<Guid>();
        foreach (var r in new[] { Shown, Pending })
            if (r != null && cleared.Add(r.Collection)) sync.ClearSynced(r.Collection);
        Shown = null;
        Pending = null;
        baseCache.Clear();
        if (any && redraw) sync.RedrawPlayer();
    }

    /// <summary>The player's collection is no longer the one the shown registration lives in.</summary>
    public bool CollectionMoved() => Shown != null && sync.PlayerCollection() is { } p && p.Id != Shown.Collection;

    /// <summary>Startup or Penumbra re-init: removes the synced temp mod from every collection, including this session's.</summary>
    public void ClearEverywhere() => sync.ClearSyncedEverywhere(sessionGuids);

    /// <summary>Penumbra went away (or restarted): forget registrations and cached bases without any IPC.</summary>
    public void Forget()
    {
        Shown = null;
        Pending = null;
        baseCache.Clear();
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
