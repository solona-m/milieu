using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Milieu.Sync;

/// <summary>
/// Synced delivery as a real Penumbra mod ("Milieu Sync") instead of a temporary one: the staging folder
/// (<see cref="SyncStaging"/>) gets a meta.json and a default_mod.json whose Files map each game path to the staged file and
/// whose Manipulations carry the item's IMC entries, so the mod shows in Penumbra's mod list. Pure file building; the
/// Penumbra calls (add/reload, enable, priority) live in PenumbraIpc.
/// </summary>
public static class SyncRealMod
{
    public const string ModName = "Milieu Sync";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Writes meta.json and default_mod.json into <paramref name="modFolder"/>. Every disk path must lie inside the folder
    /// (Penumbra mods only reference their own files). Returns false (writing nothing) when one doesn't or the manipulation
    /// string can't be read.
    /// </summary>
    public static bool Write(string modFolder, IReadOnlyDictionary<string, string> paths, string manips)
    {
        var root = Path.GetFullPath(modFolder);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (game, disk) in paths)
        {
            var rel = Path.GetRelativePath(root, Path.GetFullPath(disk));
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;
            files[game] = rel;
        }
        // Milieu's manipulation string is v0: base64(gzip(0 byte + the JSON array Penumbra's mod files use as Manipulations)).
        System.Text.Json.Nodes.JsonNode? manipulations;
        try
        {
            var raw = PenumbraMeta.Gunzip(Convert.FromBase64String(manips));
            if (raw == null || raw.Length < 2 || raw[0] != 0) return false;
            manipulations = System.Text.Json.Nodes.JsonNode.Parse(raw.AsSpan(1));
            if (manipulations is not System.Text.Json.Nodes.JsonArray) return false;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidDataException) { return false; }

        var meta = new Dictionary<string, object>
        {
            ["FileVersion"] = 3,
            ["Name"] = ModName,
            ["Author"] = "Milieu",
            ["Description"] = "Made by the Milieu plugin for synced backdrops (cushion / Mad Bird Ring). Milieu rewrites it; edits are lost.",
            ["Version"] = "1.0.0",
            ["Website"] = "",
            ["ModTags"] = Array.Empty<string>(),
        };
        var defaultMod = new Dictionary<string, object?>
        {
            ["Version"] = 0,
            ["Files"] = files,
            ["FileSwaps"] = new Dictionary<string, string>(),
            ["Manipulations"] = manipulations,
        };
        File.WriteAllText(Path.Combine(root, "meta.json"), JsonSerializer.Serialize(meta, Json));
        File.WriteAllText(Path.Combine(root, "default_mod.json"), JsonSerializer.Serialize(defaultMod, Json));
        return true;
    }
}
