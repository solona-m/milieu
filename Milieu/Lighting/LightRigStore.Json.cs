using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Milieu.Lighting;

/// <summary>
/// The rig JSON (pure; also linked into tools/PrepareBackdrops for the bundled rigs):
/// <c>{ "version": n, "estimated"?: m, "key": {s,t,r,g,b} | null, "lamps"?: [≤ 3 × {s,t,r,g,b}], "regions": [4 × {s,t,r,g,b}], "total": {r,g,b} }</c>.
/// "estimated" and "lamps" are written only for an estimated rig (docs/adr/0010).
/// </summary>
public sealed partial class LightRigStore
{
    public static string Serialise(LightRig rig)
    {
        static JsonObject Light(RigLight l) => new()
        {
            ["s"] = l.S, ["t"] = l.T, ["r"] = l.Energy.X, ["g"] = l.Energy.Y, ["b"] = l.Energy.Z,
        };
        var root = new JsonObject { ["version"] = rig.Version };
        if (rig.IsEstimated) root["estimated"] = rig.EstimatorVersion;
        root["key"] = rig.Key == null ? null : Light(rig.Key);
        if (rig.IsEstimated) root["lamps"] = new JsonArray(rig.Lamps.Select(l => (JsonNode)Light(l)).ToArray());
        root["regions"] = new JsonArray(rig.Regions.Select(r => (JsonNode)Light(r)).ToArray());
        root["total"] = new JsonObject { ["r"] = rig.TotalEnergy.X, ["g"] = rig.TotalEnergy.Y, ["b"] = rig.TotalEnergy.Z };
        return root.ToJsonString();
    }

    /// <summary>
    /// The rig in the text, or null if it is not a valid current-version rig of the asked kind: "version" must be
    /// <see cref="LightExtractor.Version"/>; an estimated rig must carry "estimated" = <see cref="LdrLightEstimator.Version"/>
    /// and at most 3 lamps, an HDR rig neither field; S/T in [0,1], finite non-negative energies, exactly 4 regions.
    /// </summary>
    public static LightRig? Parse(string text, bool estimated = false)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root) return null;
            if (Int(root["version"]) != LightExtractor.Version) return null;
            var lamps = Array.Empty<RigLight>();
            if (estimated)
            {
                if (Int(root["estimated"]) != LdrLightEstimator.Version) return null;
                if (root["lamps"] is { } lampsNode)
                {
                    if (lampsNode is not JsonArray la || la.Count > LightRig.MaxLamps) return null;
                    lamps = new RigLight[la.Count];
                    for (var i = 0; i < lamps.Length; i++)
                    {
                        if (la[i] is not { } node || Light(node) is not { } l) return null;
                        lamps[i] = l;
                    }
                }
            }
            else if (root.ContainsKey("estimated") || root.ContainsKey("lamps")) return null;
            RigLight? key = null;
            if (root["key"] is { } keyNode)
            {
                key = Light(keyNode);
                if (key == null) return null;
            }
            if (root["regions"] is not JsonArray arr || arr.Count != LightRig.RegionCount) return null;
            var regions = new RigLight[LightRig.RegionCount];
            for (var i = 0; i < regions.Length; i++)
            {
                if (arr[i] is not { } node || Light(node) is not { } l) return null;
                regions[i] = l;
            }
            if (root["total"] is not JsonObject total || Energy(total) is not { } e) return null;
            return new LightRig(LightExtractor.Version, key, regions, e, lamps, estimated ? LdrLightEstimator.Version : 0);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static float? Float(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (float)d : null;

    private static Vector3? Energy(JsonObject o)
    {
        if (Float(o["r"]) is not { } r || Float(o["g"]) is not { } g || Float(o["b"]) is not { } b) return null;
        if (!(r >= 0 && g >= 0 && b >= 0) || !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b)) return null;
        return new Vector3(r, g, b);
    }

    private static RigLight? Light(JsonNode node)
    {
        if (node is not JsonObject o || Float(o["s"]) is not { } s || Float(o["t"]) is not { } t) return null;
        if (!(s >= 0 && s <= 1 && t >= 0 && t <= 1)) return null;
        return Energy(o) is { } e ? new RigLight(s, t, e) : null;
    }
}
