using System;

namespace Milieu.Interop;

/// <summary>Names where a native signature came from, so a broken one can be traced after a game patch.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SourceAttribute(string source) : Attribute
{
    public string Source { get; } = source;
}

/// <summary>
/// Every native signature Milieu scans for, in one place. If any is not found the VFX feature disables itself
/// (InteropUnavailable) and nothing native is ever called. The create function comes from FFXIVClientStructs
/// (<c>VfxObject.Addresses.Create</c>), which is kept current with the game by its maintainers. Scene lighting
/// (docs/adr/0009) likewise scans FFXIVClientStructs' <c>Scene.Light.Addresses.Create</c> (in <c>LightInterop</c>); it has
/// no constant here because Dalamud's bundled ClientStructs provides it. Sun off (docs/adr/0013) hooks the game's environment
/// update, a signature Brio maintains (<see cref="UpdateEnvironment"/>).
/// </summary>
public static class Signatures
{
    /// <summary>Starts a created static VFX playing. <c>ISigScanner.ScanText</c> follows the E8 call itself.</summary>
    [Source("VFXEditor Interop/Constants.cs StaticVfxRunSig")]
    public const string StaticVfxRun = "E8 ?? ?? ?? ?? B0 02 EB 02";

    /// <summary>Removes (frees) a static VFX. Hooked so Milieu notices when the game frees its dome.</summary>
    [Source("VFXEditor Interop/Constants.cs StaticVfxRemoveSig")]
    public const string StaticVfxRemove =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";

    /// <summary>The pool name static VFX are created in (VFXEditor Interop/Structs/Vfx/StaticVfx.cs).</summary>
    [Source("VFXEditor Interop/Structs/Vfx/StaticVfx.cs pool name")]
    public const string StaticVfxPool = "Client.System.Scheduler.Instance.VfxObject";

    /// <summary>
    /// The game's environment update, <c>UpdateEnvironment(EnvState* a1, EnvState* a2)</c>: a general EnvState copy (it copies
    /// a2 into a1), hooked by sun off (docs/adr/0013). Scanned with <c>ScanAllText</c>: it must match exactly once.
    /// </summary>
    [Source("Brio Game/World/EnvironmentService.cs UpdateEnvironment")]
    public const string UpdateEnvironment = "0F 10 42 08 0F 11 41 08 F2 0F 10 4A 18";
}
