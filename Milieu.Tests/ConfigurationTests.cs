using Milieu.Backdrop;
using Milieu.Imaging;
using Newtonsoft.Json;
using Xunit;

namespace Milieu.Tests;

public class ConfigurationTests
{
    [Fact] // [AC-hdri-backdrop-2]
    public void Defaults_SkyAndRoomPresets()
    {
        var c = new Configuration();
        Assert.Equal((20000f, 1f, 1.6f, 0f), (c.Sky.Radius, c.Sky.VerticalScale, c.Sky.HeightOffset, c.Sky.YawDegrees));
        Assert.Equal((10f, 1f, 1.6f, 0f), (c.Room.Radius, c.Room.VerticalScale, c.Room.HeightOffset, c.Room.YawDegrees));
        Assert.Equal(DomeMode.Sky, c.Mode);
        Assert.Same(c.Sky, c.Current);
        c.Mode = DomeMode.Room;
        Assert.Same(c.Room, c.Current);
        Assert.Equal(1f, c.Brightness);
        Assert.Equal(ToneMapOperator.Aces, c.ToneMap);
        Assert.Equal(8192, c.MaxTextureWidth);
    }

    [Fact] // [AC-hdri-backdrop-4]
    public void Repeats_Default2And1()
    {
        var c = new Configuration();
        Assert.Equal(2f, c.HorizontalRepeat);
        Assert.Equal(1f, c.VerticalRepeat);
        Assert.False(c.FlipHorizontal);
    }

