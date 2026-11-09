using System;

namespace Milieu.Imaging.Exr;

// The decoder in this folder is derived from the OpenEXR reference implementation (BSD-3-Clause,
// Copyright Contributors to the OpenEXR Project); see THIRD_PARTY_NOTICES.txt and docs/adr/0008.

/// <summary>The file is corrupt, truncated or inconsistent. Mapped to DecodeFailed.</summary>
public sealed class ExrFormatException(string message) : Exception(message);

public enum ExrUnsupportedKind { Compression, Layout }

/// <summary>
/// A valid file using a variant Milieu doesn't read. <see cref="Detail"/> is the user-facing compression name
/// (e.g. "DWAA") or layout feature (e.g. "a multi-part layout").
/// </summary>
public sealed class ExrUnsupportedException(ExrUnsupportedKind kind, string detail) : Exception(detail)
{
    public ExrUnsupportedKind Kind { get; } = kind;
    public string Detail { get; } = detail;
}

/// <summary>Hard caps applied to every size read from an untrusted file (ADR 0008).</summary>
public static class ExrLimits
{
    public const long MaxBlockBytes = 64L * 1024 * 1024;
    public const int MaxTiles = 1_048_576;
    public const int MaxChannels = 1024;
    public const int MaxNameBytes = 255;
    public const long MaxHeaderBytes = 16L * 1024 * 1024;
    public const int MaxChlistBytes = 64 * 1024;

    /// <summary>Worst-case decode working set besides the float output: packed + unpacked + PIZ scratch, plus the fixed PIZ tables.</summary>
    public const long WorkingSetBytes = 3 * MaxBlockBytes + 1024 * 1024;
}

/// <summary>Per-decode diagnostics for tests (passed explicitly so parallel tests can't interfere).</summary>
internal sealed class ExrDecodeTrace
{
    /// <summary>True when any PIZ chunk used the 16-bit wavelet (maxValue ≥ 2^14).</summary>
    public bool UsedWdec16 { get; set; }

    public int Chunks { get; set; }
}
