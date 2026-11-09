using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Milieu.Assets;
using Dalamud.Configuration;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu;

/// <summary>
/// Persisted settings (versioned; see <see cref="CurrentVersion"/> and <see cref="Migrate"/>). The shown state and the dome centre are deliberately NOT saved: the backdrop is
/// never shown automatically on load. Values are clamped on load, NaN replaced with defaults.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The selected scene: null = None, "bundled:&lt;stem&gt;", or a user image path. Never logged.</summary>
    public string? ImagePath { get; set; }

    /// <summary>The user's own images shown as tiles, in the order added (paths; never logged).</summary>
    public List<string> UserImages { get; set; } = [];

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

    /// <summary>Who sees the backdrop: only the user (default), or PSync pairs through an item (experimental).</summary>
    public Sync.DeliveryMode Delivery { get; set; } = Sync.DeliveryMode.Local;

    /// <summary>The one-time notice that a user image is uploaded through PSync has been shown.</summary>
    public bool SyncUploadNoticeShown { get; set; }

    /// <summary>
    /// Scene lighting (docs/adr/0009): add game lights in GPose that approximate the shown HDR image's light. On by default
    /// (owner, 2026-11-09), also for a config saved without the field; it only acts on .hdr/.exr images, in GPose.
    /// </summary>
    public bool SceneLighting { get; set; } = true;

    /// <summary>Scales every scene light (0 = no lights).</summary>
    public float LightStrength { get; set; } = 1f;

    /// <summary>How many coloured fill lights accompany the key light.</summary>
    public int FillLightCount { get; set; } = 2;

    public static readonly (float Min, float Max) RepeatRange = (0.5f, 4f);
    public static readonly (float Min, float Max) BrightnessRange = (0f, 2f);
    public static readonly (float Min, float Max) ExposureRange = (-10f, 10f);
    public static readonly (float Min, float Max) LightStrengthRange = (0f, 2f);
    public static readonly (int Min, int Max) FillLightRange = (0, 3);

    public DomeSettings ForMode(DomeMode mode) => mode == DomeMode.Sky ? Sky : Room;

    public DomeSettings Current => ForMode(Mode);

    /// <summary>
    /// Upgrades older versions in place, as ordered steps (a later version adds its own step). Steps change only
    /// untouched defaults, except v3/v4, which apply defaults chosen in game to configs written by unreleased dev builds.
    /// </summary>
    public void Migrate(string configDirectory)
    {
        if (Version < 2)
        {
            // 1. A bundled JPEG path from v1 becomes its bundled id; 2. any other image becomes one of the user's tiles.
            if (!string.IsNullOrWhiteSpace(ImagePath))
            {
                var bundledDir = Path.GetFullPath(Path.Combine(configDirectory, "DefaultBackdrops"));
                string? dir = null;
                try { dir = Path.GetDirectoryName(Path.GetFullPath(ImagePath)); } catch (Exception) { /* not a path */ }
                var stem = Path.GetFileNameWithoutExtension(ImagePath);
                if (dir != null && string.Equals(dir.TrimEnd('\\', '/'), bundledDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    && BackdropPins.Find(stem) is { } pin)
                    ImagePath = BackdropPins.IdOf(pin.Stem);
                else if (!UserImages.Contains(ImagePath, StringComparer.OrdinalIgnoreCase))
                    UserImages.Add(ImagePath);
            }
            // 3. The untouched old sky height (0) moves up to the new default; 4. ground-projection defaults.
            if (Sky != null && Sky.HeightOffset == 0) Sky.HeightOffset = DomeSettings.SkyDefaults().HeightOffset;
            if (Room != null) Room.GroundProjection = true;
            if (Sky != null) Sky.GroundProjection = false;
            Version = 2;
        }
        if (Version < 3)
        {
            // Seen in game: the sky dome stayed a sphere sinking below you. Ground projection becomes the sky default too,
            // so the dome's floor is at your feet (v2 had it off by default; nobody had chosen it).
            if (Sky != null) Sky.GroundProjection = true;
            Version = 3;
        }
        if (Version < 4)
        {
            // Chosen in game: eye height 1.6 is the right camera height for the sky dome too. The old untouched default
            // (400, or 0 from v1 which step 2 raised) moves to it; a height the user set stays.
            if (Sky != null && Sky.HeightOffset is 400f or 0f) Sky.HeightOffset = DomeSettings.SkyDefaults().HeightOffset;
            Version = 4;
        }
        if (Version < CurrentVersion) Version = CurrentVersion;
    }

    /// <param name="syncedAvailable">False when the kill switch (Plugin.SyncedAvailable) is off: a saved synced choice then falls back to Only me.</param>
    public void Clamp(bool syncedAvailable = true)
    {
        if (!Enum.IsDefined(Delivery) || (!syncedAvailable && Delivery != Sync.DeliveryMode.Local)) Delivery = Sync.DeliveryMode.Local;
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
        LightStrength = DomeSettings.Fix(LightStrength, LightStrengthRange, 1f);
        if (FillLightCount < FillLightRange.Min || FillLightCount > FillLightRange.Max) FillLightCount = 2;
        if (ImagePath != null && ImagePath.Trim().Length == 0) ImagePath = null;
        if (BackdropPins.IsBundledId(ImagePath) && !BackdropPins.TryParseId(ImagePath, out _)) ImagePath = null;
        UserImages = (UserImages ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
                                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
