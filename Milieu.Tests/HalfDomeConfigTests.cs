using System;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Gui;
using Newtonsoft.Json;
using Xunit;

namespace Milieu.Tests;

/// <summary>The half dome's persisted bottom, the v6 migration and the settings panel rules (docs/adr/0015).</summary>
public sealed class HalfDomeConfigTests
{
    private const string A = @"C:\pics\a.hdr";
    private const string B = @"C:\pics\b.jpg";
    private static readonly JsonSerializerSettings Dalamud = new() { TypeNameHandling = TypeNameHandling.Objects };

    private static Configuration RoundTrip(Configuration c) =>
        JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(c, Dalamud), Dalamud)!;

    // ---------------------------------------------------------------- AC-7: remembered per backdrop and dome

    [Fact] // [AC-half-dome-7] [AC-half-dome-11]
    public void Defaults_SkyImageFloor_RoomOpen()
    {
        var c = new Configuration();
        Assert.Equal(DomeBottom.ImageFloor, c.Sky.Bottom);
        Assert.Equal(DomeBottom.Open, c.Room.Bottom);
        Assert.Equal(DomeBottom.Open, DomeSettings.RoomDefaults().Bottom);
        Assert.Equal(DomeMeshKind.OpenBottom, c.Room.Shape(true).Kind);
    }

    [Fact] // [AC-half-dome-11]
    public void Migrate_V7_RoomSetsBecomeOpen_SkyKept()
    {
        var c = new Configuration { Version = 6, UserImages = [A] };
        c.Room.Bottom = DomeBottom.ImageFloor;
        c.Sky.Bottom = DomeBottom.ImageFloor;
        c.Backdrops[A] = new BackdropSettings
        {
            Sky = new DomeSettings { Radius = 20000, HeightOffset = 1.6f, GroundProjection = true, Bottom = DomeBottom.ImageFloor },
            Room = new DomeSettings { Radius = 11, HeightOffset = 1.6f, GroundProjection = true, Bottom = DomeBottom.ImageFloor },
        };
        c.Backdrops[B] = null!;
        c.Migrate(@"C:\cfg");
        Assert.Equal(7, c.Version);
        Assert.Equal(DomeBottom.Open, c.Room.Bottom);
        Assert.Equal(DomeBottom.Open, c.Backdrops[A].Room.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Sky.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Backdrops[A].Sky.Bottom);
    }

    [Fact] // [AC-half-dome-11]
    public void V7RoomImageFloorChoice_Kept()
    {
        var c = new Configuration { Version = 7 };
        c.Room.Bottom = DomeBottom.ImageFloor;   // chosen after the default changed
        var back = RoundTrip(c);
        back.Migrate(@"C:\cfg");
        Assert.Equal(DomeBottom.ImageFloor, back.Room.Bottom);
    }

    [Fact] // [AC-half-dome-7]
    public void Bottom_RoundTrips_TravelsWithEntry_Copied()
    {
        var c = new Configuration { UserImages = [A, B] };
        c.SelectBackdrop(A);
        c.Room.Bottom = DomeBottom.Open;
        c.SelectBackdrop(B);   // stores A's working copy, B starts from it
        c.Room.Bottom = DomeBottom.ImageFloor;
        c.Sky.Bottom = DomeBottom.Open;
        c.SelectBackdrop(A);
        Assert.Equal(DomeBottom.Open, c.Room.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Sky.Bottom);

        var back = RoundTrip(c);
        Assert.Equal(DomeBottom.Open, back.Room.Bottom);
        Assert.Equal(DomeBottom.Open, back.Backdrops[B].Sky.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, back.Backdrops[B].Room.Bottom);

        var d = new DomeSettings { Bottom = DomeBottom.Open };
        Assert.Equal(DomeBottom.Open, d.Copy().Bottom);
        var e = new DomeSettings();
        e.CopyFrom(d);
        Assert.Equal(DomeBottom.Open, e.Bottom);
    }

    [Fact] // [AC-half-dome-7] [EDGE-half-dome-10] [AC-half-dome-11]
    public void V5JsonWithoutBottom_SkyImageFloor_RoomOpen()
    {
        const string json = "{\"Version\":5,\"Room\":{\"Radius\":11,\"VerticalScale\":1,\"HeightOffset\":1.2,\"YawDegrees\":0,\"GroundProjection\":true},"
                            + "\"Backdrops\":{\"bundled:lodge\":{\"Room\":{\"Radius\":11,\"HeightOffset\":0.9,\"GroundProjection\":true},\"Sky\":{\"Radius\":20000}}}}";
        var c = JsonConvert.DeserializeObject<Configuration>(json)!;
        c.Migrate(@"C:\cfg");
        c.Clamp();
        Assert.Equal(DomeBottom.Open, c.Room.Bottom);
        Assert.Equal(DomeBottom.Open, c.Backdrops["bundled:lodge"].Room.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Backdrops["bundled:lodge"].Sky.Bottom);
    }

    // ---------------------------------------------------------------- AC-8: the v6 reset

    private static Configuration V5Like()
    {
        var c = new Configuration { Version = 5, Mode = DomeMode.Room, ImagePath = "bundled:lodge" };
        c.Room.Radius = 11;
        c.Room.HeightOffset = 0.07f;
        c.Backdrops["bundled:lodge"] = new BackdropSettings { Room = new DomeSettings { Radius = 11, HeightOffset = 0.1f, GroundProjection = true } };
        return c;
    }

    [Fact] // [AC-half-dome-8]
    public void Migrate_V6_ResetsLowPositiveHeights()
    {
        var c = V5Like();
        c.Migrate(@"C:\cfg");
        Assert.Equal(1.6f, c.Room.HeightOffset);
        Assert.Equal(1.6f, c.Backdrops["bundled:lodge"].Room.HeightOffset);
        Assert.Equal(7, c.Version);
    }

    [Fact] // [EDGE-half-dome-12]
    public void Migrate_V6_KeepsOtherHeights()
    {
        var c = new Configuration { Version = 5 };
        var kept = new (float H, bool On)[] { (-0.15f, true), (-7.38f, true), (0f, true), (0.25f, true), (0.3f, true), (1.2f, true), (0.1f, false), (-3f, false) };
        for (var i = 0; i < kept.Length; i++)
            c.Backdrops[$"k{i}"] = new BackdropSettings
            {
                Sky = new DomeSettings { Radius = 20000, HeightOffset = kept[i].H, GroundProjection = kept[i].On },
                Room = new DomeSettings { Radius = 10, HeightOffset = kept[i].H, GroundProjection = kept[i].On },
            };
        c.Sky.HeightOffset = -7.38f;
        c.Room.HeightOffset = 0.1f;
        c.Room.GroundProjection = false;
        c.Migrate(@"C:\cfg");
        Assert.Equal(-7.38f, c.Sky.HeightOffset);
        Assert.Equal(0.1f, c.Room.HeightOffset);
        for (var i = 0; i < kept.Length; i++)
        {
            Assert.Equal(kept[i].H, c.Backdrops[$"k{i}"].Sky.HeightOffset);
            Assert.Equal(kept[i].H, c.Backdrops[$"k{i}"].Room.HeightOffset);
        }
    }

    [Fact] // [EDGE-half-dome-10] [AC-half-dome-11]
    public void Migrate_V6Config_NotReMigrated()
    {
        var c = V5Like();
        c.Version = 6;
        c.Room.Bottom = DomeBottom.ImageFloor;   // as v6 wrote it
        c.Migrate(@"C:\cfg");
        Assert.Equal(0.07f, c.Room.HeightOffset);   // the v6 height reset doesn't run again
        Assert.Equal(DomeBottom.Open, c.Room.Bottom);   // v7: the room default
        Assert.Equal(7, c.Version);
    }

    [Fact] // [EDGE-half-dome-10] [AC-half-dome-11]
    public void Migrate_V7Config_Untouched()
    {
        var c = V5Like();
        c.Version = 7;
        c.Room.Bottom = DomeBottom.ImageFloor;
        c.Migrate(@"C:\cfg");
        Assert.Equal(0.07f, c.Room.HeightOffset);   // shown inactive, never rewritten again
        Assert.Equal(DomeBottom.ImageFloor, c.Room.Bottom);
        Assert.True(c.Room.ProjectionInactive);
    }

    [Fact] // [EDGE-half-dome-11]
    public void Migrate_V6_NullsAndNaN_NoThrow()
    {
        var c = new Configuration { Version = 5, Backdrops = null! };
        c.Room.HeightOffset = float.NaN;
        c.Sky = null!;
        c.Migrate(@"C:\cfg");
        Assert.Equal(7, c.Version);

        var d = new Configuration { Version = 5 };
        d.Backdrops["x"] = new BackdropSettings { Sky = null!, Room = null! };
        d.Backdrops["y"] = null!;
        d.Room.HeightOffset = float.NaN;
        d.Migrate(@"C:\cfg");
        d.Clamp();
        Assert.Equal(1.6f, d.Room.HeightOffset);
    }

    // ---------------------------------------------------------------- AC-9 and the panel's pure rules

    [Fact] // [EDGE-half-dome-9] [AC-half-dome-11]
    public void Clamp_UndefinedBottom_BecomesTheModesDefault()
    {
        var c = new Configuration { UserImages = [A] };
        c.Room.Bottom = (DomeBottom)7;
        c.Sky.Bottom = (DomeBottom)8;
        c.Backdrops[A] = new BackdropSettings
        {
            Sky = new DomeSettings { Radius = 20000, Bottom = (DomeBottom)9 },
            Room = new DomeSettings { Radius = 10, Bottom = (DomeBottom)9 },
        };
        c.Clamp();
        Assert.Equal(DomeBottom.Open, c.Room.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Sky.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Backdrops[A].Sky.Bottom);
        Assert.Equal(DomeBottom.Open, c.Backdrops[A].Room.Bottom);
    }

    [Fact] // [AC-half-dome-9]
    public void HeightMinAndStrings()
    {
        var d = DomeSettings.RoomDefaults();
        Assert.Equal(0f, d.HeightMin(DomeMode.Room));   // open: down to the feet [AC-half-dome-12]
        Assert.Equal(0.25f, d.HeightMin(DomeMode.Room, halfDomeAvailable: false));   // kill switch: the legacy floor's minimum
        d.Bottom = DomeBottom.ImageFloor;
        Assert.Equal(0.25f, d.HeightMin(DomeMode.Room));
        Assert.Equal(0.25f, d.HeightMin(DomeMode.Sky));
        d.GroundProjection = false;
        Assert.Equal(-10f, d.HeightMin(DomeMode.Room));
        Assert.Equal(-2000f, d.HeightMin(DomeMode.Sky));
        Assert.Equal("Bottom:", DomeSettings.BottomLabel);
        Assert.Equal(["Image floor", "Open (game floor)"], DomeSettings.BottomLabels);
        Assert.Equal("The image's lower half lies on a flat floor at your feet; its horizon sits at camera height.",
                     DomeSettings.BottomTooltip(DomeBottom.ImageFloor));
        Assert.Equal("The image stops where the dome meets the floor and the game's own floor stays; its horizon sits at camera height. The room default.",
                     DomeSettings.BottomTooltip(DomeBottom.Open));
        Assert.Equal("How high the image was taken above the floor: its horizon sits this far above your feet.", DomeSettings.CameraHeightTooltip);
    }

    [Fact] // [AC-half-dome-4]
    public void ProjectionOff_IsSphereWhateverTheBottom()
    {
        foreach (var bottom in new[] { DomeBottom.ImageFloor, DomeBottom.Open })
            foreach (var available in new[] { true, false })
            {
                var d = new DomeSettings { Radius = 10, VerticalScale = 1, HeightOffset = 1.6f, GroundProjection = false, Bottom = bottom };
                Assert.Equal(new DomeShapeChoice(DomeMeshKind.Sphere, null, 1.6f), d.Shape(available));
            }
    }

    [Fact] // [AC-half-dome-9] [EDGE-half-dome-19]
    public void ShowsBottomChoice_Matrix()
    {
        var d = new DomeSettings { GroundProjection = true };
        Assert.True(d.ShowsBottomChoice(halfDomeAvailable: true));
        Assert.False(d.ShowsBottomChoice(halfDomeAvailable: false));
        d.GroundProjection = false;
        Assert.False(d.ShowsBottomChoice(true));
        Assert.Equal("Stands the dome on the floor at your feet as a half dome.", DomeSettings.ProjectionTooltip(true));
        Assert.Equal("Flattens the bottom of the dome into a floor, so the image's ground lies at your feet.", DomeSettings.ProjectionTooltip(false));
        Assert.Equal("Distance from the floor to the dome centre.", DomeSettings.HeightTooltip(false));
        Assert.Equal(DomeSettings.CameraHeightTooltip, DomeSettings.HeightTooltip(true));
    }

    [Theory] // [AC-half-dome-12] [EDGE-half-dome-23]
    [InlineData(1.6f, 10f, 1f, 0.16f, 1.6f)]      // the room default: horizon at eye level, cut at the floor
    [InlineData(1.6f, 10f, 2f, 0.08f, 1.6f)]      // vertical scale stretches the dome
    [InlineData(0f, 10f, 1f, null, 0f)]          // at the feet: the uncut upper half
    [InlineData(-0.15f, 10f, 1f, null, 0f)]      // an old "lower the dome" entry: the uncut upper half at the feet
    public void Open_CameraHeightCutsAtTheFloor(float h, float r, float vs, float? cut, float centre)
    {
        var d = new DomeSettings { Radius = r, VerticalScale = vs, HeightOffset = h, GroundProjection = true, Bottom = DomeBottom.Open };
        var s = d.Shape(true);
        Assert.Equal(DomeMeshKind.OpenBottom, s.Kind);
        Assert.Equal(centre, s.CentreHeight);
        if (cut is { } c) Assert.Equal(c, s.FloorY!.Value, 5); else Assert.Null(s.FloorY);
        Assert.False(d.IsProjectionInactive(true));
    }

    [Fact] // [EDGE-half-dome-23]
    public void Open_HeightAboveTheDome_IsInactiveSphere()
    {
        var d = new DomeSettings { Radius = 10, VerticalScale = 1, HeightOffset = 10, GroundProjection = true, Bottom = DomeBottom.Open };
        Assert.Equal(new DomeShapeChoice(DomeMeshKind.Sphere, null, 10f), d.Shape(true));
        Assert.True(d.IsProjectionInactive(true));
        Assert.Equal("Inactive: the camera height (10) must be below the dome's height (10). Lower it or raise the radius.", d.ProjectionStatus(true));
        d.HeightOffset = float.NaN;
        Assert.Equal(new DomeShapeChoice(DomeMeshKind.OpenBottom, null, 0f), d.Shape(true));
    }

    [Theory] // [AC-half-dome-9] [EDGE-half-dome-20]
    [InlineData(DomeMode.Room, -3f, 1.6f)]
    [InlineData(DomeMode.Room, 0.1f, 1.6f)]
    [InlineData(DomeMode.Sky, -3f, 1.6f)]
    [InlineData(DomeMode.Sky, 0.1f, 1.6f)]
    [InlineData(DomeMode.Room, 0.3f, 0.3f)]
    public void ProjectionToggle_RaisesLowHeight(DomeMode mode, float before, float after)
    {
        var d = new DomeSettings { Radius = 10, HeightOffset = before, GroundProjection = false };
        BackdropViewModel.ProjectionToggle(d, mode, true);
        Assert.True(d.GroundProjection);
        Assert.Equal(after, d.HeightOffset);
        BackdropViewModel.ProjectionToggle(d, mode, false);
        Assert.False(d.GroundProjection);
        Assert.Equal(after, d.HeightOffset);   // ticking off leaves the height
    }

    [Fact] // [AC-half-dome-9]
    public void ApplyOf_DomeBottom_Rebuilds() => Assert.Equal(SliderApply.Rebuild, BackdropViewModel.ApplyOf("DomeBottom"));
}
