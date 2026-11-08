using System;
using System.Collections.Generic;
using System.Globalization;

namespace Milieu.Backdrop;

/// <summary>Every user-facing status message. Texts name only an image's file name, never its full path.</summary>
public enum MessageKey
{
    None,
    InteropUnavailable,
    PenumbraMissing,
    PenumbraIncompatible,
    PenumbraModsDisabled,
    PenumbraNoBaseCollection,
    NotLoggedIn,
    UnsupportedFormat,
    TooLargeHdr,
    TooLargeLdr,
    LargeImage,
    NoWrap,
    NotTwoToOne,
    DecodeFailed,
    FileMissing,
    WriteFailed,
    RedirectTimeout,
    PenumbraGone,
    GameEndedVfx,
    Converting,
    NoImage,
    BundledNotDownloaded,
    BundledCorrupt,
    LoadingBundled,
    BundledNote,
}

public enum Severity { Info, Warning, Error }

/// <summary>A message key plus its named arguments ({file}, {w}, {h}, {gb}, {pct}).</summary>
public sealed record StatusMessage(MessageKey Key, IReadOnlyDictionary<string, string> Args)
{
    public static readonly StatusMessage None = new(MessageKey.None, new Dictionary<string, string>());

    public static StatusMessage Of(MessageKey key, params (string Name, object Value)[] args)
    {
        var d = new Dictionary<string, string>();
        foreach (var (n, v) in args) d[n] = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        return new StatusMessage(key, d);
    }

    public Severity Severity => Messages.SeverityOf(Key);

    public string Text => Messages.Format(this);
}

/// <summary>A conversion or validation failure carrying its message.</summary>
public sealed class MilieuException(StatusMessage message) : Exception(message.Text)
{
    public StatusMessage Status { get; } = message;
}

public static class Messages
{
    private static readonly Dictionary<MessageKey, string> Texts = new()
    {
        [MessageKey.None] = "",
        [MessageKey.InteropUnavailable] = "Milieu can't find the game's VFX functions. It may need an update for this game patch.",
        [MessageKey.PenumbraMissing] = "Penumbra isn't running. Milieu needs it to show backdrops.",
        [MessageKey.PenumbraIncompatible] = "This Penumbra version isn't supported (needs API 5).",
        [MessageKey.PenumbraModsDisabled] = "Mods are disabled in Penumbra. Enable them to show a backdrop.",
        [MessageKey.PenumbraNoBaseCollection] = "Penumbra's Base collection is set to None, so it can't load Milieu's files. Assign a Base collection in Penumbra.",
        [MessageKey.NotLoggedIn] = "Log in to show a backdrop.",
        [MessageKey.UnsupportedFormat] = "{file}: only .hdr, .png and .jpg are supported.",
        [MessageKey.TooLargeHdr] = "{file} is {w}x{h}. HDR images can be at most 8192x4096; downscale it first.",
        [MessageKey.TooLargeLdr] = "{file} ({w}x{h}) is too large to convert safely (about {gb} GB needed); downscale it first.",
        [MessageKey.LargeImage] = "{file} is very large ({w}x{h}); converting it takes longer and uses a lot of memory.",
        [MessageKey.NoWrap] = "{file} doesn't wrap around 360°; expect a visible seam.",
        [MessageKey.NotTwoToOne] = "{file} is taller than 2:1; it will be stretched.",
        [MessageKey.DecodeFailed] = "{file} couldn't be read.",
        [MessageKey.FileMissing] = "{file} no longer exists.",
        [MessageKey.WriteFailed] = "Couldn't write the converted texture to disk.",
        [MessageKey.RedirectTimeout] = "Penumbra didn't pick up the backdrop files. Check that Penumbra's Base collection is set, then try again.",
        [MessageKey.PenumbraGone] = "Penumbra was unloaded; the backdrop was hidden.",
        [MessageKey.GameEndedVfx] = "The game ended the backdrop effect; Milieu stopped re-showing it.",
        [MessageKey.Converting] = "Converting {file}… {pct}%",
        [MessageKey.NoImage] = "Choose a scene first.",
        [MessageKey.BundledNotDownloaded] = "{name} isn't downloaded yet.",
        [MessageKey.BundledCorrupt] = "{name} was damaged on disk; download it again.",
        [MessageKey.LoadingBundled] = "Loading {name}…",
        [MessageKey.BundledNote] = "Bundled scenes come pre-converted at 8192x4096, so the texture size and HDR settings don't apply.",
    };

    public static string Template(MessageKey key) => Texts[key];

    public static Severity SeverityOf(MessageKey key) => key switch
    {
        MessageKey.None or MessageKey.Converting or MessageKey.LoadingBundled or MessageKey.BundledNote => Severity.Info,
        MessageKey.LargeImage or MessageKey.NoWrap or MessageKey.NotTwoToOne => Severity.Warning,
        _ => Severity.Error,
    };

    public static string Format(StatusMessage m)
    {
        var text = Texts[m.Key];
        foreach (var (k, v) in m.Args) text = text.Replace("{" + k + "}", v);
        return text;
    }
}
