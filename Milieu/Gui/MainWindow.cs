using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu.Gui;

/// <summary>
/// The one window: a preview grid at the top (click to apply), "Add more…", the Sky/Room choice, and a Settings panel
/// that starts collapsed. All decisions come from the pure view models; ImGui draws off the framework thread, so every
/// action is posted to the controller and runs at the start of the next framework tick.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly BackdropController controller;
    private readonly Configuration config;
    private readonly Action save;
    private readonly SceneGridViewModel grid;
    private readonly ThumbnailCache thumbs;
    private readonly FileDialogManager files = new();

    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.85f, 0.3f, 1f);
    private static readonly Vector2 TileSize = new(176, 88);

    public MainWindow(BackdropController controller, Configuration config, Action save, SceneGridViewModel grid, ThumbnailCache thumbs)
        : base($"Milieu (build {Plugin.BuildNumber})###MilieuMain")
    {
        this.controller = controller;
        this.config = config;
        this.save = save;
        this.grid = grid;
        this.thumbs = thumbs;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 360), MaximumSize = new Vector2(2400, 2400) };
    }

    /// <summary>Dalamud's config button: open with the Settings panel expanded.</summary>
    public void OpenSettings()
    {
        IsOpen = true;
        grid.RequestSettingsExpanded();
    }

    public override void Draw()
    {
        var vm = new BackdropViewModel(controller.Status, controller.UiBlocker, config);
        grid.OnDraw();
        DrawStatus(vm);
        DrawGrid();
        DrawModes(vm);
        DrawSettings(vm);
        files.Draw();
    }

    private static void DrawStatus(BackdropViewModel vm)
    {
        if (vm.StatusText.Length > 0 && !vm.IsConverting)
            ImGui.TextColored(vm.StatusSeverity == Severity.Error ? Red : vm.StatusSeverity == Severity.Warning ? Yellow : Vector4.One, vm.StatusText);
        foreach (var w in vm.WarningTexts) ImGui.TextColored(Yellow, w);
    }

    private void DrawGrid()
    {
        ImGui.TextDisabled(SceneGridViewModel.Header);
        var size = TileSize * ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var pad = ImGui.GetStyle().FramePadding * 2;
        var perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (size.X + pad.X + spacing)));
        var tiles = grid.Tiles;
        thumbs.Retain(config.UserImages);
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine();
            DrawTile(tiles[i], size);
        }
        if (ImGui.Button("Add more…"))
            files.OpenFileDialog("Choose a panorama", "Panoramas{.hdr,.png,.jpg,.jpeg}", (ok, path) => { if (ok) grid.AddMore(path); });
        if (grid.ShowDownloadAll)
        {
            ImGui.SameLine();
            if (ImGui.Button("Download all")) grid.DownloadAll();
        }
        ImGui.Spacing();
    }

    private void DrawTile(Tile tile, Vector2 size)
    {
        using var id = ImRaii.PushId(tile.Id);
        using var group = ImRaii.Group();
        var start = ImGui.GetCursorScreenPos();
        var wrap = thumbs.Get(tile);
        bool clicked;
        if (wrap != null) clicked = ImGui.ImageButton(wrap.Handle, size, Vector2.Zero, Vector2.One);
        else clicked = ImGui.Button(tile.Kind == TileKind.None ? "None" : tile.Label, size + ImGui.GetStyle().FramePadding * 2);
        if (clicked) grid.Click(tile);
        if (ImGui.IsItemHovered() && tile.Kind == TileKind.User) ImGui.SetTooltip(tile.Id);

        var draw = ImGui.GetWindowDrawList();
        var end = start + size + ImGui.GetStyle().FramePadding * 2;
        if (tile.Selected) draw.AddRect(start, end, ImGui.GetColorU32(new Vector4(1f, 0.75f, 0.2f, 1f)), 4, ImDrawFlags.None, 3 * ImGuiHelpers.GlobalScale);
        if (tile.Overlay.Length > 0)
        {
            var textSize = ImGui.CalcTextSize(tile.Overlay);
            var at = new Vector2(start.X + (end.X - start.X - textSize.X) / 2, end.Y - textSize.Y - 6);
            draw.AddRectFilled(at - new Vector2(4, 2), at + textSize + new Vector2(4, 2), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.65f)), 3);
            draw.AddText(at, ImGui.GetColorU32(Vector4.One), tile.Overlay);
        }
        if (tile.CanRemove)
        {
            ImGui.SetCursorScreenPos(new Vector2(end.X - 22 * ImGuiHelpers.GlobalScale, start.Y + 2));
            if (ImGui.SmallButton("×")) grid.Remove(tile);
            ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        }
        ImGui.TextUnformatted(tile.Kind == TileKind.None ? "None" : tile.Label);
    }

    private void DrawModes(BackdropViewModel vm)
    {
        var mode = (int)config.Mode;
        var changed = ImGui.RadioButton("Sky dome", ref mode, (int)DomeMode.Sky);
        ImGui.SameLine();
        changed |= ImGui.RadioButton("Room dome", ref mode, (int)DomeMode.Room);
        if (changed)
        {
            config.Mode = (DomeMode)mode;
            save();
            controller.Post(controller.ApplyRebuild);   // one swap at the new mode's settings and placement
        }
        if (vm.ShowRoomHint) ImGui.TextColored(Yellow, BackdropViewModel.RoomHint);
    }

    private void DrawSettings(BackdropViewModel vm)
    {
        if (grid.ConsumeSettingsExpand()) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        if (!ImGui.CollapsingHeader("Settings")) return;

        var d = config.Current;
        var (rMin, rMax) = DomeSettings.RadiusRange(config.Mode);
        var (hMin, hMax) = DomeSettings.HeightRange(config.Mode);
        Slider("Radius", "Radius", d.Radius, rMin, rMax, v => d.Radius = v, "%.1f");
        Slider("Vertical scale", "VerticalScale", d.VerticalScale, DomeSettings.VerticalScaleRange.Min, DomeSettings.VerticalScaleRange.Max, v => d.VerticalScale = v);
        Slider(d.GroundProjection ? "Camera height" : "Height", "Height", d.HeightOffset, hMin, hMax, v => d.HeightOffset = v);
        if (d.GroundProjection && ImGui.IsItemHovered()) ImGui.SetTooltip("Distance from the floor to the dome centre.");
        Slider("Yaw", "Yaw", d.YawDegrees, DomeSettings.YawRange.Min, DomeSettings.YawRange.Max, v => d.YawDegrees = v, "%.0f°");
        var projection = d.GroundProjection;
        if (ImGui.Checkbox("Ground projection", ref projection))
        {
            d.GroundProjection = projection;
            save();
            controller.Post(controller.ApplyRebuild);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flattens the bottom of the dome into a floor, so the image's ground lies at your feet.");
        if (d.ProjectionInactive) ImGui.TextColored(Yellow, d.ProjectionStatus());
        else ImGui.TextDisabled(d.ProjectionStatus());

        ImGui.Separator();
        Slider("Horizontal repeat", "HorizontalRepeat", config.HorizontalRepeat, Configuration.RepeatRange.Min, Configuration.RepeatRange.Max, v => config.HorizontalRepeat = v);
        Slider("Vertical repeat", "VerticalRepeat", config.VerticalRepeat, Configuration.RepeatRange.Min, Configuration.RepeatRange.Max, v => config.VerticalRepeat = v);
        var flip = config.FlipHorizontal;
        if (ImGui.Checkbox("Flip horizontally", ref flip))
        {
            config.FlipHorizontal = flip;
            save();
            controller.Post(controller.ApplyRebuild);
        }
        Slider("Brightness", "Brightness", config.Brightness, Configuration.BrightnessRange.Min, Configuration.BrightnessRange.Max, v => config.Brightness = v);

        ImGui.Separator();
        if (vm.IsBundledSelection) ImGui.TextDisabled(Messages.Template(MessageKey.BundledNote));
        using (ImRaii.Disabled(!vm.HdrControlsEnabled))
        {
            var ev = config.ExposureEv;
            if (ImGui.SliderFloat("Exposure (EV)", ref ev, Configuration.ExposureRange.Min, Configuration.ExposureRange.Max, "%.1f")) config.ExposureEv = ev;
            if (ImGui.IsItemDeactivatedAfterEdit()) { save(); controller.Post(controller.RequestReconvert); }
            var op = (int)config.ToneMap;
            if (ImGui.Combo("Tone map", ref op, "None\0Reinhard\0ACES\0"))
            {
                config.ToneMap = (ToneMapOperator)op;
                save();
                controller.Post(controller.RequestReconvert);
            }
        }
        using (ImRaii.Disabled(vm.IsBundledSelection))
        {
            var size = config.MaxTextureWidth == 4096 ? 1 : 0;
            if (ImGui.Combo("Texture size", ref size, "8192 x 4096\04096 x 2048\0"))
            {
                config.MaxTextureWidth = size == 1 ? 4096 : 8192;
                save();
                controller.Post(controller.RequestReconvert);
            }
        }

        ImGui.Separator();
        using (ImRaii.Disabled(!vm.CanRecenter)) if (ImGui.Button("Re-centre on me")) controller.Post(controller.Recenter);
        if (grid.ShowCreditsLink)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Credits")) Dalamud.Utility.Util.OpenLink(BackdropPins.CreditsUrl);
        }
    }

    /// <summary>Every slider applies once, on release (single-swap rule: placement, height or rebuild).</summary>
    private void Slider(string label, string key, float value, float min, float max, Action<float> set, string format = "%.2f")
    {
        var v = value;
        if (ImGui.SliderFloat(label, ref v, min, max, format)) set(Math.Clamp(v, min, max));
        if (!ImGui.IsItemDeactivatedAfterEdit()) return;
        save();
        controller.Post(BackdropViewModel.ApplyOf(key) switch
        {
            SliderApply.Placement => controller.ApplyPlacement,
            SliderApply.Height => controller.ApplyHeight,
            _ => controller.ApplyRebuild,
        });
    }

    public void Dispose() => thumbs.Dispose();
}
