using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu.Gui;

/// <summary>
/// The one window. All enable/disable and text decisions come from the pure view models. ImGui draws off the framework
/// thread, so every action is posted to the controller and runs at the start of the next framework tick.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly BackdropController controller;
    private readonly Configuration config;
    private readonly Action save;
    private readonly BackdropListViewModel backdrops;
    private readonly FileDialogManager files = new();
    private bool expandSettings;

    /// <summary>Dalamud's config button (/xlplugins): open the window with the settings panels expanded.</summary>
    public void OpenSettings()
    {
        IsOpen = true;
        expandSettings = true;
    }

    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.85f, 0.3f, 1f);

    public MainWindow(BackdropController controller, Configuration config, Action save, BackdropListViewModel backdrops)
        : base($"Milieu (build {Plugin.BuildNumber})###MilieuMain")
    {
        this.controller = controller;
        this.config = config;
        this.save = save;
        this.backdrops = backdrops;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 420), MaximumSize = new Vector2(2000, 2000) };
    }

    public override void Draw()
    {
        var vm = new BackdropViewModel(controller.Status, controller.UiBlocker, config);
        DrawStatus(vm);
        ImGui.Separator();
        DrawImage();
        DrawBackdrops();
        ImGui.Separator();
        DrawDome(vm);
        DrawMapping();
        DrawHdr(vm);
        ImGui.Separator();
        DrawButtons(vm);
        files.Draw();
        expandSettings = false;
    }

    private static void DrawStatus(BackdropViewModel vm)
    {
        if (vm.IsConverting) ImGui.ProgressBar(vm.Progress, new Vector2(-1, 0), vm.StatusText);
        else if (vm.StatusText.Length > 0)
            ImGui.TextColored(vm.StatusSeverity == Severity.Error ? Red : vm.StatusSeverity == Severity.Warning ? Yellow : Vector4.One, vm.StatusText);
        foreach (var w in vm.WarningTexts) ImGui.TextColored(Yellow, w);
    }

    private void DrawImage()
    {
        ImGui.TextUnformatted("Image:");
        ImGui.SameLine();
        ImGui.TextUnformatted(new BackdropViewModel(controller.Status, MessageKey.None, config).ImageFileName);
        if (config.ImagePath != null && ImGui.IsItemHovered()) ImGui.SetTooltip(config.ImagePath);
        ImGui.SameLine();
        if (ImGui.Button("Browse…"))
            files.OpenFileDialog("Choose a panorama", "Panoramas{.hdr,.png,.jpg,.jpeg}", (ok, path) =>
            {
                if (!ok) return;
                config.ImagePath = path;
                save();
            });
    }

    private void DrawBackdrops()
    {
        if (!ImGui.CollapsingHeader(BackdropListViewModel.Header + "###Backdrops")) return;
        backdrops.OnDraw();
        foreach (var e in backdrops.Entries(config.ImagePath))
        {
            using (ImRaii.Disabled(!backdrops.CanSelect(e)))
            {
                if (ImGui.Selectable($"{e.DisplayName}##{e.Name}", e.Selected) && backdrops.Select(e, config)) save();
            }
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 100 * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled(e.StateText);
        }
        if (backdrops.StatusLine is { } line) ImGui.TextUnformatted(line);
        if (backdrops.ShowTryAgain && ImGui.Button("Try again")) backdrops.TryAgain();
        if (backdrops.ShowCreditsLink && ImGui.SmallButton("Credits")) Dalamud.Utility.Util.OpenLink(BackdropListViewModel.CreditsUrl);
    }

    private void DrawDome(BackdropViewModel vm)
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

        var d = config.Current;
        var (rMin, rMax) = DomeSettings.RadiusRange(config.Mode);
        var (hMin, hMax) = DomeSettings.HeightRange(config.Mode);
        Slider("Radius", "Radius", d.Radius, rMin, rMax, v => d.Radius = v, "%.1f");
        Slider("Vertical scale", "VerticalScale", d.VerticalScale, DomeSettings.VerticalScaleRange.Min, DomeSettings.VerticalScaleRange.Max, v => d.VerticalScale = v);
        Slider("Height", "Height", d.HeightOffset, hMin, hMax, v => d.HeightOffset = v);
        Slider("Yaw", "Yaw", d.YawDegrees, DomeSettings.YawRange.Min, DomeSettings.YawRange.Max, v => d.YawDegrees = v, "%.0f°");
    }

    private void DrawMapping()
    {
        if (expandSettings) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        if (!ImGui.CollapsingHeader("Mapping")) return;
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
    }

    private void DrawHdr(BackdropViewModel vm)
    {
        if (expandSettings) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        if (!ImGui.CollapsingHeader("Image conversion")) return;
        using (ImRaii.Disabled(!vm.HdrControlsEnabled))
        {
            var ev = config.ExposureEv;
            if (ImGui.SliderFloat("Exposure (EV)", ref ev, Configuration.ExposureRange.Min, Configuration.ExposureRange.Max, "%.1f")) config.ExposureEv = ev;
            if (ImGui.IsItemDeactivatedAfterEdit()) save();
            var op = (int)config.ToneMap;
            if (ImGui.Combo("Tone map", ref op, "None\0Reinhard\0ACES\0"))
            {
                config.ToneMap = (ToneMapOperator)op;
                save();
            }
        }
        var size = config.MaxTextureWidth == 4096 ? 1 : 0;
        if (ImGui.Combo("Texture size", ref size, "8192 x 4096\04096 x 2048\0"))
        {
            config.MaxTextureWidth = size == 1 ? 4096 : 8192;
            save();
        }
        ImGui.TextDisabled("Exposure, tone map and size apply the next time you press Show/Apply.");
    }

    private void DrawButtons(BackdropViewModel vm)
    {
        using (ImRaii.Disabled(!vm.CanShow)) if (ImGui.Button(vm.ShowButtonLabel)) controller.Post(controller.Show);
        ImGui.SameLine();
        using (ImRaii.Disabled(!vm.CanHide)) if (ImGui.Button("Hide")) controller.Post(controller.Hide);
        ImGui.SameLine();
        using (ImRaii.Disabled(!vm.CanRecenter)) if (ImGui.Button("Re-centre on me")) controller.Post(controller.Recenter);
    }

    /// <summary>A slider that applies live (yaw, height) or on release (everything baked into the VFX).</summary>
    private void Slider(string label, string key, float value, float min, float max, Action<float> set, string format = "%.2f")
    {
        var v = value;
        if (ImGui.SliderFloat(label, ref v, min, max, format))
        {
            set(Math.Clamp(v, min, max));
        }
        if (!ImGui.IsItemDeactivatedAfterEdit()) return;
        save();
        controller.Post(BackdropViewModel.ApplyOf(key) switch
        {
            SliderApply.Placement => controller.ApplyPlacement,
            SliderApply.Height => controller.ApplyHeight,
            _ => controller.ApplyRebuild,
        });
    }
}
