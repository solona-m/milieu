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

    [Fact] // [AC-half-dome-7]
    public void Defaults_ImageFloor()
    {
        var c = new Configuration();
        Assert.Equal(DomeBottom.ImageFloor, c.Sky.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Room.Bottom);
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

    [Fact] // [AC-half-dome-7] [EDGE-half-dome-10]
    public void V5JsonWithoutBottom_LoadsAsImageFloor()
    {
        const string json = "{\"Version\":5,\"Room\":{\"Radius\":11,\"VerticalScale\":1,\"HeightOffset\":1.2,\"YawDegrees\":0,\"GroundProjection\":true},"
                            + "\"Backdrops\":{\"bundled:lodge\":{\"Room\":{\"Radius\":11,\"HeightOffset\":0.9,\"GroundProjection\":true},\"Sky\":{\"Radius\":20000}}}}";
        var c = JsonConvert.DeserializeObject<Configuration>(json)!;
        c.Migrate(@"C:\cfg");
        c.Clamp();
        Assert.Equal(DomeBottom.ImageFloor, c.Room.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Backdrops["bundled:lodge"].Room.Bottom);
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
        Assert.Equal(6, c.Version);
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

    [Fact] // [EDGE-half-dome-10]
    public void Migrate_V6Config_NotReMigrated()
    {
        var c = V5Like();
        c.Version = 6;
        c.Migrate(@"C:\cfg");
        Assert.Equal(0.07f, c.Room.HeightOffset);   // shown inactive, never rewritten again
        Assert.True(c.Room.ProjectionInactive);
    }

    [Fact] // [EDGE-half-dome-11]
    public void Migrate_V6_NullsAndNaN_NoThrow()
    {
        var c = new Configuration { Version = 5, Backdrops = null! };
        c.Room.HeightOffset = float.NaN;
        c.Sky = null!;
        c.Migrate(@"C:\cfg");
        Assert.Equal(6, c.Version);

        var d = new Configuration { Version = 5 };
        d.Backdrops["x"] = new BackdropSettings { Sky = null!, Room = null! };
        d.Backdrops["y"] = null!;
        d.Room.HeightOffset = float.NaN;
        d.Migrate(@"C:\cfg");
        d.Clamp();
        Assert.Equal(1.6f, d.Room.HeightOffset);
    }

    // ---------------------------------------------------------------- AC-9 and the panel's pure rules

    [Fact] // [EDGE-half-dome-9]
    public void Clamp_UndefinedBottom_BecomesImageFloor()
    {
        var c = new Configuration { UserImages = [A] };
        c.Room.Bottom = (DomeBottom)7;
        c.Backdrops[A] = new BackdropSettings { Sky = new DomeSettings { Radius = 20000, Bottom = (DomeBottom)9 } };
        c.Clamp();
        Assert.Equal(DomeBottom.ImageFloor, c.Room.Bottom);
        Assert.Equal(DomeBottom.ImageFloor, c.Backdrops[A].Sky.Bottom);
    }

    [Fact] // [AC-half-dome-9]
    public void HeightMinAndStrings()
    {
        var d = DomeSettings.RoomDefaults();
        Assert.Equal(0.25f, d.HeightMin(DomeMode.Room));
        Assert.Equal(0.25f, d.HeightMin(DomeMode.Sky));
        d.GroundProjection = false;
        Assert.Equal(-10f, d.HeightMin(DomeMode.Room));
        Assert.Equal(-2000f, d.HeightMin(DomeMode.Sky));
        Assert.Equal("Bottom:", DomeSettings.BottomLabel);
        Assert.Equal(["Image floor", "Open (horizon at your feet)"], DomeSettings.BottomLabels);
        Assert.Equal("The image's lower half lies on a flat floor at your feet; its horizon sits at camera height.",
                     DomeSettings.BottomTooltip(DomeBottom.ImageFloor));
        Assert.Equal("Only the image's upper half is drawn, its horizon at your feet, and the game's own ground shows below. Best for skies and open scenes: in a room the horizon sits below eye level and the image's floor is gone.",
                     DomeSettings.BottomTooltip(DomeBottom.Open));
        Assert.Equal("How high the image was taken above the floor: its horizon sits this far above your feet.", DomeSettings.CameraHeightTooltip);
        Assert.Equal("Not used: with an open bottom the image's horizon is at your feet.", DomeSettings.CameraHeightDisabledTooltip);
    }

    [Fact] // [EDGE-half-dome-19]
    public void CameraHeightDisabled_Matrix()
    {
        var d = new DomeSettings { GroundProjection = true, Bottom = DomeBottom.Open };
        Assert.True(d.CameraHeightDisabled(halfDomeAvailable: true));
        Assert.False(d.CameraHeightDisabled(halfDomeAvailable: false));
        d.Bottom = DomeBottom.ImageFloor;
        Assert.False(d.CameraHeightDisabled(true));
        d.Bottom = DomeBottom.Open;
        d.GroundProjection = false;
        Assert.False(d.CameraHeightDisabled(true));
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
