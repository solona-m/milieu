using System;
using System.IO;
using System.Threading;
using Milieu.Backdrop;
using Milieu.Imaging.Exr;
using StbImageSharp;

namespace Milieu.Imaging;

public enum ImageKind { Hdr, Png, Jpeg, Exr }

public static class ImageKindExtensions
{
    /// <summary>True for the float (HDR) formats: Radiance .hdr and OpenEXR.</summary>
    public static bool IsHdr(this ImageKind k) => k is ImageKind.Hdr or ImageKind.Exr;
}

/// <summary>What the header says, read without decoding the pixels.</summary>
public sealed record ProbeInfo(ImageKind Kind, int Width, int Height, int BitsPerChannel, int Components, bool Progressive, long FileBytes)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>A decoded image: float RGB (HDR/EXR) or 8-bit RGB (PNG/JPG), row-major, top row first.</summary>
public sealed class LoadedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public float[]? Hdr { get; init; }
    public byte[]? Rgb { get; init; }
    public bool IsHdr => Hdr != null;
}

/// <summary>Probes and decodes panoramas (StbImageSharp; OpenEXR via <see cref="ExrDecoder"/>), enforcing the size and memory limits up front.</summary>
public static class ImageLoader
{
    public const int MaxHdrWidth = 8192;
    public const int MaxHdrHeight = 4096;
    public const long MaxLdrPixels = 134_217_728;          // e.g. 16384x8192 or 21184x3848
    public const long LargeImagePixels = 33_554_432;       // 8192x4096: above this, the LargeImage warning

    /// <summary>Every extension Milieu reads, lower case.</summary>
    public static readonly string[] SupportedExtensions = [".hdr", ".exr", ".png", ".jpg", ".jpeg"];

    /// <summary>The "Add more…" file dialog filter, built from <see cref="SupportedExtensions"/>.</summary>
    public static readonly string FileDialogFilter = "Panoramas{" + string.Join(",", SupportedExtensions) + "}";

    public static ImageKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hdr" => ImageKind.Hdr,
        ".exr" => ImageKind.Exr,
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
            if (kind == ImageKind.Exr)
            {
                var exr = ExrHeader.Read(s);
                return new ProbeInfo(kind, exr.Width, exr.Height, exr.BitsPerChannel, 3, false, length);
            }
            var header = (kind == ImageKind.Png ? HeaderProbe.Png(s) : HeaderProbe.Jpeg(s))
                         ?? throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
            return new ProbeInfo(kind, header.Width, header.Height, header.BitsPerChannel, header.Components, header.Progressive, length);
        }
        catch (MilieuException) { throw; }
        catch (ExrUnsupportedException e) { throw Unsupported(e, file); }
        catch (Exception)
        {
            throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
        }
    }

    private static MilieuException Unsupported(ExrUnsupportedException e, string file) => new(e.Kind == ExrUnsupportedKind.Compression
        ? StatusMessage.Of(MessageKey.UnsupportedExrCompression, ("file", file), ("compression", e.Detail))
        : StatusMessage.Of(MessageKey.UnsupportedExrLayout, ("file", file), ("feature", e.Detail)));

    /// <summary>
    /// Applies the size and memory limits to a probe. Returns the warnings to show; throws when the image must be refused.
    /// </summary>
    public static StatusMessage[] CheckLimits(ProbeInfo p, string fileName, int outWidth)
    {
        if (p.Width <= 0 || p.Height <= 0)
            throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", fileName)));
        if (p.Kind.IsHdr())
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
            if (kind == ImageKind.Exr)
            {
                // Defence in depth: the file may have changed since the probe. Refuse before the offset table or output.
                var header = ExrHeader.Read(s);
                if (header.Width > MaxHdrWidth || header.Height > MaxHdrHeight)
                    throw new MilieuException(StatusMessage.Of(MessageKey.TooLargeHdr, ("file", file), ("w", header.Width), ("h", header.Height)));
                s.Position = 0;
                var (w, h, rgb) = ExrDecoder.Decode(s, ct);
                return new LoadedImage { Width = w, Height = h, Hdr = rgb };
            }
            var ldr = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
            return new LoadedImage { Width = ldr.Width, Height = ldr.Height, Rgb = ldr.Data };
        }
        catch (OperationCanceledException) { throw; }
        catch (MilieuException) { throw; }
        catch (ExrUnsupportedException e) { throw Unsupported(e, file); }
        catch (Exception)
        {
            throw new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", file)));
        }
    }
}
