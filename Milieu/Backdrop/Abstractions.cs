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
    Vector3? PlayerPosition { get; }
}
