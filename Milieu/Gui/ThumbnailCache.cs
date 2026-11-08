using System;
using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Milieu.Backdrop;

namespace Milieu.Gui;

/// <summary>
/// Preview textures. Bundled thumbnails are embedded JPEGs loaded through Dalamud's shared texture cache (never
/// disposed here). User thumbnails are raw RGBA from <see cref="ThumbStore"/>, turned into textures this cache owns and
/// disposes when they change, are removed, or the window goes away.
/// </summary>
public sealed class ThumbnailCache(ITextureProvider textures, ThumbStore store) : IDisposable
{
    private readonly Dictionary<string, (string Key, IDalamudTextureWrap Wrap)> owned = new(StringComparer.OrdinalIgnoreCase);

    public IDalamudTextureWrap? Get(Tile tile)
    {
        if (tile.ThumbKey == null) return null;
        if (tile.Kind == TileKind.Bundled)
            return textures.GetFromManifestResource(typeof(Plugin).Assembly, tile.ThumbKey).GetWrapOrDefault();

        var key = ThumbStore.Key(tile.ThumbKey);
        if (key == null) { Drop(tile.ThumbKey); return null; }
        if (owned.TryGetValue(tile.ThumbKey, out var hit) && hit.Key == key) return hit.Wrap;
        Drop(tile.ThumbKey);
        if (store.TryLoad(tile.ThumbKey) is not { } rgba) return null;
        var wrap = textures.CreateFromRaw(RawImageSpecification.Rgba32(ThumbStore.Width, ThumbStore.Height), rgba, "Milieu thumbnail");
        owned[tile.ThumbKey] = (key, wrap);
        return wrap;
    }

    /// <summary>Disposes textures of images no longer in the grid.</summary>
    public void Retain(IEnumerable<string> userImages)
    {
        var keep = new HashSet<string>(userImages, StringComparer.OrdinalIgnoreCase);
        foreach (var k in new List<string>(owned.Keys)) if (!keep.Contains(k)) Drop(k);
    }

    private void Drop(string path)
    {
        if (!owned.Remove(path, out var e)) return;
        e.Wrap.Dispose();
    }

    public void Dispose()
    {
        foreach (var (_, w) in owned.Values) w.Dispose();
        owned.Clear();
    }
}
