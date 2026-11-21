using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace Milieu.Tests;

/// <summary>Per-backdrop settings in the configuration (docs/adr/0014).</summary>
public sealed class ConfigurationPerBackdropTests
{
    private static readonly JsonSerializerSettings Dalamud = new() { TypeNameHandling = TypeNameHandling.Objects };
    private const string A = @"C:\pics\a.hdr";
    private const string B = @"C:\pics\b.jpg";

    private static BackdropSettings Distinct(int k) => new()
    {
        Mode = k % 2 == 0 ? DomeMode.Sky : DomeMode.Room,
        Sky = new DomeSettings { Radius = 1000 + k, VerticalScale = 0.5f + k * 0.01f, HeightOffset = 2 + k, YawDegrees = 10 + k, GroundProjection = k % 2 == 0 },
        Room = new DomeSettings { Radius = 5 + k, VerticalScale = 1.5f, HeightOffset = 1 + k * 0.1f, YawDegrees = -20 - k, GroundProjection = k % 2 == 1 },
        HorizontalRepeat = 1 + k * 0.1f, VerticalRepeat = 1.5f, FlipHorizontal = k % 2 == 1, Brightness = 0.5f + k * 0.1f,
        ExposureEv = -1 - k, ToneMap = k % 2 == 0 ? ToneMapOperator.Reinhard : ToneMapOperator.Aces,
        SceneLighting = k % 2 == 1, LightStrength = 0.3f + k * 0.1f, FillLightCount = k % 4,
        NightWhileShown = k % 2 == 0, SunOffWhileShown = k % 2 == 1,
    };

    private static string Json(BackdropSettings s) => JsonConvert.SerializeObject(s);

    private static Configuration WithTwo()
    {
        var c = new Configuration { UserImages = [A, B] };
        c.Backdrops[A] = Distinct(1);
        c.Backdrops[B] = Distinct(2);
        return c;
    }

    [Fact] // [AC-per-backdrop-settings-1]
    public void SelectBackdrop_AppliesStoredSet_AllFourteenFields()
    {
        var c = WithTwo();
        c.SelectBackdrop(A);
        Assert.Equal(Json(Distinct(1)), Json(c.CaptureLive()));
        Assert.Same(c.Room, c.Current);   // A's mode is Room
        c.SelectBackdrop(B);
        Assert.Equal(Json(Distinct(2)), Json(c.CaptureLive()));
        Assert.Same(c.Sky, c.Current);
    }

    [Fact] // [AC-per-backdrop-settings-3]
    public void SelectBackdrop_FirstTime_CopiesCurrentWorkingCopy()
    {
        var c = WithTwo();
        c.SelectBackdrop(A);
        c.Brightness = 1.7f;
        c.SelectBackdrop(@"C:\pics\new.png");
        Assert.Equal(1.7f, c.Brightness);
        Assert.Equal(Json(c.CaptureLive()), Json(c.Backdrops[@"C:\pics\new.png"]));
        Assert.Equal(1.7f, c.Backdrops[A].Brightness);   // stored on the way out
    }

    [Fact] // [AC-per-backdrop-settings-3]
    public void Clamp_NonEmptyMap_NewPinNotSeeded_FirstSelectCopiesCurrent()
    {
        var c = new Configuration { UserImages = [A] };
        c.Backdrops[A] = Distinct(1);
        var forest = BackdropPins.IdOf("forest");
        c.Clamp();
        Assert.False(c.Backdrops.ContainsKey(forest));   // a non-empty store isn't re-seeded (a future pin starts on first use)
        c.Brightness = 0.25f;
        c.SelectBackdrop(forest);
        Assert.Equal(0.25f, c.Backdrops[forest].Brightness);
    }

