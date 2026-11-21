namespace Milieu.Tests.Support;

/// <summary>A BC7 block written from the spec: its fields, its 16 bytes and its 16 RGBA texels.</summary>
public sealed record GoldenBlock(int Mode, int Partition, int[] Endpoints, int[] PBits, byte[] Indices, byte[] Bytes, byte[] Texels);

/// <summary>
/// Hand-derived BC7 blocks (fast-bc7, decision 14). Each was laid out field by field from the Khronos Data Format
/// Specification's BC7 bit layout (fields packed least significant bit first: mode bits, partition, endpoints in
/// R0.. G0.. B0.. order, p-bits, then indices with each anchor's MSB dropped) and decoded with the spec's endpoint expansion
/// and <c>(e0*(64-w) + e1*w + 32) &gt;&gt; 6</c> interpolation, by a standalone script that shares nothing with
/// <c>Bc7Block</c> (tools/Bc7Golden/golden.py). Three independent readings must agree: these literals, BcDecoder, and Bc7Block.PackFields.
/// Endpoints are in bit order (mode 6/5: R0 R1 G0 G1 B0 B1, 7 bits; mode 1: R0..R3 G0..G3 B0..B3, 6 bits).
/// </summary>
public static class Bc7GoldenBlocks
{
    /// <summary>mode 6, e0 &lt; e1 in every channel, indices 3,0..15 (pixel 0 anchor MSB 0).</summary>
    public static readonly GoldenBlock Mode6Ascending = new(Mode: 6, Partition: 0,
        Endpoints: [10, 100, 20, 90, 30, 80], PBits: [1, 1],
        Indices: [3, 0, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        Bytes: [0x40, 0x05, 0x99, 0xA2, 0xF5, 0x40, 0xFF, 0xFF, 0x07, 0x21, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE],
        Texels: [58, 69, 81, 255, 21, 41, 61, 255, 32, 50, 67, 255, 46, 61, 75, 255, 69, 78, 88, 255, 80, 87, 94, 255, 94, 98, 102, 255, 105, 107, 108, 255, 117, 115, 114, 255, 128, 124, 120, 255, 142, 135, 128, 255, 153, 144, 134, 255, 164, 153, 141, 255, 176, 161, 147, 255, 190, 172, 155, 255, 201, 181, 161, 255]);

    /// <summary>mode 6, e0 &gt; e1 (the swapped order the anchor rule produces), p-bits 1.</summary>
    public static readonly GoldenBlock Mode6Descending = new(Mode: 6, Partition: 0,
        Endpoints: [120, 5, 64, 63, 0, 127], PBits: [1, 1],
        Indices: [7, 15, 14, 13, 12, 11, 10, 9, 8, 0, 1, 2, 3, 4, 5, 6],
        Bytes: [0x40, 0x7C, 0x01, 0xF8, 0x03, 0xFC, 0xFF, 0xFF, 0xFF, 0xDE, 0xBC, 0x9A, 0x08, 0x21, 0x43, 0x65],
        Texels: [133, 128, 120, 255, 11, 127, 255, 255, 25, 127, 239, 255, 43, 127, 219, 255, 58, 127, 203, 255, 72, 128, 188, 255, 86, 128, 172, 255, 104, 128, 152, 255, 119, 128, 136, 255, 241, 129, 1, 255, 227, 129, 17, 255, 209, 129, 37, 255, 194, 129, 53, 255, 180, 128, 68, 255, 166, 128, 84, 255, 148, 128, 104, 255]);

    /// <summary>mode 1, partition 0 (second-subset anchor 15), p-bits 0/1.</summary>
    public static readonly GoldenBlock Mode1Partition0 = new(Mode: 1, Partition: 0,
        Endpoints: [3, 60, 40, 10, 5, 50, 63, 0, 20, 21, 1, 62], PBits: [0, 1],
        Indices: [0, 3, 6, 1, 4, 7, 2, 5, 0, 3, 6, 1, 4, 7, 2, 3],
        Bytes: [0x02, 0x03, 0x8F, 0x2A, 0x85, 0xFC, 0x03, 0x54, 0x15, 0xF8, 0x32, 0x87, 0x57, 0x31, 0x87, 0xD7],
        Texels: [12, 20, 80, 255, 109, 96, 82, 255, 59, 38, 217, 255, 146, 219, 40, 255, 144, 125, 82, 255, 241, 201, 84, 255, 129, 184, 75, 255, 76, 73, 182, 255, 12, 20, 80, 255, 109, 96, 82, 255, 59, 38, 217, 255, 146, 219, 40, 255, 144, 125, 82, 255, 241, 201, 84, 255, 129, 184, 75, 255, 112, 148, 109, 255]);

    /// <summary>mode 1, partition 17 (second-subset anchor 2), p-bits 0/1.</summary>
    public static readonly GoldenBlock Mode1Partition17 = new(Mode: 1, Partition: 17,
        Endpoints: [3, 60, 40, 10, 5, 50, 63, 0, 20, 21, 1, 62], PBits: [0, 1],
        Indices: [0, 3, 2, 1, 4, 7, 2, 5, 0, 3, 6, 1, 4, 7, 2, 5],
        Bytes: [0x46, 0x03, 0x8F, 0x2A, 0x85, 0xFC, 0x03, 0x54, 0x15, 0xF8, 0x32, 0xC3, 0xAB, 0x98, 0xC3, 0xAB],
        Texels: [12, 20, 80, 255, 112, 148, 109, 255, 129, 184, 75, 255, 146, 219, 40, 255, 144, 125, 82, 255, 241, 201, 84, 255, 76, 71, 81, 255, 76, 73, 182, 255, 12, 20, 80, 255, 109, 96, 82, 255, 209, 176, 83, 255, 44, 45, 81, 255, 144, 125, 82, 255, 241, 201, 84, 255, 76, 71, 81, 255, 177, 150, 83, 255]);

    /// <summary>mode 1, partition 18 (second-subset anchor 8), p-bits 0/1.</summary>
    public static readonly GoldenBlock Mode1Partition18 = new(Mode: 1, Partition: 18,
        Endpoints: [3, 60, 40, 10, 5, 50, 63, 0, 20, 21, 1, 62], PBits: [0, 1],
        Indices: [0, 3, 6, 1, 4, 7, 2, 5, 0, 3, 6, 1, 4, 7, 2, 5],
        Bytes: [0x4A, 0x03, 0x8F, 0x2A, 0x85, 0xFC, 0x03, 0x54, 0x15, 0xF8, 0x32, 0x87, 0x57, 0x99, 0xC3, 0xAB],
        Texels: [12, 20, 80, 255, 109, 96, 82, 255, 209, 176, 83, 255, 44, 45, 81, 255, 144, 125, 82, 255, 241, 201, 84, 255, 76, 71, 81, 255, 177, 150, 83, 255, 163, 255, 6, 255, 109, 96, 82, 255, 209, 176, 83, 255, 44, 45, 81, 255, 93, 109, 148, 255, 42, 2, 251, 255, 129, 184, 75, 255, 177, 150, 83, 255]);

    /// <summary>mode 1, partition 34 (second-subset anchor 6), p-bits 0/1.</summary>
    public static readonly GoldenBlock Mode1Partition34 = new(Mode: 1, Partition: 34,
        Endpoints: [3, 60, 40, 10, 5, 50, 63, 0, 20, 21, 1, 62], PBits: [0, 1],
        Indices: [0, 3, 6, 1, 4, 7, 2, 5, 0, 3, 6, 1, 4, 7, 2, 5],
        Bytes: [0x8A, 0x03, 0x8F, 0x2A, 0x85, 0xFC, 0x03, 0x54, 0x15, 0xF8, 0x32, 0x87, 0xB7, 0x98, 0xC3, 0xAB],
        Texels: [12, 20, 80, 255, 112, 148, 109, 255, 209, 176, 83, 255, 146, 219, 40, 255, 93, 109, 148, 255, 241, 201, 84, 255, 129, 184, 75, 255, 177, 150, 83, 255, 12, 20, 80, 255, 112, 148, 109, 255, 209, 176, 83, 255, 146, 219, 40, 255, 93, 109, 148, 255, 241, 201, 84, 255, 129, 184, 75, 255, 177, 150, 83, 255]);

    /// <summary>mode 5 solid (0, 129, 255): 7-bit 0/64/127, colour index 0 everywhere.</summary>
    public static readonly GoldenBlock Mode5SolidZeroChannel = new(Mode: 5, Partition: 0,
        Endpoints: [0, 0, 64, 64, 127, 127], PBits: [],
        Indices: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        Bytes: [0x20, 0x00, 0x00, 0x10, 0xF8, 0xFF, 0xFF, 0xFF, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        Texels: [0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255, 0, 129, 255, 255]);

    /// <summary>mode 5 solid at colour index 1 (weight 21) with endpoints in swapped order.</summary>
    public static readonly GoldenBlock Mode5SolidSwapped = new(Mode: 5, Partition: 0,
        Endpoints: [30, 10, 0, 0, 127, 1], PBits: [],
        Indices: [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        Bytes: [0x20, 0x1E, 0x05, 0x00, 0xF0, 0x0F, 0xFC, 0xFF, 0xAF, 0xAA, 0xAA, 0xAA, 0x00, 0x00, 0x00, 0x00],
        Texels: [47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255, 47, 0, 172, 255]);

    public static readonly GoldenBlock[] All =
    [
        Mode6Ascending, Mode6Descending, Mode1Partition0, Mode1Partition17, Mode1Partition18, Mode1Partition34,
        Mode5SolidZeroChannel, Mode5SolidSwapped,
    ];
}
