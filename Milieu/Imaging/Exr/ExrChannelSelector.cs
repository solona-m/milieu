using System;
using System.Collections.Generic;
using System.Linq;

namespace Milieu.Imaging.Exr;

/// <summary>
/// Picks the colour channels of an EXR (plan decision 6): the unprefixed RGB group, else the only prefixed RGB group, else
/// the first "Combined"/"beauty"/"RGBA" layer, else an unprefixed Y as grey. Grouping and ordering are ordinal.
/// </summary>
public static class ExrChannelSelector
{
    private static readonly string[] Matched = ["R", "G", "B", "Y", "RY", "BY"];
    private static readonly string[] MainLayers = ["Combined", "beauty", "RGBA"];

    /// <summary>Returns the indices (into the stored chlist) of R, G, B; for grey all three are the Y index.</summary>
    public static int[] Select(IReadOnlyList<ExrChannel> channels)
    {
        var groups = new SortedDictionary<string, List<(int Index, string Component)>>(StringComparer.Ordinal);
        for (var i = 0; i < channels.Count; i++)
        {
            var name = channels[i].Name;
            var dot = name.LastIndexOf('.');
            var prefix = dot < 0 ? "" : name[..dot];
            if (!groups.TryGetValue(prefix, out var list)) groups[prefix] = list = [];
            list.Add((i, dot < 0 ? name : name[(dot + 1)..]));
        }

        // A case-only duplicate of a matched component (R + r) makes the file ambiguous, even if an exact R exists.
        foreach (var g in groups.Values)
            foreach (var m in Matched)
                if (g.Count(c => string.Equals(c.Component, m, StringComparison.OrdinalIgnoreCase)) > 1)
                    throw ExrHeader.Layout("several RGB layers and no main one");

        var rgbGroups = groups.Where(g => Rgb(g.Value) != null).ToList();

        if (groups.TryGetValue("", out var plain) && Rgb(plain) is { } main) return main;
        if (rgbGroups.Count == 1) return Rgb(rgbGroups[0].Value)!;
        foreach (var g in rgbGroups)
        {
            var dot = g.Key.LastIndexOf('.');
            var last = dot < 0 ? g.Key : g.Key[(dot + 1)..];
            if (MainLayers.Any(l => string.Equals(l, last, StringComparison.OrdinalIgnoreCase))) return Rgb(g.Value)!;
        }

        var hasChroma = groups.Values.Any(g => Find(g, "RY") >= 0 || Find(g, "BY") >= 0);
        if (plain != null && !hasChroma && Find(plain, "Y") is var y and >= 0) return [y, y, y];
        if (hasChroma) throw ExrHeader.Layout("luminance/chroma channels");
        if (rgbGroups.Count > 1) throw ExrHeader.Layout("several RGB layers and no main one");
        throw ExrHeader.Layout("no R, G, B channels");
    }

    private static int[]? Rgb(List<(int Index, string Component)> g)
    {
        int r = Find(g, "R"), gr = Find(g, "G"), b = Find(g, "B");
        return r >= 0 && gr >= 0 && b >= 0 ? [r, gr, b] : null;
    }

    /// <summary>Exact-case match first, then case-insensitive (case-only duplicates were already rejected).</summary>
    private static int Find(List<(int Index, string Component)> g, string component)
    {
        foreach (var c in g) if (string.Equals(c.Component, component, StringComparison.Ordinal)) return c.Index;
        foreach (var c in g) if (string.Equals(c.Component, component, StringComparison.OrdinalIgnoreCase)) return c.Index;
        return -1;
    }
}
