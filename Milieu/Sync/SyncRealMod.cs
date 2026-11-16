using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Milieu.Sync;

/// <summary>
/// Synced delivery as a real Penumbra mod, "Milieu Sync" (owner's choice, 2026-11-16; docs/adr/0006): the staging folder
/// (<see cref="SyncStaging"/>) gets a meta.json and a default_mod.json whose Files map each game path to the staged file and
/// whose Manipulations carry the item's IMC entries, so the mod shows in Penumbra's mod list. Milieu writes the v3 layout
/// (meta.json + default_mod.json); Penumbra migrates it to v4 on load (seen in game), which is why every write rewrites
/// both files. Pure file building: the Penumbra calls live in <see cref="SyncModRegistrar"/>.
/// </summary>
public static class SyncRealMod
{
    public const string ModName = "Milieu Sync";
    public const string MetaFile = "meta.json";
    public const string DefaultModFile = "default_mod.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Writes meta.json and default_mod.json into <paramref name="modFolder"/>, each atomically. Every disk path must lie
    /// inside the folder (Penumbra mods only reference their own files). Returns false, writing nothing, when one doesn't or
    /// the manipulation string can't be read. IO errors propagate.
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
        if (DecodeManipulations(manips) is not { } manipulations) return false;
        WriteBoth(root, files, manipulations);
        return true;
    }

    /// <summary>
    /// Rewrites the mod with no files and no manipulations, so an enabled state left behind (a crash, a failed disable, an
    /// inheriting collection) changes nothing even if Milieu never runs again. IO errors propagate.
    /// </summary>
    public static void WriteEmpty(string modFolder) =>
        WriteBoth(Path.GetFullPath(modFolder), new SortedDictionary<string, string>(StringComparer.Ordinal), new JsonArray());

    /// <summary>Milieu's manipulation string is v0: base64(gzip(0 byte + the JSON array mod files use as Manipulations)).</summary>
    internal static JsonArray? DecodeManipulations(string manips)
    {
        try
        {
            var raw = PenumbraMeta.Gunzip(Convert.FromBase64String(manips));
            if (raw == null || raw.Length < 2 || raw[0] != 0) return null;
            return JsonNode.Parse(raw.AsSpan(1)) as JsonArray;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidDataException) { return null; }
    }

    private static void WriteBoth(string root, SortedDictionary<string, string> files, JsonArray manipulations)
    {
        var meta = new Dictionary<string, object>
        {
            ["FileVersion"] = 3,
            ["Name"] = ModName,
            ["Author"] = "Milieu",
            ["Description"] = "Made by the Milieu plugin for synced backdrops (cushion / Mad Bird Ring). Milieu rewrites it; edits are lost. Empty while no synced backdrop is shown.",
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
        Directory.CreateDirectory(root);
        WriteAtomic(Path.Combine(root, MetaFile), JsonSerializer.Serialize(meta, Json));
        WriteAtomic(Path.Combine(root, DefaultModFile), JsonSerializer.Serialize(defaultMod, Json));
    }

    /// <summary>Temp file + rename, so Penumbra never reads a half-written file.</summary>
    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
