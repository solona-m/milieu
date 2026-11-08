using System.Linq;
using Milieu.Sync;
using Xunit;

namespace Milieu.Tests;

public class SyncTargetTests
{
    [Fact] // [AC-synced-delivery-4]
    public void Paths_PinnedForBothTargets()
    {
        Assert.Equal("chara/accessory/a0028/a0028.imc", SyncTarget.MadBirdRing.ImcPath);
        Assert.Equal("chara/monster/m8058/obj/body/b0001/b0001.imc", SyncTarget.Cushion.ImcPath);
        Assert.Equal("chara/accessory/a0028/vfx/eff/va0200.avfx", SyncTarget.MadBirdRing.VfxPath(200));
        Assert.Equal("chara/accessory/a0028/vfx/eff/va0007.avfx", SyncTarget.MadBirdRing.VfxPath(7));
        Assert.Equal("chara/monster/m8058/obj/body/b0001/vfx/eff/vm0200.avfx", SyncTarget.Cushion.VfxPath(200));
        Assert.Equal("chara/monster/m8058/obj/body/b0001/vfx/eff/vm0007.avfx", SyncTarget.Cushion.VfxPath(7));
        Assert.Equal(3, SyncTarget.MadBirdRing.PartIndex);
        Assert.Equal(0, SyncTarget.Cushion.PartIndex);
        Assert.Equal((3, 6, 12, 4), (SyncTarget.ObjectTypeAccessory, SyncTarget.ObjectTypeMonster, SyncTarget.EquipSlotRFinger, SyncTarget.BodySlotBody));
        Assert.Equal(new ImcKey(28, 2, 3, 0, 12, 0), SyncTarget.MadBirdRing.KeyOf(2));
        Assert.Equal(new ImcKey(8058, 1, 6, 1, 0, 4), SyncTarget.Cushion.KeyOf(1));
        Assert.Same(SyncTarget.Cushion, SyncTarget.For(DeliveryMode.Cushion));
        Assert.Same(SyncTarget.MadBirdRing, SyncTarget.For(DeliveryMode.MadBirdRing));
        Assert.Null(SyncTarget.For(DeliveryMode.Local));
    }

    [Fact] // [EDGE-synced-delivery-9]
    public void ChooseVfxId_SkipsUsed_NullWhenFull()
    {
        Assert.Equal((byte)200, SyncTarget.ChooseVfxId([0, 1, 2]));
        Assert.Equal((byte)201, SyncTarget.ChooseVfxId([0, 200]));
        Assert.Equal((byte)203, SyncTarget.ChooseVfxId([200, 201, 202]));
        Assert.Null(SyncTarget.ChooseVfxId(Enumerable.Range(200, 55).Select(i => (byte)i)));
        Assert.Equal((byte)254, SyncTarget.ChooseVfxId(Enumerable.Range(200, 54).Select(i => (byte)i)));
    }
}
