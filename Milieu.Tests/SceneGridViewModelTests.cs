using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Gui;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class SceneGridViewModelTests
{
    private readonly Configuration config = new();
    private readonly FakeGridController controller = new();
    private readonly FakeGridDownloads downloads = new();
    private readonly List<Action> posted = [];
    private readonly HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
    private int saves;
    private readonly SceneGridViewModel vm;

    public SceneGridViewModelTests() =>
        vm = new SceneGridViewModel(config, () => saves++, posted.Add, controller, downloads, existing.Contains);

    /// <summary>Runs what the view model posted, as the framework tick would.</summary>
    private void RunPosted() { var a = posted.ToList(); posted.Clear(); foreach (var x in a) x(); }

    private Tile T(string id) => vm.Tiles.Single(t => t.Id == id);

    [Fact] // [AC-hdri-backdrop-11]
    public void BundledTiles_EightWithDisplayNamesAndEmbeddedThumbKeys()
    {
        var bundled = vm.Tiles.Where(t => t.Kind == TileKind.Bundled).ToList();
        Assert.Equal(new[] { "Forest", "Galaxy", "Living Room", "Living Room 2", "Lodge", "Moon", "Sunset", "Warehouse" }, bundled.Select(t => t.Label));
        Assert.Equal(8, BackdropPins.Count);
        Assert.Equal("Milieu.Resources.Thumbs.moon.jpg", bundled.Single(t => t.Label == "Moon").ThumbKey);
        Assert.All(bundled, t => Assert.Equal(TileState.NotDownloaded, t.State));
    }

    [Fact] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-59]
    public void Header_DerivedFromPins()
    {
        Assert.Equal(357_914_496L, BackdropPins.TotalBytes);
        var mb = (BackdropPins.TotalBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        Assert.Equal($"Scenes ({BackdropPins.Count} bundled · {mb} MB to download)", SceneGridViewModel.Header);
        Assert.Equal("Scenes (8 bundled · 341.3 MB to download)", SceneGridViewModel.Header);
    }

    [Fact] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-48]
    public void DrawingGrid_PostsNothing()
    {
        _ = vm.Tiles;
        vm.OnDraw();
        Assert.Empty(posted);
        Assert.Empty(downloads.Requests);
    }

    [Fact] // [AC-hdri-backdrop-14]
    public void TileOrder_NoneThenBundledThenUserImagesInAddedOrder()
    {
        config.UserImages = [@"C:\b.jpg", @"C:\a.png"];
        var kinds = vm.Tiles.Select(t => t.Kind).ToList();
        Assert.Equal(TileKind.None, kinds[0]);
        Assert.All(kinds.Skip(1).Take(8), k => Assert.Equal(TileKind.Bundled, k));
        Assert.Equal(new[] { @"C:\b.jpg", @"C:\a.png" }, vm.Tiles.Skip(9).Select(t => t.Id));
        Assert.True(vm.Tiles.Skip(9).All(t => t.CanRemove));
    }

    [Fact] // [AC-hdri-backdrop-1] [AC-hdri-backdrop-14]
    public void ClickUserTile_PostsSelectSaveShow()
    {
        config.UserImages = [@"C:\pano.jpg"];
        existing.Add(@"C:\pano.jpg");
        vm.Click(T(@"C:\pano.jpg"));
        Assert.Null(config.ImagePath);          // nothing happens until the posted action runs
        Assert.Empty(controller.Calls);
        RunPosted();
        Assert.Equal(@"C:\pano.jpg", config.ImagePath);
        Assert.Equal(1, saves);
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [AC-hdri-backdrop-1] [AC-hdri-backdrop-14]
    public void ClickDownloadedBundledTile_PostsSelectSaveShow()
    {
        downloads.Downloaded.Add("lodge");
        vm.Click(T("bundled:lodge"));
        Assert.Empty(controller.Calls);
        RunPosted();
        Assert.Equal("bundled:lodge", config.ImagePath);
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [AC-hdri-backdrop-14]
    public void SelectedTileHighlighted_AndStateOverlays()
    {
        downloads.Downloaded.Add("moon");
        config.ImagePath = "bundled:moon";
        controller.State = BackdropState.Shown;
        Assert.True(T("bundled:moon").Selected);
        Assert.Equal(TileState.Shown, T("bundled:moon").State);
        controller.State = BackdropState.Preparing;
        Assert.Equal("Loading…", T("bundled:moon").Overlay);
        downloads.Status["forest"] = new EntryStatus(EntryState.Downloading, 44739312 / 4, "");
        Assert.Equal("25%", T("bundled:forest").Overlay);
        downloads.Status["galaxy"] = new EntryStatus(EntryState.Failed, 0, "x");
        Assert.Equal("Failed – click to retry", T("bundled:galaxy").Overlay);
        controller.State = BackdropState.Hidden;
        Assert.Equal("Click to show", T("bundled:moon").Overlay);
    }

    [Fact] // [AC-hdri-backdrop-14]
    public void AddMore_PostsAppendSelectShow()
    {
        vm.AddMore(@"C:\new.hdr");
        Assert.Empty(config.UserImages);
        RunPosted();
        Assert.Equal(new[] { @"C:\new.hdr" }, config.UserImages);
        Assert.Equal(@"C:\new.hdr", config.ImagePath);
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [AC-exr-support-1]
    public void AddMore_Exr_AddsTileAndSelects()
    {
        vm.AddMore(@"C:\new.exr");
        RunPosted();
        Assert.Equal(new[] { @"C:\new.exr" }, config.UserImages);
        Assert.Equal(@"C:\new.exr", config.ImagePath);
        Assert.Contains(vm.Tiles, t => t.Id == @"C:\new.exr");
    }

    [Fact] // [EDGE-hdri-backdrop-72]
    public void AddMore_DuplicatePathSelectsExisting()
    {
        config.UserImages = [@"C:\Pics\Pano.jpg"];
        vm.AddMore(@"c:\pics\pano.jpg");
        RunPosted();
        Assert.Single(config.UserImages);
        Assert.Equal(@"C:\Pics\Pano.jpg", config.ImagePath);
    }

    [Fact] // [AC-hdri-backdrop-14]
    public void Remove_PostsListRemovalOnly_NeverDeletesFile()
    {
        using var t = new TempDir();
        var file = t.File("keep.jpg");
        System.IO.File.WriteAllBytes(file, [1]);
        config.UserImages = [file, @"C:\other.jpg"];
        config.ImagePath = @"C:\other.jpg";
        vm.Remove(T(file));
        RunPosted();
        Assert.Equal(new[] { @"C:\other.jpg" }, config.UserImages);
        Assert.Equal(@"C:\other.jpg", config.ImagePath);
        Assert.Empty(controller.Calls);
        Assert.True(System.IO.File.Exists(file));
    }

    [Fact] // [EDGE-hdri-backdrop-74]
    public void RemoveSelected_PostsNoneAndHide()
    {
        config.UserImages = [@"C:\sel.jpg"];
        config.ImagePath = @"C:\sel.jpg";
        vm.Remove(T(@"C:\sel.jpg"));
        RunPosted();
        Assert.Null(config.ImagePath);
        Assert.Equal(new[] { "Hide" }, controller.Calls);
    }

    [Fact] // [AC-hdri-backdrop-14] [EDGE-hdri-backdrop-78]
    public void SettingsCollapsedViaToggle_ExpandedViaOpenSettings()
    {
        Assert.False(vm.ConsumeSettingsExpand());   // /milieu: collapsed (the panel's own default)
        vm.RequestSettingsExpanded();               // Dalamud's config button
        Assert.True(vm.ConsumeSettingsExpand());
    }

    [Fact] // [EDGE-hdri-backdrop-93]
    public void OpenSettingsExpandIsOneShot()
    {
        vm.RequestSettingsExpanded();
        Assert.True(vm.ConsumeSettingsExpand());
        Assert.False(vm.ConsumeSettingsExpand());
    }

    [Fact] // [AC-hdri-backdrop-14] [EDGE-hdri-backdrop-79]
    public void NoShowApplyHideActionsExposed()
    {
        var names = typeof(SceneGridViewModel).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToList();
        Assert.DoesNotContain("Show", names);
        Assert.DoesNotContain("Hide", names);
        Assert.DoesNotContain("Apply", names);
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoPaths.Root(), "Milieu", "Gui", "MainWindow.cs"));
        foreach (var label in new[] { "ImGui.Button(\"Show", "ImGui.Button(\"Hide", "ImGui.Button(\"Apply", "ImGui.Button(\"Browse", "vm.ShowButtonLabel" })
            Assert.DoesNotContain(label, source);
    }

    [Fact] // [AC-hdri-backdrop-7] [EDGE-hdri-backdrop-70]
    public void NoneTile_PostsHideAndPersistsNull()
    {
        config.ImagePath = "bundled:moon";
        vm.Click(T(SceneGridViewModel.NoneId));
        RunPosted();
        Assert.Null(config.ImagePath);
        Assert.Equal(new[] { "Hide" }, controller.Calls);
        Assert.True(T(SceneGridViewModel.NoneId).Selected);
        vm.SelectNone();
        RunPosted();
        Assert.Equal(new[] { "Hide", "Hide" }, controller.Calls);
    }

    [Fact] // [AC-hdri-backdrop-9]
    public void ClickWhileBlocked_PostedActionSelectsAndSurfacesBlockerOnly()
    {
        // The controller's Show() is itself a no-op while blocked (BackdropControllerTests); the grid only selects and asks.
        downloads.Downloaded.Add("lodge");
        vm.Click(T("bundled:lodge"));
        RunPosted();
        Assert.Equal("bundled:lodge", config.ImagePath);
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [EDGE-hdri-backdrop-71]
    public void ClickSelected_PostsShowWhenHidden_NothingWhenShownOrPreparing()
    {
        downloads.Downloaded.Add("lodge");
        config.ImagePath = "bundled:lodge";
        foreach (var s in new[] { BackdropState.Shown, BackdropState.Preparing, BackdropState.AwaitingRedirect })
        {
            controller.State = s;
            vm.Click(T("bundled:lodge"));
            Assert.Empty(posted);
        }
        controller.State = BackdropState.Hidden;
        vm.Click(T("bundled:lodge"));
        RunPosted();
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [EDGE-hdri-backdrop-73]
    public void MissingUserFile_TileMissing_ClickPostsFileMissingNoShow()
    {
        config.UserImages = [@"C:\gone.jpg"];
        Assert.Equal(TileState.Missing, T(@"C:\gone.jpg").State);
        vm.Click(T(@"C:\gone.jpg"));
        RunPosted();
        Assert.Equal(new[] { "Report" }, controller.Calls);
        Assert.Equal(MessageKey.FileMissing, controller.Reported!.Key);
        Assert.Equal("gone.jpg no longer exists.", controller.Reported.Text);
    }

    [Fact] // [EDGE-hdri-backdrop-61]
    public void ClickNotDownloadedBundled_PostsRequest_AutoShowPostedAfterCompletion()
    {
        vm.Click(T("bundled:forest"));
        RunPosted();
        Assert.Equal(new[] { "forest" }, downloads.Requests);
        Assert.Empty(controller.Calls);
        vm.OnDraw();
        Assert.Empty(posted);                      // not downloaded yet
        downloads.Downloaded.Add("forest");
        vm.OnDraw();
        RunPosted();
        Assert.Equal(new[] { "Show" }, controller.Calls);
        vm.OnDraw();
        Assert.Empty(posted);                      // once only
    }

    [Fact] // [EDGE-hdri-backdrop-66]
    public void SecondBundledClick_QueuesBoth_OnlyLastSelectedAutoShows()
    {
        vm.Click(T("bundled:forest"));
        RunPosted();
        vm.Click(T("bundled:galaxy"));
        RunPosted();
        Assert.Equal(new[] { "forest", "galaxy" }, downloads.Requests);
        downloads.Downloaded.Add("forest");
        vm.OnDraw();
        Assert.Empty(posted);
        downloads.Downloaded.Add("galaxy");
        vm.OnDraw();
        RunPosted();
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [EDGE-hdri-backdrop-75]
    public void DownloadCompletesAfterReselect_NoAutoShowPosted()
    {
        vm.Click(T("bundled:forest"));
        RunPosted();
        config.UserImages = [@"C:\mine.jpg"];
        existing.Add(@"C:\mine.jpg");
        vm.Click(T(@"C:\mine.jpg"));
        RunPosted();
        controller.Calls.Clear();
        downloads.Downloaded.Add("forest");
        vm.OnDraw();
        Assert.Empty(posted);
    }

    [Fact] // [EDGE-hdri-backdrop-49]
    public void SelectedBundledDeleted_ShowsNotDownloaded_ClickPostsRequestThenShow()
    {
        config.ImagePath = "bundled:sunset";
        Assert.Equal(TileState.NotDownloaded, T("bundled:sunset").State);
        vm.Click(T("bundled:sunset"));
        RunPosted();
        Assert.Equal(new[] { "sunset" }, downloads.Requests);
        downloads.Downloaded.Add("sunset");
        vm.OnDraw();
        RunPosted();
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }

    [Fact] // [EDGE-hdri-backdrop-76]
    public void UserTileWithoutThumb_UsesPlaceholderLabel()
    {
        config.UserImages = [@"C:\pics\My Room.hdr"];
        var t = T(@"C:\pics\My Room.hdr");
        Assert.Equal("My Room", t.Label);
        Assert.Equal(@"C:\pics\My Room.hdr", t.ThumbKey);   // ThumbnailCache falls back to the label when no thumbnail exists
    }

    [Fact] // [EDGE-hdri-backdrop-44] [EDGE-hdri-backdrop-59]
    public void CreditsLink_HiddenWhileUnpublished()
    {
        Assert.True(vm.ShowCreditsLink);
        downloads.Unpublished = true;
        Assert.False(vm.ShowCreditsLink);
        Assert.Equal("https://github.com/solona-m/milieu/blob/backdrops-v2/Milieu/DefaultBackdrops/CREDITS.md", BackdropPins.CreditsUrl);
    }

    [Fact] // [EDGE-hdri-backdrop-92]
    public void AllActionsGoThroughPost()
    {
        config.UserImages = [@"C:\u.jpg"];
        existing.Add(@"C:\u.jpg");
        downloads.Downloaded.Add("lodge");
        var before = (config.ImagePath, config.UserImages.Count, saves);
        vm.Click(T(SceneGridViewModel.NoneId));
        vm.Click(T("bundled:lodge"));
        vm.Click(T("bundled:forest"));
        vm.Click(T(@"C:\u.jpg"));
        vm.AddMore(@"C:\added.jpg");
        vm.Remove(T(@"C:\u.jpg"));
        vm.DownloadAll();
        vm.TryAgain("galaxy");
        Assert.Equal(before, (config.ImagePath, config.UserImages.Count, saves));
        Assert.Empty(controller.Calls);
        Assert.Empty(downloads.Requests);
        Assert.Equal(8, posted.Count);
    }

    [Fact] // [EDGE-hdri-backdrop-92]
    public void PendingAutoShowIsPostedFromDraw()
    {
        vm.Click(T("bundled:moon"));
        RunPosted();
        downloads.Downloaded.Add("moon");   // the download finished on its worker thread: nothing happens there
        Assert.Empty(controller.Calls);
        vm.OnDraw();
        Assert.Single(posted);
        Assert.Empty(controller.Calls);
        RunPosted();
        Assert.Equal(new[] { "Show" }, controller.Calls);
    }
}