    [Fact] // [AC-per-backdrop-settings-4]
    public void Migrate_v4_ThenClamp_SeedsEveryKnownBackdropWithV4Values()
    {
        var json = "{\"Version\":4,\"ImagePath\":\"C:\\\\pics\\\\a.hdr\",\"UserImages\":[\"C:\\\\pics\\\\a.hdr\",\"C:\\\\pics\\\\b.jpg\"],"
                   + "\"Mode\":1,\"Room\":{\"Radius\":7,\"VerticalScale\":1,\"HeightOffset\":1.2,\"YawDegrees\":30,\"GroundProjection\":true},"
                   + "\"Brightness\":0.6,\"ExposureEv\":-2,\"LightStrength\":0.4,\"NightWhileShown\":true}";
        var c = JsonConvert.DeserializeObject<Configuration>(json)!;
        c.Migrate("config");
        Assert.Equal(6, c.Version);
        Assert.Empty(c.Backdrops);   // Migrate never seeds
        c.Clamp();
        var expected = Json(c.CaptureLive());
        Assert.Equal(2 + BackdropPins.Count, c.Backdrops.Count);
        Assert.All(c.Backdrops.Values, e => Assert.Equal(expected, Json(e)));
        Assert.Equal((DomeMode.Room, 7f, 30f, 0.6f, -2f, 0.4f, true), (c.Mode, c.Room.Radius, c.Room.YawDegrees, c.Brightness, c.ExposureEv, c.LightStrength, c.NightWhileShown));
    }

    [Fact] // [AC-per-backdrop-settings-4]
    public void Migrate_v1Chain_SeedsCarryHeightFixes()
    {
        var c = JsonConvert.DeserializeObject<Configuration>("{\"Version\":1,\"Sky\":{\"Radius\":20000,\"VerticalScale\":1,\"HeightOffset\":0}}")!;
        c.Migrate("config");
        c.Clamp();
        Assert.All(c.Backdrops.Values, e => Assert.Equal(1.6f, e.Sky.HeightOffset));
        Assert.All(c.Backdrops.Values, e => Assert.True(e.Sky.GroundProjection));
    }

    [Fact] // [AC-per-backdrop-settings-6]
    public void SelectBackdrop_NeverChangesTextureSizeOrDelivery()
    {
        var c = WithTwo();
        c.MaxTextureWidth = 4096;
        c.Delivery = Sync.DeliveryMode.MadBirdRing;
        c.SyncUploadNoticeShown = true;
        c.SelectBackdrop(A);
        c.SelectBackdrop(B);
        c.SelectBackdrop(null);
        Assert.Equal((4096, Sync.DeliveryMode.MadBirdRing, true), (c.MaxTextureWidth, c.Delivery, c.SyncUploadNoticeShown));
    }

    [Fact] // [AC-per-backdrop-settings-7] [EDGE-per-backdrop-settings-7]
    public void Clamp_KeepsEntryOfMissingFileWhileInUserImages()
    {
        var c = WithTwo();   // the files don't exist: entries are kept while their tiles are
        c.Clamp();
        Assert.True(c.Backdrops.ContainsKey(A));
        Assert.True(c.Backdrops.ContainsKey(B));
        Assert.True(c.RemoveUserImage(B) == false);
        Assert.False(c.Backdrops.ContainsKey(B));
    }

    [Fact] // [AC-per-backdrop-settings-10] [EDGE-per-backdrop-settings-9]
    public void Clamp_RepairsAndDropsBadEntries_SelectedSettingsKept()
    {
        var c = WithTwo();
        c.SelectBackdrop(A);
        var selected = Json(c.CaptureLive());
        c.Backdrops[B] = new BackdropSettings { Brightness = float.NaN, Sky = null!, Mode = (DomeMode)9, FillLightCount = 99 };
        c.Backdrops["   "] = new BackdropSettings();
        c.Backdrops[@"C:\not\a\tile.png"] = new BackdropSettings();
        c.Backdrops["bundled:nosuchscene"] = new BackdropSettings();
        c.Backdrops[BackdropPins.IdOf("moon")] = null!;
        c.Clamp();
        Assert.Equal(new[] { A, B }.OrderBy(x => x), c.Backdrops.Keys.OrderBy(x => x));
        Assert.Equal(1f, c.Backdrops[B].Brightness);
        Assert.NotNull(c.Backdrops[B].Sky);
        Assert.Equal(DomeMode.Sky, c.Backdrops[B].Mode);
        Assert.Equal(2, c.Backdrops[B].FillLightCount);
        Assert.Equal(selected, Json(c.CaptureLive()));
        Assert.Equal(selected, Json(c.Backdrops[A]));
    }

