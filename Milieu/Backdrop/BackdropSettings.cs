using System;
using Milieu.Imaging;

namespace Milieu.Backdrop;

/// <summary>
/// The settings one backdrop remembers (docs/adr/0014): its dome sets and mode, the image look, scene lighting, sun off and
/// night. Defaults equal <see cref="Configuration"/>'s. Texture size and "Visible to" stay global.
/// </summary>
[Serializable]
public sealed class BackdropSettings
{
    public DomeMode Mode { get; set; } = DomeMode.Sky;
    public DomeSettings Sky { get; set; } = DomeSettings.SkyDefaults();
    public DomeSettings Room { get; set; } = DomeSettings.RoomDefaults();

    public float HorizontalRepeat { get; set; } = 2f;
    public float VerticalRepeat { get; set; } = 1f;
    public bool FlipHorizontal { get; set; }
    public float Brightness { get; set; } = 1f;

    public float ExposureEv { get; set; }
    public ToneMapOperator ToneMap { get; set; } = ToneMapOperator.Aces;

    public bool SceneLighting { get; set; } = true;
    public float LightStrength { get; set; } = 1f;
    public int FillLightCount { get; set; } = 2;

    public bool NightWhileShown { get; set; }
    public bool SunOffWhileShown { get; set; } = true;

    /// <summary>A deep copy: new dome sets, so entries never alias each other or the working copy.</summary>
    public BackdropSettings Copy()
    {
        var c = (BackdropSettings)MemberwiseClone();
        c.Sky = (Sky ?? DomeSettings.SkyDefaults()).Copy();
        c.Room = (Room ?? DomeSettings.RoomDefaults()).Copy();
        return c;
    }

    /// <summary>The bounds of the global settings, held once: NaN and out-of-range values fall back as on load.</summary>
    public void Clamp()
    {
        if (!Enum.IsDefined(Mode)) Mode = DomeMode.Sky;
        Sky ??= DomeSettings.SkyDefaults();
        Room ??= DomeSettings.RoomDefaults();
        Sky.Clamp(DomeMode.Sky);
        Room.Clamp(DomeMode.Room);
        HorizontalRepeat = DomeSettings.Fix(HorizontalRepeat, Configuration.RepeatRange, 2f);
        VerticalRepeat = DomeSettings.Fix(VerticalRepeat, Configuration.RepeatRange, 1f);
        Brightness = DomeSettings.Fix(Brightness, Configuration.BrightnessRange, 1f);
        ExposureEv = DomeSettings.Fix(ExposureEv, Configuration.ExposureRange, 0f);
        if (!Enum.IsDefined(ToneMap)) ToneMap = ToneMapOperator.Aces;
        LightStrength = DomeSettings.Fix(LightStrength, Configuration.LightStrengthRange, 1f);
        if (FillLightCount < Configuration.FillLightRange.Min || FillLightCount > Configuration.FillLightRange.Max) FillLightCount = 2;
    }
}
