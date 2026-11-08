using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Milieu.Assets;
using Milieu.Backdrop;

namespace Milieu.Gui;

public enum TileKind { None, Bundled, User }

public enum TileState { Idle, Hidden, Shown, Loading, Converting, NotDownloaded, Queued, Downloading, Unpublished, Failed, Missing }

/// <summary>One preview tile. <see cref="ThumbKey"/>: an embedded resource name (bundled) or the image path (user).</summary>
public sealed record Tile(string Id, TileKind Kind, string Label, string? ThumbKey, bool Selected, TileState State, float Progress, bool CanRemove)
{
    public string Overlay => State switch
    {
        TileState.NotDownloaded => "Not downloaded",
        TileState.Queued => "Queued",
        TileState.Downloading => $"{(int)(Progress * 100)}%",
        TileState.Loading => "Loading…",
        TileState.Converting => $"Converting {(int)(Progress * 100)}%",
        TileState.Unpublished => "Not published yet",
        TileState.Failed => "Failed – click to retry",
        TileState.Missing => "Missing",
        TileState.Hidden => "Click to show",
        _ => "",
    };
}

/// <summary>What the grid needs from the backdrop controller.</summary>
public interface IGridController
{
    BackdropState State { get; }
    float Progress { get; }
    void Show();
    void Hide();
    void ReportStatus(StatusMessage message);
}

/// <summary>What the grid needs from the bundled downloads.</summary>
public interface IGridDownloads
{
    EntryStatus StatusOf(string stem);
    bool IsDownloaded(string stem);
    void Request(string stem);
    void RequestAll();
    bool AnyMissing { get; }
    bool PackUnpublished { get; }
}

