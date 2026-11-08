using System;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class ImcFileTests
{
    [Fact] // [EDGE-synced-delivery-8]
    public void TryRead_RejectsMalformed()
    {
        Assert.False(ImcFile.TryRead(null, 0, out _));
        Assert.False(ImcFile.TryRead([1, 0, 1], 0, out _));                      // < 4 bytes
        Assert.False(ImcFile.TryRead([0, 0, 1, 0, 0, 0, 0, 0, 0, 0], 0, out _));  // count 0
        var ring = SyncFixtures.RingImc();
        var unset = (byte[])ring.Clone();
        unset[2] = 0x17;                                                       // part 3 bit cleared
        Assert.False(ImcFile.TryRead(unset, 3, out _));
        Assert.False(ImcFile.TryRead(ring.AsSpan(0, ring.Length - 1).ToArray(), 3, out _));   // truncated
        Assert.False(ImcFile.TryRead(SyncFixtures.CushionImc(), 3, out _));    // monster layout has no part 3
    }

    [Fact] // [EDGE-synced-delivery-8]
    public void TryRead_EquipmentAndMonsterLayouts()
    {
        Assert.True(ImcFile.TryRead(SyncFixtures.RingImc(3, 5, 6, 7), 3, out var ring));
        Assert.Equal(3, ring.Count);
        for (var v = 1; v <= 3; v++) Assert.Equal(SyncFixtures.Entry(3, v, (byte)(4 + v)), ring[v - 1]);

        Assert.True(ImcFile.TryRead(SyncFixtures.CushionImc(2, 9), 0, out var cushion));
        Assert.Equal([SyncFixtures.Entry(0, 1, 9), SyncFixtures.Entry(0, 2)], cushion);
    }

    [Fact] // [EDGE-synced-delivery-6]
    public void Entry_MaskAndSoundSplit()
    {
        var e = new ImcEntry(3, 1, (ushort)(0x2A5 | (5 << 10)), 7, 2);
        Assert.Equal(0x2A5, e.AttributeMask);
        Assert.Equal(5, e.SoundId);
        Assert.Equal(e with { VfxId = 200 }, e.WithVfx(200));
    }
}
