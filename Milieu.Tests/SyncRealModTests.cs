using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class SyncRealModTests : System.IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private string Folder => dir.File("_milieu_sync");

    private static string Manips() => PenumbraMeta.EncodeImcV0(SyncTarget.Cushion, [((byte)1, SyncFixtures.Entry(0, 1, 1))]);

    [Fact] // [AC-synced-lighting-8]
    public void Write_MapsGamePathsToRelativeFiles_AndCarriesTheImcManipulation()
    {
        Directory.CreateDirectory(Folder);
        var paths = new Dictionary<string, string>
        {
            ["chara/monster/m8058/obj/body/b0001/vfx/eff/vm0001.avfx"] = Path.Combine(Folder, "dome_a.avfx"),
            ["vfx/milieu/0011/sky.atex"] = Path.Combine(Folder, "sky_b.atex"),
        };
        Assert.True(SyncRealMod.Write(Folder, paths, Manips()));

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "meta.json")));
        Assert.Equal("Milieu Sync", meta.RootElement.GetProperty("Name").GetString());
        Assert.Equal(3, meta.RootElement.GetProperty("FileVersion").GetInt32());
        using var mod = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "default_mod.json")));
        var files = mod.RootElement.GetProperty("Files");
        Assert.Equal("dome_a.avfx", files.GetProperty("chara/monster/m8058/obj/body/b0001/vfx/eff/vm0001.avfx").GetString());
        Assert.Equal("sky_b.atex", files.GetProperty("vfx/milieu/0011/sky.atex").GetString());
        var m = mod.RootElement.GetProperty("Manipulations")[0];
        Assert.Equal("Imc", m.GetProperty("Type").GetString());
        Assert.Equal(1, m.GetProperty("Manipulation").GetProperty("Entry").GetProperty("VfxId").GetInt32());
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));   // atomic: no temp files left
    }

    [Fact] // [EDGE-synced-lighting-13]
    public void Write_RefusesPathsOutsideTheFolder_AndUnreadableManips_WritingNothing()
    {
        var outside = new Dictionary<string, string> { ["a.avfx"] = dir.File("elsewhere.avfx") };
        Assert.False(SyncRealMod.Write(Folder, outside, Manips()));
        Assert.False(SyncRealMod.Write(Folder, new Dictionary<string, string>(), "not base64!"));
        Assert.False(File.Exists(Path.Combine(Folder, "meta.json")));
    }

    [Fact] // [AC-synced-lighting-8] [EDGE-synced-lighting-36]
    public void WriteEmpty_LeavesNoFilesNoManipulations()
    {
        Directory.CreateDirectory(Folder);
        SyncRealMod.Write(Folder, new Dictionary<string, string> { ["x.avfx"] = Path.Combine(Folder, "d.avfx") }, Manips());
        SyncRealMod.WriteEmpty(Folder);
        using var mod = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "default_mod.json")));
        Assert.Empty(mod.RootElement.GetProperty("Files").EnumerateObject());
        Assert.Equal(0, mod.RootElement.GetProperty("Manipulations").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(Folder, "meta.json")));   // still a v3 mod Penumbra re-migrates on reload
    }
}
