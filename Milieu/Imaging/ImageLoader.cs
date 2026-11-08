using System;
using System.IO;
using System.Threading;
using Milieu.Backdrop;
using StbImageSharp;

namespace Milieu.Imaging;

public enum ImageKind { Hdr, Png, Jpeg }

/// <summary>What the header says, read without decoding the pixels.</summary>
public sealed record ProbeInfo(ImageKind Kind, int Width, int Height, int BitsPerChannel, int Components, bool Progressive, long FileBytes)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>A decoded image: float RGB (HDR) or 8-bit RGB (PNG/JPG), row-major, top row first.</summary>
public sealed class LoadedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public float[]? Hdr { get; init; }
    public byte[]? Rgb { get; init; }
    public bool IsHdr => Hdr != null;
}

/// <summary>Probes and decodes panoramas with StbImageSharp, enforcing the size and memory limits up front.</summary>
public static class ImageLoader
{
    public const int MaxHdrWidth = 8192;
    public const int MaxHdrHeight = 4096;
    public const long MaxLdrPixels = 134_217_728;          // e.g. 16384x8192 or 21184x3848
    public const long LargeImagePixels = 33_554_432;       // 8192x4096: above this, the LargeImage warning

    public static ImageKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hdr" => ImageKind.Hdr,
        ".png" => ImageKind.Png,
        ".jpg" or ".jpeg" => ImageKind.Jpeg,
        _ => null,
    };

    /// <summary>Reads the header only. Throws <see cref="MilieuException"/> for unsupported, missing or unreadable files.</summary>
    public static ProbeInfo Probe(string path)
    {
        var file = Path.GetFileName(path);
        var kind = KindOf(path) ?? throw new MilieuException(StatusMessage.Of(MessageKey.UnsupportedFormat, ("file", file)));
        if (!File.Exists(path)) throw new MilieuException(StatusMessage.Of(MessageKey.FileMissing, ("file", file)));
        try
        {
            using var s = File.OpenRead(path);
            var length = s.Length;
            if (kind == ImageKind.Hdr)
            {
                var (hw, hh) = RadianceHeader.Read(s) ?? throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
                return new ProbeInfo(kind, hw, hh, 32, 3, false, length);
            }
            var header = (kind == ImageKind.Png ? HeaderProbe.Png(s) : HeaderProbe.Jpeg(s))
                         ?? throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
            return new ProbeInfo(kind, header.Width, header.Height, header.BitsPerChannel, header.Components, header.Progressive, length);
        }
        catch (MilieuException) { throw; }
        catch (Exception)
        {
            throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
        }
    }

    /// <summary>
    /// Applies the size and memory limits to a probe. Returns the warnings to show; throws when the image must be refused.
    /// </summary>
    public static StatusMessage[] CheckLimits(ProbeInfo p, string fileName, int outWidth)
    {
        if (p.Width <= 0 || p.Height <= 0)
            throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", fileName)));
        if (p.Kind == ImageKind.Hdr)
        {
            if (p.Width > MaxHdrWidth || p.Height > MaxHdrHeight)
                throw new MilieuException(StatusMessage.Of(MessageKey.TooLargeHdr, ("file", fileName), ("w", p.Width), ("h", p.Height)));
            return [];
        }
        var peak = DecodeBudget.PredictPeakBytes(p, outWidth);
        if (p.Pixels > MaxLdrPixels || peak > DecodeBudget.BudgetBytes)
            throw new MilieuException(StatusMessage.Of(MessageKey.TooLargeLdr, ("file", fileName), ("w", p.Width), ("h", p.Height),
                                                      ("gb", (peak / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));
        return p.Pixels > LargeImagePixels
            ? [StatusMessage.Of(MessageKey.LargeImage, ("file", fileName), ("w", p.Width), ("h", p.Height))]
            : [];
    }

    /// <summary>Decodes the pixels. PNG/JPG always come back as 8-bit RGB (alpha dropped, grey expanded, 16-bit reduced).</summary>
    public static LoadedImage Load(string path, ImageKind kind, CancellationToken ct)
    {
        var file = Path.GetFileName(path);
        if (!File.Exists(path)) throw new MilieuException(StatusMessage.Of(MessageKey.FileMissing, ("file", file)));
        ct.ThrowIfCancellationRequested();
        try
        {
            using var s = File.OpenRead(path);
            if (kind == ImageKind.Hdr)
            {
                var img = ImageResultFloat.FromStream(s, ColorComponents.RedGreenBlue);
                return new LoadedImage { Width = img.Width, Height = img.Height, Hdr = img.Data };
            }
            var ldr = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
            return new LoadedImage { Width = ldr.Width, Height = ldr.Height, Rgb = ldr.Data };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
        }
    }
}
