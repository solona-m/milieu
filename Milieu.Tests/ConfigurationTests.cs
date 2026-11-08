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
        Assert.Equal((20000f, 1f, 400f, 0f), (c.Sky.Radius, c.Sky.VerticalScale, c.Sky.HeightOffset, c.Sky.YawDegrees));
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
}