/// <summary>
/// The scene grid (pure, unit-tested): None, the bundled scenes, then the user's images. Clicking a tile applies it
/// at once. Every action is posted (to the framework tick) and nothing calls the controller directly, because ImGui
/// draws off the framework thread and downloads finish on a worker thread.
/// </summary>
public sealed class SceneGridViewModel(Configuration config, Action save, Action<Action> post, IGridController controller,
                                       IGridDownloads downloads, Func<string, bool> fileExists)
{
    public const string NoneId = "none";
    public const string ThumbResourcePrefix = "Milieu.Resources.Thumbs.";

    private string? pendingShow;
    private bool expandSettings;

    public static string ThumbResource(string stem) => ThumbResourcePrefix + stem + ".jpg";

    public static string Header =>
        $"Scenes ({BackdropPins.Count} bundled · " +
        (BackdropPins.TotalBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB to download)";

    public bool ShowDownloadAll => downloads.AnyMissing;

    public bool ShowCreditsLink => !downloads.PackUnpublished;

    public string? PendingShow => pendingShow;

    public IReadOnlyList<Tile> Tiles
    {
        get
        {
            var list = new List<Tile> { new(NoneId, TileKind.None, "None", null, config.ImagePath == null, TileState.Idle, 0, false) };
            foreach (var pin in BackdropPins.All)
            {
                var id = BackdropPins.IdOf(pin.Stem);
                var selected = config.ImagePath == id;
                var s = downloads.StatusOf(pin.Stem);
                var (state, progress) = s.State switch
                {
                    EntryState.Queued => (TileState.Queued, 0f),
                    EntryState.Downloading => (TileState.Downloading, s.Fraction(pin.Bytes)),
                    EntryState.Unpublished => (TileState.Unpublished, 0f),
                    EntryState.Failed => (TileState.Failed, 0f),
                    EntryState.NotDownloaded => (TileState.NotDownloaded, 0f),
                    _ => SelectedState(selected, bundled: true),
                };
                list.Add(new Tile(id, TileKind.Bundled, pin.DisplayName, ThumbResource(pin.Stem), selected, state, progress, false));
            }
            foreach (var path in config.UserImages)
            {
                var selected = string.Equals(config.ImagePath, path, StringComparison.OrdinalIgnoreCase);
                var (state, progress) = fileExists(path) ? SelectedState(selected, bundled: false) : (TileState.Missing, 0f);
                list.Add(new Tile(path, TileKind.User, Path.GetFileNameWithoutExtension(path), path, selected, state, progress, true));
            }
            return list;
        }
    }

    private (TileState, float) SelectedState(bool selected, bool bundled)
    {
        if (!selected) return (TileState.Idle, 0f);
        return controller.State switch
        {
            BackdropState.Preparing => (bundled ? TileState.Loading : TileState.Converting, controller.Progress),
            BackdropState.Shown or BackdropState.Suspended or BackdropState.AwaitingRedirect => (TileState.Shown, 0f),
            _ => (TileState.Hidden, 0f),
        };
    }

    /// <summary>A tile was clicked: select it, persist, and show (downloading first if needed). Posted.</summary>
    public void Click(Tile tile)
    {
        switch (tile.Kind)
        {
            case TileKind.None:
                pendingShow = null;
                post(() => { config.ImagePath = null; save(); controller.Hide(); });
                return;
            case TileKind.Bundled:
            {
                var stem = tile.Id[BackdropPins.IdPrefix.Length..];
                if (!downloads.IsDownloaded(stem))
                {
                    pendingShow = stem;
                    post(() => { config.ImagePath = tile.Id; save(); downloads.Request(stem); });
                    return;
                }
                pendingShow = null;
                if (tile.Selected && !NeedsShow()) return;
                post(() => { config.ImagePath = tile.Id; save(); controller.Show(); });
                return;
            }
            default:
            {
                pendingShow = null;
                var path = tile.Id;
                if (!fileExists(path))
                {
                    post(() => { config.ImagePath = path; save(); controller.ReportStatus(StatusMessage.Of(MessageKey.FileMissing, ("file", Path.GetFileName(path)))); });
                    return;
                }
                if (tile.Selected && !NeedsShow()) return;
                post(() => { config.ImagePath = path; save(); controller.Show(); });
                return;
            }
        }
    }

    /// <summary>Re-clicking the selected tile re-shows it only when nothing is shown or on its way.</summary>
    private bool NeedsShow() => controller.State == BackdropState.Hidden;

    /// <summary>"Add more…": the chosen image becomes a tile (or the existing one), is selected and shown. Posted.</summary>
    public void AddMore(string path)
    {
        pendingShow = null;
        post(() =>
        {
            var existing = config.UserImages.FirstOrDefault(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (existing == null) config.UserImages.Add(path);
            config.ImagePath = existing ?? path;
            save();
            controller.Show();
        });
    }

    /// <summary>"×": removes one of the user's images from the grid. The file itself is never touched. Posted.</summary>
    public void Remove(Tile tile)
    {
        if (tile.Kind != TileKind.User) return;
        post(() =>
        {
            config.UserImages.RemoveAll(p => string.Equals(p, tile.Id, StringComparison.OrdinalIgnoreCase));
            var wasSelected = string.Equals(config.ImagePath, tile.Id, StringComparison.OrdinalIgnoreCase);
            if (wasSelected) config.ImagePath = null;
            save();
            if (wasSelected) controller.Hide();
        });
    }

    /// <summary>The same action as clicking None (/milieu off).</summary>
    public void SelectNone() => Click(Tiles[0]);

    public void DownloadAll() => post(downloads.RequestAll);

    public void TryAgain(string stem) => post(() => downloads.Request(stem));

    /// <summary>Called on each draw: a clicked scene that finished downloading is shown, if it is still selected.</summary>
    public void OnDraw()
    {
        if (pendingShow == null) return;
        if (config.ImagePath != BackdropPins.IdOf(pendingShow)) { pendingShow = null; return; }
        if (!downloads.IsDownloaded(pendingShow)) return;
        pendingShow = null;
        post(controller.Show);
    }

    /// <summary>Dalamud's config button: the Settings panel opens expanded once.</summary>
    public void RequestSettingsExpanded() => expandSettings = true;

    /// <summary>One-shot: true the first time it is read after <see cref="RequestSettingsExpanded"/>.</summary>
    public bool ConsumeSettingsExpand()
    {
        var e = expandSettings;
        expandSettings = false;
        return e;
    }
}
