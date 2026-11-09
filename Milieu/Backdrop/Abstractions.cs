using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Milieu.Backdrop;

/// <summary>A spawned dome VFX. Marked dead (thread-safely) when Milieu removes it or the game frees it.</summary>
public sealed class VfxHandle(nint pointer, string gamePath, DateTime spawnedAtUtc)
{
    private int dead;

    public nint Pointer { get; } = pointer;
    public string GamePath { get; } = gamePath;
    public DateTime SpawnedAtUtc { get; } = spawnedAtUtc;

    public bool IsDead => Volatile.Read(ref dead) != 0;

    /// <summary>Returns true for the call that actually killed it (so a removal happens exactly once).</summary>
    public bool MarkDead() => Interlocked.Exchange(ref dead, 1) == 0;
}

/// <summary>Static VFX spawning (native). Every member is framework-thread only, except the event.</summary>
public interface IVfxSpawner
{
    bool Available { get; }
    VfxHandle? Spawn(string gamePath, Vector3 position, float yawRadians);
    void Move(VfxHandle handle, Vector3 position, float yawRadians);
    /// <summary>Removes the VFX. False when the call was refused (wrong thread), so the caller keeps the handle.</summary>
    bool Remove(VfxHandle handle);

    /// <summary>Called once per framework tick: frees native strings whose grace period has passed.</summary>
    void Tick();

    /// <summary>The game freed one of Milieu's VFX by itself. May fire on any thread; the handle is already dead.</summary>
    event Action<VfxHandle>? GameRemoved;
}

public enum PenumbraState { Available, Missing, Incompatible, ModsDisabled, NoBaseCollection }

/// <summary>Milieu's Penumbra temporary mod (tag "Milieu", priority 0, all collections).</summary>
public interface IPenumbraRedirects
{
    PenumbraState State { get; }
    void Refresh();
    bool Set(IReadOnlyDictionary<string, string> redirects);
    void Clear();
    string Resolve(string gamePath);
}

public interface IGameState
{
    bool IsLoggedIn { get; }

    /// <summary>Logged in, local player present, not between areas, not in a cutscene. GPose does not matter.</summary>
    bool IsReady { get; }

    bool BetweenAreas { get; }

    /// <summary>The real local player's position (object 0), not a GPose actor copy.</summary>
    Vector3? PlayerPosition { get; }

    /// <summary>In GPose (scene lighting is GPose-only).</summary>
    bool IsGPosing { get; }
}

/// <summary>The player's effective Penumbra collection and whether it is assigned to the character individually.</summary>
public readonly record struct PlayerCollectionInfo(Guid Id, bool IndividualSet);

/// <summary>
/// Synced delivery's Penumbra calls: one temporary mod (tag "MilieuSync", priority 1,000,000) in the local player's
/// collection only, carrying the IMC VFX id and the redirects. Every member swallows IPC exceptions.
/// </summary>
public interface IPenumbraSync
{
    /// <summary>Penumbra's mod directory as of the last refresh; null when unset or not an existing directory.</summary>
    string? ModDirectory { get; }

    /// <summary>The local player's effective collection; null when there is no valid player object or the Guid is empty.</summary>
    PlayerCollectionInfo? PlayerCollection();

    /// <summary>The Base (default) collection's Guid; null when unset.</summary>
    Guid? BaseCollection();

    /// <summary>Adds or replaces the synced temp mod in one collection. Penumbra's code (0 = success), or -1 when the call throws.</summary>
    int SetSynced(Guid collection, IReadOnlyDictionary<string, string> paths, string manips);

    void ClearSynced(Guid collection);

    /// <summary>Removes the synced temp mod from every listed collection and from <paramref name="alsoClear"/>.</summary>
    void ClearSyncedEverywhere(IEnumerable<Guid> alsoClear);

    string ResolvePlayer(string gamePath);

    /// <summary>The player collection's meta manipulations (Penumbra's v1 string); "" on failure.</summary>
    string PlayerMeta();

    void RedrawPlayer();
}

/// <summary>Read access to the game's own files (vanilla, before any mod).</summary>
public interface IGameData
{
    byte[]? ReadVanilla(string gamePath);
}
