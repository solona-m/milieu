using System;
using Dalamud.Configuration;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu;

/// <summary>
/// Persisted settings (Version 1). The shown state and the dome centre are deliberately NOT saved: the backdrop is
/// never shown automatically on load. Values are clamped on load, NaN replaced with defaults.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>A user file or <c>&lt;config&gt;/DefaultBackdrops/&lt;name&gt;</c>. Never logged.</summary>
    public string? ImagePath { get; set; }

    public DomeMode Mode { get; set; } = DomeMode.Sky;
    public DomeSettings Sky { get; set; } = DomeSettings.SkyDefaults();
    public DomeSettings Room { get; set; } = DomeSettings.RoomDefaults();

    public float HorizontalRepeat { get; set; } = 2f;
    public float VerticalRepeat { get; set; } = 1f;
    public bool FlipHorizontal { get; set; }
    public float Brightness { get; set; } = 1f;

    public float ExposureEv { get; set; }
    public ToneMapOperator ToneMap { get; set; } = ToneMapOperator.Aces;

    public int MaxTextureWidth { get; set; } = 8192;

    public static readonly (float Min, float Max) RepeatRange = (0.5f, 4f);
    public static readonly (float Min, float Max) BrightnessRange = (0f, 2f);
    public static readonly (float Min, float Max) ExposureRange = (-10f, 10f);

    public DomeSettings ForMode(DomeMode mode) => mode == DomeMode.Sky ? Sky : Room;

    public DomeSettings Current => ForMode(Mode);

    /// <summary>Upgrades older versions in place. Version 1 is the first, so there is nothing to do yet.</summary>
    public void Migrate()
    {
        if (Version < CurrentVersion) Version = CurrentVersion;
    }

    public void Clamp()
    {
        if (!Enum.IsDefined(Mode)) Mode = DomeMode.Sky;
        Sky ??= DomeSettings.SkyDefaults();
        Room ??= DomeSettings.RoomDefaults();
        Sky.Clamp(DomeMode.Sky);
        Room.Clamp(DomeMode.Room);
        HorizontalRepeat = DomeSettings.Fix(HorizontalRepeat, RepeatRange, 2f);
        VerticalRepeat = DomeSettings.Fix(VerticalRepeat, RepeatRange, 1f);
        Brightness = DomeSettings.Fix(Brightness, BrightnessRange, 1f);
        ExposureEv = DomeSettings.Fix(ExposureEv, ExposureRange, 0f);
        if (!Enum.IsDefined(ToneMap)) ToneMap = ToneMapOperator.Aces;
        if (MaxTextureWidth is not (4096 or 8192)) MaxTextureWidth = 8192;
        if (ImagePath != null && ImagePath.Trim().Length == 0) ImagePath = null;
    }
}
