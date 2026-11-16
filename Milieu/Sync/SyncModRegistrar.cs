using System;
using System.Collections.Generic;
using System.IO;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>
/// The Penumbra mod-management calls synced delivery makes on Milieu's own "Milieu Sync" mod, and nothing else (docs/adr/0006).
/// Implemented by PenumbraIpc as thin wrappers; every call returns Penumbra's code (0 Success, 1 NothingChanged) or -1 when the
/// IPC throws.
/// </summary>
public interface ISyncModApi
{
    /// <summary>Penumbra answers IPC (also with mods disabled or no Base collection); false when it is missing or incompatible.</summary>
    bool PenumbraPresent { get; }
    int ReloadMod(string dir, string name);
    int AddMod(string dir);
    int SetModPriority(Guid collection, string dir, string name, int priority);
    int SetModEnabled(Guid collection, string dir, string name, bool enabled);
    void RemoveLegacyTempMod(Guid collection);
    IReadOnlyCollection<Guid> Collections();
}

/// <summary>
/// Registers synced delivery as the real "Milieu Sync" mod and takes it down again. Order on enable: write the files →
/// ReloadMod (AddMod when Penumbra doesn't know the mod) → priority → enable → remove an older build's temp mod. A failed
/// priority enables nothing; a failed enable is disabled again (fail closed). At rest the mod is emptied (<see cref="Neutralize"/>),
/// so an enabled state left behind changes nothing.
/// </summary>
public sealed class SyncModRegistrar(ISyncModApi api, Func<string?> modDirectory, ILogSink log)
{
    public const string Dir = SyncStaging.FolderName;
    public const string Name = SyncRealMod.ModName;
    public const int Priority = 1_000_000;

    /// <summary>Writes, adds and enables the mod in <paramref name="collection"/>. 0 on success, else Penumbra's code or -1.</summary>
    public int Enable(Guid collection, IReadOnlyDictionary<string, string> paths, string manips)
    {
        if (modDirectory() is not { } dir) return -1;
        try
        {
            if (!SyncRealMod.Write(Path.Combine(dir, Dir), paths, manips))
            {
                log.Warning("[Milieu] Could not write the Milieu Sync mod files.");
                return -1;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning("[Milieu] Could not write the Milieu Sync mod files: " + e.GetType().Name);
            return -1;
        }
        var ec = api.ReloadMod(Dir, Name);
        if (ec != 0) ec = api.AddMod(Dir);
        if (ec != 0)
        {
            log.Warning($"[Milieu] Penumbra could not add the Milieu Sync mod (code {ec}).");
            return ec;
        }
        var prio = api.SetModPriority(collection, Dir, Name, Priority);
        if (prio is not (0 or 1))
        {
            log.Warning($"[Milieu] Penumbra could not set the Milieu Sync priority (code {prio}); not enabled.");
            return prio;
        }
        var on = api.SetModEnabled(collection, Dir, Name, true);
        if (on is not (0 or 1))
        {
            log.Warning($"[Milieu] Penumbra could not enable the Milieu Sync mod (code {on}); disabling it again.");
            api.SetModEnabled(collection, Dir, Name, false);
            return on;
        }
        api.RemoveLegacyTempMod(collection);
        log.Information("[Milieu] Milieu Sync mod enabled.");
        return 0;
    }

    /// <summary>
    /// Disables the mod in <paramref name="collection"/> and removes an older build's temp mod there. True when disabled.
    /// No IPC while Penumbra is missing (false: still to do).
    /// </summary>
    public bool Disable(Guid collection)
    {
        if (!api.PenumbraPresent) return false;
        var off = api.SetModEnabled(collection, Dir, Name, false);
        api.RemoveLegacyTempMod(collection);
        return off is 0 or 1;
    }

    /// <summary>Disable + Neutralize: what hiding a synced backdrop does in one collection. True when disabled.</summary>
    public bool Clear(Guid collection)
    {
        var off = Disable(collection);
        Neutralize();
        return off;
    }

    /// <summary>
    /// Startup / Penumbra re-init cleanup: disables the mod in every collection (only when its meta.json exists; ND-5a), removes
    /// the legacy temp mod everywhere, then empties the mod.
    /// </summary>
    public void DisableEverywhere(IEnumerable<Guid> alsoClear)
    {
        var all = new HashSet<Guid>(alsoClear);
        foreach (var id in api.Collections()) all.Add(id);
        var exists = MetaExists();
        foreach (var id in all)
        {
            if (exists) api.SetModEnabled(id, Dir, Name, false);
            api.RemoveLegacyTempMod(id);
        }
        Neutralize();
    }

    /// <summary>
    /// Rewrites the mod empty and reloads it, so a leftover enabled state does nothing. Skipped when the mod directory is
    /// unknown or the mod was never written; no IPC when Penumbra is missing. Never throws.
    /// </summary>
    public void Neutralize()
    {
        if (modDirectory() is not { } dir || !File.Exists(Path.Combine(dir, Dir, SyncRealMod.MetaFile))) return;
        try { SyncRealMod.WriteEmpty(Path.Combine(dir, Dir)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning("[Milieu] Could not empty the Milieu Sync mod: " + e.GetType().Name);
            return;
        }
        if (api.PenumbraPresent) api.ReloadMod(Dir, Name);
    }

    private bool MetaExists() => modDirectory() is { } dir && File.Exists(Path.Combine(dir, Dir, SyncRealMod.MetaFile));
}
