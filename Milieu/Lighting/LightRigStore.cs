using System;
using System.IO;
using System.Linq;
using Milieu.Backdrop;

namespace Milieu.Lighting;

/// <summary>
/// Light rigs cached as JSON in <c>&lt;config&gt;/cache/lights/lights_&lt;key&gt;.json</c> (the format is in
/// LightRigStore.Json.cs). Saves are atomic. A file that fails validation (see <see cref="Parse"/>, including an
/// estimated flag that doesn't match what the caller asked for) is a miss: it is deleted and the caller extracts again.
/// </summary>
public sealed partial class LightRigStore(CacheStore cache)
{
    public const int DefaultKeep = 100;

    /// <summary>The rig under <paramref name="key"/>, or null. <paramref name="estimated"/> must match the file's flag.</summary>
    public LightRig? Load(string key, bool estimated = false)
    {
        var path = cache.LightRigPath(key);
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var rig = Parse(text, estimated);
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
}
