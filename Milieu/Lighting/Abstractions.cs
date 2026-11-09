using System.Numerics;
using System.Threading;

namespace Milieu.Lighting;

/// <summary>The game light shapes Milieu creates (values match the game's LightShape).</summary>
public enum LightShapeKind { Point = 2, Spot = 3 }

/// <summary>Everything Milieu writes into one game light. Colour is linear RGB (the game's Color, intensity 1).</summary>
public sealed record LightParams(LightShapeKind Shape, Vector3 Position, Quaternion Rotation, Vector3 Colour, float Range,
                                 float SpotAngleDegrees, float AngularFalloffDegrees, bool CharacterShadows);

/// <summary>A light Milieu created. Marked dead when Milieu destroyed it or found it no longer valid.</summary>
public sealed class LightHandle(nint pointer)
{
    private int dead;

    public nint Pointer { get; } = pointer;

    public bool IsDead => Volatile.Read(ref dead) != 0;

    /// <summary>True for the call that actually killed it.</summary>
    public bool MarkDead() => Interlocked.Exchange(ref dead, 1) == 0;
}

/// <summary>
/// Native game lights (docs/adr/0009). Every member is framework-thread only. Implementations never call native code
/// while <see cref="Available"/> is false.
/// </summary>
public interface ILightSpawner
{
    bool Available { get; }

    /// <summary>Creates and configures one light; null when the game didn't create a usable light.</summary>
    LightHandle? Create(LightParams p);

    /// <summary>Rewrites a light's parameters. False when the handle is found dead (no native call is made on it).</summary>
    bool Update(LightHandle h, LightParams p);

    /// <summary>The per-frame render/culling/material refresh. False when the handle is found dead.</summary>
    bool Refresh(LightHandle h);

    /// <summary>Destroys the light. False only when refused (not on the framework thread): the caller keeps the handle.</summary>
    bool Destroy(LightHandle h);
}

/// <summary>The dome as drawn: everything that decides where a texel of the image appears in the world.</summary>
public readonly record struct DomeView(float YawRadians, bool Flip, float VerticalScale, float URepeat, float VRepeat);

/// <summary>The image the drawn dome shows, and how it is drawn.</summary>
public sealed record LitBackdrop(string ImagePath, DomeView View);

/// <summary>
/// "A lighting attempt is in progress": set before lights are created, cleared once they proved stable or were removed.
/// Found set at startup, it means the game closed (crashed) mid-attempt last session.
/// </summary>
public interface ILightingSentinel
{
    bool WasSetAtStartup { get; }
    void Set();
    void Clear();
}
