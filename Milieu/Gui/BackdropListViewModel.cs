using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Milieu.Assets;

namespace Milieu.Gui;

/// <summary>One row of the Backdrops list.</summary>
public sealed record BackdropEntry(string Name, string DisplayName, bool Downloaded, bool Selected, string StateText);

/// <summary>Pure state for the Backdrops section (unit-tested): header, entries, status line, Try again, Credits.</summary>
public sealed class BackdropListViewModel
{
    private readonly Func<PackStatus> status;
    private readonly Func<string, bool> isDownloaded;
    private readonly Func<string, string> pathOf;
    private readonly Action<bool> ensure;
    private bool ensured;

    public BackdropListViewModel(Func<PackStatus> status, Func<string, bool> isDownloaded, Func<string, string> pathOf, Action<bool> ensure)
    {
        this.status = status;
        this.isDownloaded = isDownloaded;
        this.pathOf = pathOf;
        this.ensure = ensure;
    }

    public static BackdropListViewModel For(DefaultBackdropsDownloadService s) =>
        new(() => s.Status, s.IsDownloaded, s.PathOf, retry => s.EnsureAsync(retry));

    /// <summary>"Backdrops (7 · 50.4 MB)", from the pinned table, shown before anything is downloaded.</summary>
    public static string Header =>
        $"Backdrops ({DefaultBackdropsDownloadService.Backdrops.Length} · " +
        (DefaultBackdropsDownloadService.TotalBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB)";

    /// <summary>Called when the list is drawn: the first draw starts the download, later draws do nothing.</summary>
    public void OnDraw()
    {
        if (ensured) return;
        ensured = true;
        ensure(false);
    }

    public void TryAgain() => ensure(true);

    public IReadOnlyList<BackdropEntry> Entries(string? selectedPath) =>
        DefaultBackdropsDownloadService.Backdrops.Select(b =>
        {
            var downloaded = isDownloaded(b.Name);
            var selected = selectedPath != null && string.Equals(pathOf(b.Name), selectedPath, StringComparison.OrdinalIgnoreCase);
            return new BackdropEntry(b.Name, b.DisplayName, downloaded, selected, downloaded ? "Downloaded" : "Not downloaded");
        }).ToList();

    public bool CanSelect(BackdropEntry e) => e.Downloaded;

    /// <summary>Picking a downloaded backdrop makes it the image to show (persisted as ImagePath).</summary>
    public bool Select(BackdropEntry e, Configuration config)
    {
        if (!e.Downloaded) return false;
        config.ImagePath = pathOf(e.Name);
        return true;
    }

    public string? StatusLine
    {
        get
        {
            var s = status();
            return s.State switch
            {
                PackState.Downloading => $"Downloading backdrops… {s.Have}/{s.Total}",
                PackState.Failed when s.Unpublished => "Backdrops aren't published yet.",
                PackState.Failed => "Couldn't download the backdrops.",
                _ => null,
            };
        }
    }

    public bool ShowTryAgain => status().State == PackState.Failed;

    public bool ShowCreditsLink => !status().Unpublished;

    public static string CreditsUrl => DefaultBackdropsDownloadService.CreditsUrl;
}
