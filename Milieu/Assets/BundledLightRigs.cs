using System;
using System.Collections.Generic;
using Milieu.Lighting;

namespace Milieu.Assets;

/// <summary>
/// One bundled scene's precomputed light estimate (docs/adr/0010): the rig JSON, tied to the committed source JPEG's
/// SHA-256 and to the scene's .atex pin SHA-256.
/// </summary>
public sealed record BundledRigEntry(string Stem, string JpegSha256, string PinSha256, string Json);

/// <summary>
/// The bundled scenes' light rigs, compiled into the plugin: nothing is downloaded, decoded or opened for them. The
/// table (<see cref="All"/>) is generated into BundledLightRigs.Data.cs by
/// <c>PrepareBackdrops rigs Milieu/DefaultBackdrops Milieu/Assets/BundledLightRigs.Data.cs</c>; regenerate it whenever
/// a bundled image, its pin or <see cref="LdrLightEstimator"/>/<see cref="LightExtractor"/> changes.
/// </summary>
public static partial class BundledLightRigs
{
    /// <summary>The scene's rig, or null when there is no entry, its pin changed, or the rig is not a valid current estimate.</summary>
    public static LightRig? Find(string stem) => Find(stem, All, BackdropPins.Find);

    internal static LightRig? Find(string stem, IReadOnlyList<BundledRigEntry> entries, Func<string, BackdropPin?> pins)
    {
        BundledRigEntry? entry = null;
        foreach (var e in entries)
            if (string.Equals(e.Stem, stem, StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
        if (entry == null || pins(stem) is not { } pin) return null;
        if (!string.Equals(entry.PinSha256, pin.Sha256, StringComparison.Ordinal)) return null;
        return LightRigStore.Parse(entry.Json, estimated: true);
    }
}
