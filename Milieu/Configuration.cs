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
/// <para>
/// Per-backdrop settings (docs/adr/0014): the flat per-backdrop fields (mode, both dome sets, repeats, flip, brightness,
/// exposure, tone map, scene lighting, strength, fills, night, sun off) are the selected backdrop's working copy, which every
/// reader uses and the panel edits. <see cref="Backdrops"/> holds one entry per backdrop, keyed by its <see cref="ImagePath"/>
/// id ("bundled:&lt;stem&gt;" or the user path exactly as in <see cref="UserImages"/>, case-insensitive; never logged).
/// Selecting stores the working copy into the old entry and loads the new one; every save stores the working copy into the
/// selected entry, so <see cref="ImagePath"/> changes only inside this class.
/// </para>
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 6;

    private readonly object gate = new();

    /// <summary>Guards the per-backdrop store against a draw-thread save racing a framework-thread switch (not serialized).</summary>
    internal object Gate => gate;

    /// <summary>Bumped whenever the selected backdrop changes (not serialized): an off→on flag change across it isn't a retry.</summary>
    internal int SelectionGeneration { get; private set; }

    /// <summary>Each backdrop's remembered settings, keyed by its id (see the class doc). Never logged.</summary>
    public Dictionary<string, BackdropSettings> Backdrops { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Night while the backdrop is shown (docs/adr/0012): hold the local time of day at night while a dome is drawn, so the
    /// game's sun doesn't compete with the backdrop's lights. Off by default; a config saved without it loads off.
    /// </summary>
    public bool NightWhileShown { get; set; }

    /// <summary>
    /// Turn off the game's sun (docs/adr/0013): zero the game's sun and moon light colour while a backdrop is shown to "Only
    /// me". On by default (the owner asked for it, 2026-11-16), also for a config saved without the field; release-gated in
    /// ADR 0013.
    /// </summary>
    public bool SunOffWhileShown { get; set; } = true;

    public static readonly (float Min, float Max) RepeatRange = (0.5f, 4f);
    public static readonly (float Min, float Max) BrightnessRange = (0f, 2f);
    public static readonly (float Min, float Max) ExposureRange = (-10f, 10f);
    public static readonly (float Min, float Max) LightStrengthRange = (0f, 2f);
    public static readonly (int Min, int Max) FillLightRange = (0, 3);

    public DomeSettings ForMode(DomeMode mode) => mode == DomeMode.Sky ? Sky : Room;

    public DomeSettings Current => ForMode(Mode);

    /// <summary>A deep copy of the working copy.</summary>
    public BackdropSettings CaptureLive() => new()
    {
        Mode = Mode, Sky = (Sky ?? DomeSettings.SkyDefaults()).Copy(), Room = (Room ?? DomeSettings.RoomDefaults()).Copy(),
        HorizontalRepeat = HorizontalRepeat, VerticalRepeat = VerticalRepeat, FlipHorizontal = FlipHorizontal, Brightness = Brightness,
        ExposureEv = ExposureEv, ToneMap = ToneMap,
        SceneLighting = SceneLighting, LightStrength = LightStrength, FillLightCount = FillLightCount,
        NightWhileShown = NightWhileShown, SunOffWhileShown = SunOffWhileShown,
    };

    /// <summary>
    /// Loads <paramref name="s"/> into the working copy. The dome sets are copied into the existing instances, so a
    /// <see cref="Current"/> reference held by a drawing frame isn't orphaned.
    /// </summary>
    public void ApplyLive(BackdropSettings s)
    {
        Mode = s.Mode;
        Sky ??= DomeSettings.SkyDefaults();
        Room ??= DomeSettings.RoomDefaults();
        Sky.CopyFrom(s.Sky ?? DomeSettings.SkyDefaults());
        Room.CopyFrom(s.Room ?? DomeSettings.RoomDefaults());
        HorizontalRepeat = s.HorizontalRepeat;
        VerticalRepeat = s.VerticalRepeat;
        FlipHorizontal = s.FlipHorizontal;
        Brightness = s.Brightness;
        ExposureEv = s.ExposureEv;
        ToneMap = s.ToneMap;
        SceneLighting = s.SceneLighting;
        LightStrength = s.LightStrength;
        FillLightCount = s.FillLightCount;
        NightWhileShown = s.NightWhileShown;
        SunOffWhileShown = s.SunOffWhileShown;
    }

    /// <summary>Stores the working copy into the selected backdrop's entry (nothing when None is selected).</summary>
    public void StoreSelected()
    {
        lock (gate)
        {
            Backdrops ??= new(StringComparer.OrdinalIgnoreCase);
            if (ImagePath != null) Backdrops[ImagePath] = CaptureLive();
        }
    }

    /// <summary>
    /// Selects a backdrop (null = None). The same id is a no-op. Otherwise the working copy is stored into the old entry, the
    /// selection changes and the generation is bumped; then the new entry is loaded, or (first time) created from the
    /// working copy.
    /// </summary>
    public void SelectBackdrop(string? id)
    {
        lock (gate)
        {
            if (string.Equals(ImagePath, id, StringComparison.OrdinalIgnoreCase)) return;
            StoreSelected();
            ImagePath = id;
            SelectionGeneration++;
            if (id == null) return;
            if (Backdrops.TryGetValue(id, out var entry) && entry != null) ApplyLive(entry);
            else Backdrops[id] = CaptureLive();
        }
    }

    /// <summary>Adds a user image tile, or returns the existing one with the same full path. Returns its id.</summary>
    public string AddUserImage(string path)
    {
        lock (gate)
        {
            var existing = UserImages.FirstOrDefault(p => SameFullPath(p, path));
            if (existing != null) return existing;
            UserImages.Add(path);
            return path;
        }
    }

    private static bool SameFullPath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>Removes a user image tile and forgets its settings. If it was selected, None is selected (nothing stored). Returns whether it was selected.</summary>
    public bool RemoveUserImage(string path)
    {
        lock (gate)
        {
            UserImages.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            Backdrops.Remove(path);
            if (!string.Equals(ImagePath, path, StringComparison.OrdinalIgnoreCase)) return false;
            ImagePath = null;
            SelectionGeneration++;
            return true;
        }
    }

    /// <summary>Stores the working copy for every known backdrop (the selection, the user's images, the bundled scenes) without an entry.</summary>
    internal void SeedMissing()
    {
        lock (gate)
        {
            Backdrops ??= new(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>();
            if (ImagePath != null) ids.Add(ImagePath);
            ids.AddRange(UserImages ?? []);
            ids.AddRange(BackdropPins.All.Select(p => BackdropPins.IdOf(p.Stem)));
            foreach (var id in ids)
                if (!Backdrops.ContainsKey(id)) Backdrops[id] = CaptureLive();
        }
    }

    /// <summary>
    /// Upgrades older versions in place, as ordered steps (a later version adds its own step). Steps change only
    /// untouched defaults, except v3/v4, which apply defaults chosen in game to configs written by unreleased dev builds, and
    /// v6, which resets a camera height whose meaning changed (docs/adr/0015).
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
        if (Version < 5)
        {
            // Per-backdrop settings (docs/adr/0014): nothing to change here. Clamp seeds every known backdrop from the clamped
            // working copy when the store is empty, so seeds never carry the invalid values Migrate may still see.
            Version = 5;
        }
        if (Version < 6)
        {
            // Half dome on the floor (docs/adr/0015): the dome now always stands on the floor, and the height is only the
            // camera height. A height in (0, 0.25) with projection on was the old way to lower the dome to the floor; under
            // the new meaning it puts the camera a few centimetres off the floor and squashes the image's floor to a dot
            // (the reported smear), so it goes back to eye height. A height <= 0 is inactive and draws the same full sphere
            // before and after, so it stays; so do projection-off sets and every height >= 0.25.
            static void Reset(DomeSettings? d)
            {
                if (d is { GroundProjection: true, HeightOffset: > 0f and < DomeSettings.MinCameraHeight }) d.HeightOffset = 1.6f;
            }
            Reset(Sky);
            Reset(Room);
            foreach (var entry in (Backdrops ?? []).Values)
            {
                if (entry == null) continue;
                Reset(entry.Sky);
                Reset(entry.Room);
            }
            Version = 6;
        }
        if (Version < CurrentVersion) Version = CurrentVersion;
    }

    /// <param name="syncedAvailable">False when the kill switch (Plugin.SyncedAvailable) is off: a saved synced choice then falls back to Only me.</param>
    public void Clamp(bool syncedAvailable = true)
    {
        lock (gate)
        {
            // 1. Global settings.
            if (!Enum.IsDefined(Delivery) || (!syncedAvailable && Delivery != Sync.DeliveryMode.Local)) Delivery = Sync.DeliveryMode.Local;
            if (MaxTextureWidth is not (4096 or 8192)) MaxTextureWidth = 8192;
            if (ImagePath != null && ImagePath.Trim().Length == 0) ImagePath = null;
            if (BackdropPins.IsBundledId(ImagePath) && !BackdropPins.TryParseId(ImagePath, out _)) ImagePath = null;
            UserImages = (UserImages ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
                                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // 2. The working copy, through the per-backdrop bounds (null dome sets become defaults).
            Sky ??= DomeSettings.SkyDefaults();
            Room ??= DomeSettings.RoomDefaults();
            var live = CaptureLive();
            live.Clamp();
            ApplyLive(live);

            // 3. The entries: null values and blank keys dropped, keys of no known backdrop pruned, values clamped, rebuilt
            // case-insensitive (the first key wins a case collision).
            var known = new HashSet<string>(UserImages, StringComparer.OrdinalIgnoreCase);
            if (ImagePath != null) known.Add(ImagePath);
            var rebuilt = new Dictionary<string, BackdropSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in Backdrops ?? [])
            {
                if (value == null || string.IsNullOrWhiteSpace(key) || rebuilt.ContainsKey(key)) continue;
                var pinned = BackdropPins.IsBundledId(key) && BackdropPins.TryParseId(key, out _);
                if (!pinned && !known.Contains(key)) continue;
                value.Clamp();
                rebuilt[key] = value;
            }
            Backdrops = rebuilt;

            // 4. A store that is empty (an upgrade, or after an older build's save) is seeded from the clamped working copy.
            if (Backdrops.Count == 0) SeedMissing();

            // 5. On load the working copy wins: it is what the user last saw.
            StoreSelected();
        }
    }
}
