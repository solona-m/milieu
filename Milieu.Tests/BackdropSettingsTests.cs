using System;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

/// <summary>The per-backdrop settings set (docs/adr/0014).</summary>
public sealed class BackdropSettingsTests
{
    /// <summary>Test helper: makes <paramref name="id"/>'s entry (from the working copy, then <paramref name="set"/>) and selects it.</summary>
    internal static void SwitchTo(Configuration c, string id, Action<BackdropSettings> set)
    {
        var entry = c.CaptureLive();
        set(entry);
        if (!c.UserImages.Contains(id) && !id.StartsWith("bundled:", StringComparison.Ordinal)) c.UserImages.Add(id);
        c.Backdrops[id] = entry;
        c.SelectBackdrop(id);
    }

    [Theory] // [EDGE-per-backdrop-settings-9]
    [InlineData("nan")]
    [InlineData("range")]
    [InlineData("nullsets")]
    [InlineData("enums")]
    [InlineData("fills")]
    public void Clamp_FixesEachField(string bad)
    {
        var s = new BackdropSettings();
        switch (bad)
        {
            case "nan":
                s.HorizontalRepeat = float.NaN; s.VerticalRepeat = float.PositiveInfinity; s.Brightness = float.NaN;
                s.ExposureEv = float.NaN; s.LightStrength = float.NaN; s.Sky.Radius = float.NaN;
                break;
            case "range":
                s.HorizontalRepeat = 99; s.VerticalRepeat = -1; s.Brightness = 9; s.ExposureEv = -50; s.LightStrength = 7; s.Room.Radius = 1e6f;
                break;
            case "nullsets": s.Sky = null!; s.Room = null!; break;
            case "enums": s.Mode = (DomeMode)42; s.ToneMap = (ToneMapOperator)99; break;
            case "fills": s.FillLightCount = 9; break;
        }
        s.Clamp();
        Assert.InRange(s.HorizontalRepeat, Configuration.RepeatRange.Min, Configuration.RepeatRange.Max);
        Assert.InRange(s.VerticalRepeat, Configuration.RepeatRange.Min, Configuration.RepeatRange.Max);
        Assert.InRange(s.Brightness, Configuration.BrightnessRange.Min, Configuration.BrightnessRange.Max);
        Assert.InRange(s.ExposureEv, Configuration.ExposureRange.Min, Configuration.ExposureRange.Max);
        Assert.InRange(s.LightStrength, Configuration.LightStrengthRange.Min, Configuration.LightStrengthRange.Max);
        Assert.InRange(s.FillLightCount, Configuration.FillLightRange.Min, Configuration.FillLightRange.Max);
        Assert.True(Enum.IsDefined(s.Mode));
        Assert.True(Enum.IsDefined(s.ToneMap));
        Assert.NotNull(s.Sky);
        Assert.NotNull(s.Room);
        Assert.True(float.IsFinite(s.Sky.Radius));
        Assert.InRange(s.Room.Radius, 1f, 100f);
        if (bad == "nan") Assert.Equal((2f, 1f, 1f, 0f, 1f), (s.HorizontalRepeat, s.VerticalRepeat, s.Brightness, s.ExposureEv, s.LightStrength));
        if (bad == "fills") Assert.Equal(2, s.FillLightCount);
    }

    [Fact] // [EDGE-per-backdrop-settings-1]
    public void Copy_IsDeep()
    {
        var a = new BackdropSettings();
        var b = a.Copy();
        Assert.NotSame(a.Sky, b.Sky);
        Assert.NotSame(a.Room, b.Room);
        b.Sky.Radius = 500;
        Assert.Equal(20000f, a.Sky.Radius);
    }

    [Fact] // [AC-per-backdrop-settings-6]
    public void BackdropSettings_HasExactlyTheFourteenFields()
    {
        var names = typeof(BackdropSettings).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Brightness", "ExposureEv", "FillLightCount", "FlipHorizontal", "HorizontalRepeat", "LightStrength", "Mode",
                             "NightWhileShown", "Room", "SceneLighting", "Sky", "SunOffWhileShown", "ToneMap", "VerticalRepeat" }, names);
        Assert.DoesNotContain("MaxTextureWidth", names);
        Assert.DoesNotContain("Delivery", names);
    }

    [Fact] // [AC-per-backdrop-settings-1]
    public void Defaults_MatchConfiguration()
    {
        var c = new Configuration();
        var s = new BackdropSettings();
        Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(c.CaptureLive()), Newtonsoft.Json.JsonConvert.SerializeObject(s));
    }
}