    [Fact] // [AC-per-backdrop-settings-10]
    public void RoundTrip_TypeNameHandlingObjects_BackdropsSurviveCaseInsensitive()
    {
        var c = WithTwo();
        c.SelectBackdrop(A);
        var back = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(c, Dalamud), Dalamud)!;
        back.Migrate("config");
        back.Clamp();
        Assert.Equal(Json(Distinct(2)), Json(back.Backdrops[B.ToUpperInvariant()]));
        Assert.Equal(Json(Distinct(1)), Json(back.CaptureLive()));
    }

    /// <summary>The shape an older build (v4) deserializes into: no Backdrops property, no BackdropSettings type.</summary>
    private sealed class V4Shape
    {
        public int Version { get; set; }
        public string? ImagePath { get; set; }
        public List<string> UserImages { get; set; } = [];
        public DomeMode Mode { get; set; }
        public DomeSettings Sky { get; set; } = DomeSettings.SkyDefaults();
        public DomeSettings Room { get; set; } = DomeSettings.RoomDefaults();
        public float Brightness { get; set; }
        public float ExposureEv { get; set; }
        public bool SceneLighting { get; set; }
    }

    private sealed class OldBuildBinder : ISerializationBinder
    {
        private readonly DefaultSerializationBinder inner = new();

        public Type BindToType(string? assemblyName, string typeName)
        {
            if (typeName.Contains("BackdropSettings")) throw new JsonSerializationException("an older build has no " + typeName);
            if (typeName == typeof(Configuration).FullName) return typeof(V4Shape);
            return inner.BindToType(assemblyName, typeName);
        }

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName) =>
            inner.BindToName(serializedType, out assemblyName, out typeName);
    }

    [Fact] // [AC-per-backdrop-settings-10] [EDGE-per-backdrop-settings-11]
    public void V5Json_LoadsIntoV4Shape_FlatValuesKept()
    {
        var c = WithTwo();
        c.SelectBackdrop(B);
        var json = JsonConvert.SerializeObject(c, Dalamud);
        var old = JsonConvert.DeserializeObject<V4Shape>(json, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Objects, SerializationBinder = new OldBuildBinder(),
        })!;
        Assert.Equal((B, Distinct(2).Brightness, Distinct(2).ExposureEv, Distinct(2).Sky.Radius), (old.ImagePath, old.Brightness, old.ExposureEv, old.Sky.Radius));
    }

    [Fact] // [AC-per-backdrop-settings-10] [EDGE-per-backdrop-settings-11]
    public void V5LoadedWithoutBackdrops_ReSeededFromLastSelected()
    {
        var c = WithTwo();
        c.SelectBackdrop(B);
        var json = JsonConvert.SerializeObject(c, Dalamud).Replace("\"Backdrops\"", "\"DroppedByAnOlderBuild\"");
        var back = JsonConvert.DeserializeObject<Configuration>(json, Dalamud)!;
        back.Migrate("config");
        back.Clamp();
        Assert.Equal(Json(Distinct(2)), Json(back.Backdrops[A]));   // forgotten: A now starts from the last selected (B)
    }

    [Fact] // [EDGE-per-backdrop-settings-1]
    public void Entries_NeverAliasWorkingCopyOrEachOther()
    {
        var c = WithTwo();
        var heldCurrent = c.Current;
        c.SelectBackdrop(A);
        c.SelectBackdrop(B);
        c.Sky.Radius = 333;
        Assert.Equal(Distinct(1).Sky.Radius, c.Backdrops[A].Sky.Radius);
        Assert.NotSame(c.Sky, c.Backdrops[B].Sky);
        Assert.Same(heldCurrent, c.Sky);   // a held Current reference follows the switch
        c.StoreSelected();
        Assert.Equal(333f, c.Backdrops[B].Sky.Radius);
        Assert.NotSame(c.Backdrops[A].Sky, c.Backdrops[B].Sky);
    }

    [Fact] // [EDGE-per-backdrop-settings-2]
    public void ModeFlipOnOneBackdrop_KeepsOtherModeSet()
    {
        var c = WithTwo();
        c.SelectBackdrop(B);   // Sky
        var sky = Json(c.CaptureLive());
        c.Mode = DomeMode.Room;
        c.Room.Radius = 42;
        c.StoreSelected();
        c.SelectBackdrop(A);
        c.SelectBackdrop(B);
        Assert.Equal(42f, c.Room.Radius);
        c.Mode = DomeMode.Sky;
        Assert.Equal(Distinct(2).Sky.Radius, c.Current.Radius);
        Assert.Equal(Json(Distinct(1)), Json(c.Backdrops[A]));
        Assert.NotEqual(sky, Json(c.CaptureLive()));
    }

    [Fact] // [EDGE-per-backdrop-settings-5]
    public void Keys_CaseInsensitive_OneEntry()
    {
        var c = WithTwo();
        c.SelectBackdrop(A.ToUpperInvariant());
        Assert.Equal(Json(Distinct(1)), Json(c.CaptureLive()));
        Assert.Equal(2, c.Backdrops.Count);
    }

    [Fact] // [EDGE-per-backdrop-settings-8]
    public void Clamp_PrunesUnpinnedBundledEntry()
    {
        var c = new Configuration { ImagePath = "bundled:retired" };
        c.Backdrops["bundled:retired"] = new BackdropSettings();
        c.Backdrops[BackdropPins.IdOf("moon")] = new BackdropSettings();
        c.Clamp();
        Assert.Null(c.ImagePath);
        Assert.False(c.Backdrops.ContainsKey("bundled:retired"));
        Assert.True(c.Backdrops.ContainsKey(BackdropPins.IdOf("moon")));
    }

    [Fact] // [EDGE-per-backdrop-settings-10]
    public void NoVersionJson_SeededByClamp_NothingLost()
    {
        var c = JsonConvert.DeserializeObject<Configuration>("{\"Brightness\":0.4,\"UserImages\":[\"C:\\\\x.png\"]}")!;
        c.Migrate("config");
        c.Clamp();
        Assert.Equal(0.4f, c.Brightness);
        Assert.Equal(0.4f, c.Backdrops[@"C:\x.png"].Brightness);
        Assert.Equal(1 + BackdropPins.Count, c.Backdrops.Count);
    }

    [Fact] // [EDGE-per-backdrop-settings-12]
    public void MigrateAndClampTwice_IdenticalJson_ExistingEntriesUntouched()
    {
        var c = WithTwo();
        c.SelectBackdrop(A);
        c.Migrate("config"); c.Clamp();
        var once = JsonConvert.SerializeObject(c, Dalamud);
        c.Migrate("config"); c.Clamp();
        Assert.Equal(once, JsonConvert.SerializeObject(c, Dalamud));
        Assert.Equal(Json(Distinct(2)), Json(c.Backdrops[B]));
    }

    [Fact] // [EDGE-per-backdrop-settings-13]
    public void Clamp_FlatFieldsWinOverSelectedEntry()
    {
        var c = WithTwo();
        c.SelectBackdrop(A);
        c.Brightness = 1.9f;   // as an older build might have edited it
        c.Clamp();
        Assert.Equal(1.9f, c.Backdrops[A].Brightness);
    }

    [Fact] // [EDGE-per-backdrop-settings-19]
    public void BundledEntry_ExposureToneMapSurviveRoundTrip()
    {
        var moon = BackdropPins.IdOf("moon");
        var c = new Configuration();
        BackdropSettingsTests.SwitchTo(c, moon, e => { e.ExposureEv = 3; e.ToneMap = ToneMapOperator.Reinhard; });
        var back = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(c, Dalamud), Dalamud)!;
        back.Clamp();
        Assert.Equal((3f, ToneMapOperator.Reinhard), (back.Backdrops[moon].ExposureEv, back.Backdrops[moon].ToneMap));
    }

    [Fact] // [EDGE-per-backdrop-settings-22]
    public void ConcurrentSaveAndSelect_NoTornEntryNoException()
    {
        var c = WithTwo();
        var known = new HashSet<string> { Json(Distinct(1)), Json(Distinct(2)) };
        var stop = false;
        var saves = 0;
        Exception? error = null;
        using var started = new ManualResetEventSlim();
        var saver = new Thread(() =>
        {
            started.Set();
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    Plugin.SaveConfig(c, cfg => JsonConvert.SerializeObject(cfg, Dalamud));
                    Interlocked.Increment(ref saves);
                }
            }
            catch (Exception e) { error = e; }
        }) { IsBackground = true };
        saver.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var selects = 0;
        while ((selects < 10_000 || Volatile.Read(ref saves) < 200) && saver.IsAlive)   // real overlap; a crashed saver ends the loop
            c.SelectBackdrop(++selects % 50 == 0 ? $@"C:\pics\new{selects}.png" : selects % 2 == 0 ? A : B);   // new keys grow the map mid-save
        Volatile.Write(ref stop, true);
        saver.Join();
        Assert.Null(error);
        Assert.True(saves >= 200);
        Assert.Contains(Json(c.Backdrops[A]), known);
        Assert.Contains(Json(c.Backdrops[B]), known);
    }

    [Fact] // [EDGE-per-backdrop-settings-23]
    public void PersistedKeys_OnlyPathsInUserImagesOrImagePath()
    {
        var c = WithTwo();
        c.Backdrops[@"C:\secret\elsewhere.png"] = new BackdropSettings();
        c.Clamp();
        Assert.All(c.Backdrops.Keys, k => Assert.True(BackdropPins.IsBundledId(k) || c.UserImages.Contains(k) || k == c.ImagePath, k));
    }

    [Fact] // [EDGE-per-backdrop-settings-25]
    public void MigrateNeverSeeds_ClampSeedsClampedValues()
    {
        var c = new Configuration { Brightness = float.NaN, UserImages = [A] };
        c.Backdrops.Clear();
        c.Migrate("config");
        Assert.Empty(c.Backdrops);
        c.Clamp();
        Assert.All(c.Backdrops.Values, e => Assert.Equal(1f, e.Brightness));
    }

    [Fact] // [EDGE-per-backdrop-settings-26]
    public void ImagePath_AssignedOnlyInsideConfiguration()
    {
        var root = Path.Combine(RepoPaths.Root(), "Milieu");
        var pattern = new Regex(@"(\.|\b)ImagePath\s*(\?\?)?=(?![=>])");   // also ??=, never == or =>
        var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith($"{Path.DirectorySeparatorChar}Configuration.cs"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => pattern.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.Empty(hits);
    }

    [Fact] // [EDGE-per-backdrop-settings-27]
    public void Migrate_v4_NullSkyOrRoom_DoesNotThrow_SeedsDefaults()
    {
        var c = JsonConvert.DeserializeObject<Configuration>("{\"Version\":4,\"Sky\":null,\"Room\":null,\"UserImages\":[\"C:\\\\x.png\"]}")!;
        c.Migrate("config");
        c.Clamp();
        Assert.NotNull(c.Sky);
        Assert.Equal(20000f, c.Backdrops[@"C:\x.png"].Sky.Radius);
        Assert.Equal(10f, c.Backdrops[@"C:\x.png"].Room.Radius);
    }
    [Fact] // [AC-per-backdrop-settings-10] [EDGE-per-backdrop-settings-9]
    public void Clamp_NullBackdropsMap_DoesNotThrow_Seeds()
    {
        var c = JsonConvert.DeserializeObject<Configuration>("{\"Version\":5,\"Backdrops\":null,\"UserImages\":[\"C:\\\\x.png\"]}", Dalamud)!;
        c.Migrate("config");
        c.Clamp();
        Assert.Equal(1 + BackdropPins.Count, c.Backdrops.Count);
        Assert.All(c.Backdrops.Values, e => Assert.Equal(Json(c.CaptureLive()), Json(e)));
    }

    [Fact] // [AC-per-backdrop-settings-3]
    public void AddUserImage_UnparseableStoredPath_FallsBackToCaseInsensitiveEquality()
    {
        var bad = "C:\\pics\\bad\0name.png";
        var c = new Configuration { UserImages = [bad] };
        Assert.Equal(bad, c.AddUserImage(bad.ToUpperInvariant()));
        Assert.Single(c.UserImages);
        Assert.Equal(@"C:\pics\ok.png", c.AddUserImage(@"C:\pics\ok.png"));
        Assert.Equal(2, c.UserImages.Count);
    }
}
