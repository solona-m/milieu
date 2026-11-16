using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class SyncModRegistrarTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeSyncModApi api = new();
    private readonly FakeLog log = new();
    private readonly Guid c = Guid.NewGuid();
    private string? modDir;
    private readonly SyncModRegistrar r;

    public SyncModRegistrarTests()
    {
        modDir = dir.Path;
        Directory.CreateDirectory(Path.Combine(dir.Path, "_milieu_sync"));
        r = new SyncModRegistrar(api, () => modDir, log);
    }

    public void Dispose() => dir.Dispose();

    private string Folder => Path.Combine(dir.Path, "_milieu_sync");
    private Dictionary<string, string> Paths() => new() { ["chara/x/vm0001.avfx"] = Path.Combine(Folder, "dome.avfx") };
    private static string Manips() => PenumbraMeta.EncodeImcV0(SyncTarget.Cushion, [((byte)1, SyncFixtures.Entry(0, 1, 1))]);

    private int FilesInMod()
    {
        using var j = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "default_mod.json")));
        return j.RootElement.GetProperty("Files").EnumerateObject().Count();
    }

    [Fact] // [AC-synced-lighting-8]
    public void Enable_Order_Reload_Priority_Enable_Legacy()
    {
        Assert.Equal(0, r.Enable(c, Paths(), Manips()));
        Assert.Equal(["Reload _milieu_sync Milieu Sync", $"Priority {c} 1000000", $"Enabled {c} True", $"Legacy {c}"], api.Calls);
        Assert.Equal(1, FilesInMod());
    }

    [Fact] // [EDGE-synced-lighting-11]
    public void Enable_UnknownMod_AddsIt_BothFailing_ReturnsCodeEnablesNothing()
    {
        api.ReloadResult = 3;
        Assert.Equal(0, r.Enable(c, Paths(), Manips()));
        Assert.Contains("Add _milieu_sync", api.Calls);
        api.Calls.Clear();
        api.AddResult = 5;
        Assert.Equal(5, r.Enable(c, Paths(), Manips()));
        Assert.DoesNotContain(api.Calls, x => x.StartsWith("Enabled") || x.StartsWith("Priority"));
    }

    [Fact] // [EDGE-synced-lighting-12] [EDGE-synced-lighting-37]
    public void Enable_PriorityFails_EnablesNothing_EnableFails_DisablesAgain()
    {
        api.PriorityResult = 4;
        Assert.Equal(4, r.Enable(c, Paths(), Manips()));
        Assert.DoesNotContain(api.Calls, x => x.StartsWith("Enabled"));
        api.Calls.Clear();
        api.PriorityResult = 0;
        api.EnableResult = 6;
        Assert.Equal(6, r.Enable(c, Paths(), Manips()));
        Assert.Equal([$"Enabled {c} True", $"Enabled {c} False"], api.Calls.Where(x => x.StartsWith("Enabled")));
        Assert.DoesNotContain(api.Calls, x => x.StartsWith("Legacy"));
    }

    [Fact] // [EDGE-synced-lighting-12]
    public void Enable_NothingChangedCounts_AsSuccess()
    {
        api.PriorityResult = 1;
        api.EnableResult = 1;
        Assert.Equal(0, r.Enable(c, Paths(), Manips()));
    }

    [Fact] // [EDGE-synced-lighting-13]
    public void Enable_WriteFails_ReturnsMinusOne_NoIpc()
    {
        Assert.Equal(-1, r.Enable(c, new Dictionary<string, string> { ["a"] = dir.File("outside.avfx") }, Manips()));
        modDir = null;
        Assert.Equal(-1, r.Enable(c, Paths(), Manips()));
        Assert.Empty(api.Calls);
    }

    [Fact] // [AC-synced-lighting-8] [EDGE-synced-lighting-36] [EDGE-synced-lighting-51]
    public void Clear_DisablesRemovesLegacyAndEmptiesTheMod()
    {
        r.Enable(c, Paths(), Manips());
        api.Calls.Clear();
        Assert.True(r.Clear(c));
        Assert.Equal([$"Enabled {c} False", $"Legacy {c}", "Reload _milieu_sync Milieu Sync"], api.Calls);
        Assert.Equal(0, FilesInMod());
        api.DisableResult = 7;
        Assert.False(r.Clear(c));   // reported, so the caller keeps the collection to retry
    }

    [Fact] // [EDGE-synced-lighting-14] [EDGE-synced-lighting-45]
    public void DisableEverywhere_OnlyWhenTheModExists_AlwaysRemovesLegacy()
    {
        var other = Guid.NewGuid();
        api.KnownCollections = [other];
        r.DisableEverywhere([c]);
        Assert.DoesNotContain(api.Calls, x => x.StartsWith("Enabled"));   // never written: nothing to disable, no folder made
        Assert.Contains($"Legacy {c}", api.Calls);
        Assert.Contains($"Legacy {other}", api.Calls);
        Assert.False(File.Exists(Path.Combine(Folder, "meta.json")));

        r.Enable(c, Paths(), Manips());
        api.Calls.Clear();
        r.DisableEverywhere([c]);
        Assert.Contains($"Enabled {c} False", api.Calls);
        Assert.Contains($"Enabled {other} False", api.Calls);
        Assert.Equal(0, FilesInMod());
    }

    [Fact] // [EDGE-synced-lighting-36] [EDGE-synced-lighting-45]
    public void Neutralize_PenumbraMissing_WritesFileNoIpc_NoModDirOrMeta_Skipped()
    {
        r.Enable(c, Paths(), Manips());
        api.Calls.Clear();
        api.PenumbraPresent = false;
        r.Neutralize();
        Assert.Empty(api.Calls);
        Assert.Equal(0, FilesInMod());
        File.Delete(Path.Combine(Folder, "meta.json"));
        File.Delete(Path.Combine(Folder, "default_mod.json"));
        r.Neutralize();
        Assert.False(File.Exists(Path.Combine(Folder, "default_mod.json")));
        modDir = null;
        r.Neutralize();   // no throw
    }
}
