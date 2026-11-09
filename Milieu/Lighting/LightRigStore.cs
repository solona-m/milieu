using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Milieu.Backdrop;

namespace Milieu.Lighting;

/// <summary>
/// Light rigs cached as JSON in <c>&lt;config&gt;/cache/lights/lights_&lt;key&gt;.json</c>, shaped
/// <c>{ "version": n, "key": {s,t,r,g,b} | null, "regions": [4 × {s,t,r,g,b}], "total": {r,g,b} }</c>. Saves are atomic.
/// A file that fails validation (wrong version, S/T outside [0,1], non-finite or negative energy, not exactly 4 regions,
/// unparsable) is a miss: it is deleted and the caller extracts again.
/// </summary>
public sealed class LightRigStore(CacheStore cache)
{
    public const int DefaultKeep = 100;

    public LightRig? Load(string key)
    {
        var path = cache.LightRigPath(key);
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var rig = Parse(text);
        if (rig == null) CacheStore.TryDelete(path);
        return rig;
    }

    public void Save(string key, LightRig rig)
    {
        var path = cache.LightRigPath(key);
        var text = Serialise(rig);
        cache.WriteAtomic(path, s => { using var w = new StreamWriter(s); w.Write(text); });
    }

    /// <summary>Keeps the newest <paramref name="keep"/> rig files; touches only <c>lights_*.json</c> in the rig folder.</summary>
    public int TrimRigs(int keep = DefaultKeep)
    {
        var dir = cache.LightRigDir;
        if (!Directory.Exists(dir)) return 0;
        var files = new DirectoryInfo(dir).EnumerateFiles("lights_*.json", SearchOption.TopDirectoryOnly)
                                          .OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
                                          .ToList();
        var deleted = 0;
        foreach (var f in files.Skip(Math.Max(0, keep)))
            if (CacheStore.TryDelete(f.FullName)) deleted++;
        return deleted;
    }

    public static string Serialise(LightRig rig)
    {
        static JsonObject Light(RigLight l) => new()
        {
            ["s"] = l.S, ["t"] = l.T, ["r"] = l.Energy.X, ["g"] = l.Energy.Y, ["b"] = l.Energy.Z,
        };
        var root = new JsonObject
        {
            ["version"] = rig.Version,
            ["key"] = rig.Key == null ? null : Light(rig.Key),
            ["regions"] = new JsonArray(rig.Regions.Select(r => (JsonNode)Light(r)).ToArray()),
            ["total"] = new JsonObject { ["r"] = rig.TotalEnergy.X, ["g"] = rig.TotalEnergy.Y, ["b"] = rig.TotalEnergy.Z },
        };
        return root.ToJsonString();
    }

    /// <summary>The rig in the text, or null if it is not a valid current-version rig.</summary>
    public static LightRig? Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root) return null;
            if (Int(root["version"]) != LightExtractor.Version) return null;
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
            return new LightRig(LightExtractor.Version, key, regions, e);
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