    [Fact] // [AC-hdri-backdrop-10]
    public void RoundTrip_AllFieldsSurvive()
    {
        var c = new Configuration
        {
            ImagePath = @"C:\pics\pano.hdr", Mode = DomeMode.Room, HorizontalRepeat = 1.5f, VerticalRepeat = 2f,
            FlipHorizontal = true, Brightness = 0.7f, ExposureEv = -2f, ToneMap = ToneMapOperator.Reinhard, MaxTextureWidth = 4096,
        };
        c.Sky.Radius = 9000; c.Sky.YawDegrees = 45; c.Room.HeightOffset = 2; c.Room.VerticalScale = 0.5f;
        var json = JsonConvert.SerializeObject(c, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects });
        var back = JsonConvert.DeserializeObject<Configuration>(json)!;
        Assert.Equal(JsonConvert.SerializeObject(c), JsonConvert.SerializeObject(back));
        Assert.Equal(@"C:\pics\pano.hdr", back.ImagePath);
        Assert.Equal(9000, back.Sky.Radius);
        Assert.Equal(0.5f, back.Room.VerticalScale);
    }

    [Fact] // [EDGE-hdri-backdrop-27]
    public void Clamp_FixesOutOfRangeAndNaN()
    {
        var c = new Configuration { Brightness = float.NaN, ExposureEv = 99, MaxTextureWidth = 3000, ToneMap = (ToneMapOperator)9, Mode = (DomeMode)5, ImagePath = "  " };
        c.Sky.Radius = float.PositiveInfinity;
        c.Room.Radius = 0.01f;
        c.Room.HeightOffset = float.NaN;
        c.Sky = null!;
        c.Clamp();
        Assert.Equal(1f, c.Brightness);
        Assert.Equal(10f, c.ExposureEv);
        Assert.Equal(8192, c.MaxTextureWidth);
        Assert.Equal(ToneMapOperator.Aces, c.ToneMap);
        Assert.Equal(DomeMode.Sky, c.Mode);
        Assert.Null(c.ImagePath);
        Assert.Equal(20000f, c.Sky.Radius);
        Assert.Equal(1f, c.Room.Radius);
        Assert.Equal(1.6f, c.Room.HeightOffset);
    }

    [Fact] // [EDGE-hdri-backdrop-30]
    public void Repeat_Clamped()
    {
        foreach (var (input, expected) in new[] { (0f, 0.5f), (float.NaN, 2f), (10f, 4f), (1.25f, 1.25f) })
        {
            var c = new Configuration { HorizontalRepeat = input };
            c.Clamp();
            Assert.Equal(expected, c.HorizontalRepeat);
        }
    }


    [Fact] // [AC-hdri-backdrop-15]
    public void Defaults_CameraHeight16_GroundProjectionOnForBoth()
    {
        var c = new Configuration();
        Assert.Equal(1.6f, c.Sky.HeightOffset);
        Assert.Equal(1.6f, c.Room.HeightOffset);
        Assert.True(c.Sky.GroundProjection);
        Assert.True(c.Room.GroundProjection);
        Assert.Equal(4, c.Version);
        Assert.Empty(c.UserImages);
    }

    private static Configuration V1(string json) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;

    [Fact] // [AC-hdri-backdrop-15] [EDGE-hdri-backdrop-83]
    public void Migrate_SkyHeightDefaultsTo16_KeepsUserValue_SetsGroundProjectionDefaults()
    {
        var untouched = V1("{\"Version\":1,\"Sky\":{\"Radius\":20000,\"VerticalScale\":1,\"HeightOffset\":0,\"YawDegrees\":0},\"Room\":{\"Radius\":10,\"VerticalScale\":1,\"HeightOffset\":1.6,\"YawDegrees\":0}}");
        untouched.Migrate(@"C:\cfg");
        Assert.Equal(1.6f, untouched.Sky.HeightOffset);
        Assert.True(untouched.Room.GroundProjection);
        Assert.True(untouched.Sky.GroundProjection);
        Assert.Equal(4, untouched.Version);

        var userSet = V1("{\"Version\":1,\"Sky\":{\"Radius\":20000,\"VerticalScale\":1,\"HeightOffset\":250,\"YawDegrees\":30}}");
        userSet.Migrate(@"C:\cfg");
        Assert.Equal(250f, userSet.Sky.HeightOffset);
        Assert.Equal(30f, userSet.Sky.YawDegrees);

        // A v2 config (sky projection off, height 400 by the old defaults) gets projection and eye height.
        var v2 = new Configuration { Version = 2 };
        v2.Sky.HeightOffset = 400;
        v2.Sky.GroundProjection = false;
        v2.Migrate(@"C:\cfg");
        Assert.Equal(1.6f, v2.Sky.HeightOffset);
        Assert.True(v2.Sky.GroundProjection);
        Assert.Equal(4, v2.Version);

        // A v3 config with a height the user chose keeps it.
        var v3 = new Configuration { Version = 3 };
        v3.Sky.HeightOffset = 120;
        v3.Migrate(@"C:\cfg");
        Assert.Equal(120f, v3.Sky.HeightOffset);

        // Already v4: nothing changes, including a projection the user turned off.
        var v4 = new Configuration();
        v4.Sky.GroundProjection = false;
        v4.Sky.HeightOffset = 400;
        v4.Migrate(@"C:\cfg");
        Assert.False(v4.Sky.GroundProjection);
        Assert.Equal(400f, v4.Sky.HeightOffset);
    }

    [Theory] // [EDGE-hdri-backdrop-64]
    [InlineData("forest")] [InlineData("galaxy")] [InlineData("livingroom")] [InlineData("livingroom2")]
    [InlineData("lodge")] [InlineData("moon")] [InlineData("sunset")] [InlineData("warehouse")]
    public void Migrate_v1_bundled_jpeg_path_to_bundled_id(string stem)
    {
        var c = V1("{\"Version\":1,\"ImagePath\":" + Newtonsoft.Json.JsonConvert.ToString($@"C:\cfg\DefaultBackdrops\{stem}.jpeg") + "}");
        c.Migrate(@"C:\cfg");
        Assert.Equal("bundled:" + stem, c.ImagePath);
        Assert.Empty(c.UserImages);
    }

    [Fact] // [EDGE-hdri-backdrop-64]
    public void Migrate_v1_user_path_added_to_UserImages()
    {
        var c = V1("{\"Version\":1,\"ImagePath\":" + Newtonsoft.Json.JsonConvert.ToString(@"D:\pics\pano.hdr") + "}");
        c.Migrate(@"C:\cfg");
        Assert.Equal(@"D:\pics\pano.hdr", c.ImagePath);
        Assert.Equal(new[] { @"D:\pics\pano.hdr" }, c.UserImages);
        // A bundled-looking file outside the config's DefaultBackdrops folder is a user image.
        var other = V1("{\"Version\":1,\"ImagePath\":" + Newtonsoft.Json.JsonConvert.ToString(@"D:\pics\forest.jpeg") + "}");
        other.Migrate(@"C:\cfg");
        Assert.Equal(@"D:\pics\forest.jpeg", other.ImagePath);
    }

    [Fact] // [EDGE-hdri-backdrop-65]
    public void Clamp_UnknownBundledId_ToNull()
    {
        var c = new Configuration { ImagePath = "bundled:nope", UserImages = [@"C:\a.png", @"c:\A.PNG", "", @"C:\b.jpg"] };
        c.Clamp();
        Assert.Null(c.ImagePath);
        Assert.Equal(new[] { @"C:\a.png", @"C:\b.jpg" }, c.UserImages);
        var ok = new Configuration { ImagePath = "bundled:moon" };
        ok.Clamp();
        Assert.Equal("bundled:moon", ok.ImagePath);
    }
}
