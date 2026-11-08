using System;
using System.Text;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class PenumbraMetaTests
{
    [Fact] // [EDGE-synced-delivery-6]
    public void ReadImcOverrides_V1Layout()
    {
        var a = (SyncTarget.MadBirdRing.KeyOf(1), new ImcEntry(3, 0, (ushort)(0x2A5 | (5 << 10)), 0, 1));
        var b = (SyncTarget.Cushion.KeyOf(2), new ImcEntry(1, 2, 7, 9, 0));
        var map = PenumbraMeta.ReadImcOverrides(SyncFixtures.V1([a, b]));
        Assert.Equal(2, map.Count);
        Assert.Equal(a.Item2, map[a.Item1]);
        Assert.Equal(b.Item2, map[b.Item1]);
    }

    public static TheoryData<string> BadInputs()
    {
        var v1 = SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(1), new ImcEntry(1, 0, 0, 0, 0))]);
        var raw = PenumbraMeta.Gunzip(Convert.FromBase64String(v1))!;
        var v0 = (byte[])raw.Clone(); v0[0] = 0;
        var v2 = (byte[])raw.Clone(); v2[0] = 2;
        var prefix = (byte[])raw.Clone(); prefix[1] = (byte)'X';
        var big = (byte[])raw.Clone(); BitConverter.GetBytes(1_000_000).CopyTo(big, 9);
        return new TheoryData<string>
        {
            "",
            "not base64 !!",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("plain, not gzip")),
            SyncFixtures.Gzip64(v0),
            SyncFixtures.Gzip64(v2),
            SyncFixtures.Gzip64(prefix),
            SyncFixtures.Gzip64(big),
            SyncFixtures.Gzip64(raw[..12]),
        };
    }

    [Theory] // [EDGE-synced-delivery-7]
    [MemberData(nameof(BadInputs))]
    public void ReadImcOverrides_BadInputs_Empty(string input) => Assert.Empty(PenumbraMeta.ReadImcOverrides(input));

    [Fact] // [AC-synced-delivery-3]
    public void EncodeV0_VersionByteAndKeyOrder()
    {
        var s = PenumbraMeta.EncodeImcV0(SyncTarget.MadBirdRing, [(1, new ImcEntry(3, 1, (ushort)(0x2A5 | (5 << 10)), 200, 2))]);
        var raw = PenumbraMeta.Gunzip(Convert.FromBase64String(s))!;
        Assert.Equal(0, raw[0]);
        Assert.Equal("[{\"Type\":\"Imc\",\"Manipulation\":{\"Entry\":{\"MaterialId\":3,\"DecalId\":1,\"VfxId\":200,\"MaterialAnimationId\":2,"
                     + "\"AttributeMask\":677,\"SoundId\":5},\"ObjectType\":\"Accessory\",\"PrimaryId\":28,\"Variant\":1,\"EquipSlot\":\"RFinger\"}}]",
                     PenumbraMeta.DecodeV0Json(s));
    }
}
