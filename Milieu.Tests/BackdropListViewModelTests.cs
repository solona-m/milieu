using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Milieu.Assets;
using Milieu.Gui;
using Xunit;

namespace Milieu.Tests;

public class BackdropListViewModelTests
{
    private sealed class Fake
    {
        public PackStatus Status = new(PackState.Idle, 0, DefaultBackdropsDownloadService.Backdrops.Length, "", false);
        public readonly HashSet<string> Downloaded = [];
        public readonly List<bool> Ensures = [];
        public BackdropListViewModel Vm() => new(() => Status, Downloaded.Contains, n => @"C:\cfg\DefaultBackdrops\" + n, Ensures.Add);
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void ListsFiveBundledBackdrops_WithDisplayNames()
    {
        // The pack holds every image the user supplied (seven), each with a display name.
        var names = new Fake().Vm().Entries(null).Select(e => e.DisplayName).ToArray();
        Assert.Equal(new[] { "Forest", "Galaxy", "Living Room", "Living Room 2", "Lodge", "Sunset", "Warehouse" }, names);
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void FirstDraw_StartsDownloadOnce()
    {
        var f = new Fake();
        var vm = f.Vm();
        vm.OnDraw(); vm.OnDraw(); vm.OnDraw();
        Assert.Equal(new[] { false }, f.Ensures);
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void OnlyDownloadedEntriesSelectable_TryAgainOnFailure()
    {
        var f = new Fake();
        f.Downloaded.Add("lodge.jpeg");
        var vm = f.Vm();
        var entries = vm.Entries(null);
        var cfg = new Configuration();
        Assert.False(vm.Select(entries.Single(e => e.Name == "forest.jpeg"), cfg));
        Assert.Null(cfg.ImagePath);
        Assert.True(vm.Select(entries.Single(e => e.Name == "lodge.jpeg"), cfg));
        Assert.Equal(@"C:\cfg\DefaultBackdrops\lodge.jpeg", cfg.ImagePath);
        Assert.False(vm.ShowTryAgain);
        f.Status = f.Status with { State = PackState.Failed };
        Assert.True(vm.ShowTryAgain);
        vm.TryAgain();
        Assert.Equal(new[] { true }, f.Ensures);
        f.Status = f.Status with { State = PackState.Downloading, Have = 3 };
        Assert.Equal("Downloading backdrops… 3/7", vm.StatusLine);
    }

    [Fact] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-59]
    public void CreditsLink_PointsAtTaggedCreditsFile_HiddenWhileUnpublished()
    {
        Assert.Equal("https://github.com/solona-m/milieu/blob/backdrops-v1/Milieu/DefaultBackdrops/CREDITS.md", BackdropListViewModel.CreditsUrl);
        var f = new Fake();
        Assert.True(f.Vm().ShowCreditsLink);
        f.Status = f.Status with { State = PackState.Failed, Unpublished = true };
        Assert.False(f.Vm().ShowCreditsLink);
    }

    [Fact] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-59]
    public void Header_ShowsPinnedTotalSize()
    {
        var mb = DefaultBackdropsDownloadService.Backdrops.Sum(b => b.Bytes) / 1048576.0;
        Assert.Equal($"Backdrops (7 · {mb.ToString("0.0", CultureInfo.InvariantCulture)} MB)", BackdropListViewModel.Header);
    }

    [Fact] // [AC-hdri-backdrop-10]
    public void BundledSelection_PersistsAsImagePath()
    {
        var f = new Fake();
        f.Downloaded.Add("galaxy.jpeg");
        var cfg = new Configuration();
        var vm = f.Vm();
        vm.Select(vm.Entries(null).Single(e => e.Name == "galaxy.jpeg"), cfg);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(cfg);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;
        Assert.Equal(@"C:\cfg\DefaultBackdrops\galaxy.jpeg", back.ImagePath);
        Assert.True(vm.Entries(back.ImagePath).Single(e => e.Name == "galaxy.jpeg").Selected);
    }

    [Fact] // [EDGE-hdri-backdrop-48]
    public void NoDraw_NoEnsure()
    {
        var f = new Fake();
        _ = f.Vm().Entries(null);
        Assert.Empty(f.Ensures);
    }

    [Fact] // [EDGE-hdri-backdrop-49]
    public void SelectedButDeleted_ShowsNotDownloaded()
    {
        var f = new Fake();
        var e = f.Vm().Entries(@"C:\cfg\DefaultBackdrops\sunset.jpeg").Single(x => x.Name == "sunset.jpeg");
        Assert.True(e.Selected);
        Assert.False(e.Downloaded);
        Assert.Equal("Not downloaded", e.StateText);
    }
}
