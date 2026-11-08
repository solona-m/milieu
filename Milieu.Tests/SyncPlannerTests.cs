using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Milieu.Backdrop;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class SyncPlannerTests
{
    private const string Atex = "vfx/milieu/0011223344556677/sky.atex";

    private static (SyncPlan Plan, List<JsonElement> Manips) Plan(SyncTarget t, byte[] imc, string meta = "")
    {
        var (b, f) = SyncPlanner.ResolveBase(t, imc, meta);
        Assert.Equal(MessageKey.None, f);
        var (plan, f2) = SyncPlanner.Build(b!, Atex, @"D:\mods\_milieu_sync\sky_x.atex", @"D:\mods\_milieu_sync\dome_y.avfx");
        Assert.Equal(MessageKey.None, f2);
        var json = JsonDocument.Parse(PenumbraMeta.DecodeV0Json(plan!.Manips)).RootElement.EnumerateArray().ToList();
        return (plan, json);
    }

    private static ImcEntry EntryOf(JsonElement m)
    {
        var e = m.GetProperty("Manipulation").GetProperty("Entry");
        var mask = e.GetProperty("AttributeMask").GetUInt16();
        var sound = e.GetProperty("SoundId").GetByte();
        return new ImcEntry(e.GetProperty("MaterialId").GetByte(), e.GetProperty("DecalId").GetByte(), (ushort)(mask | (sound << 10)),
                            e.GetProperty("VfxId").GetByte(), e.GetProperty("MaterialAnimationId").GetByte());
    }

    [Fact] // [AC-synced-delivery-3]
    public void Cushion_Manips_DecodeToMonsterImcPerVariant()
    {
        var (plan, manips) = Plan(SyncTarget.Cushion, SyncFixtures.CushionImc(2));
        Assert.Equal(2, manips.Count);
        for (var i = 0; i < manips.Count; i++)
        {
            var m = manips[i];
            Assert.Equal("Imc", m.GetProperty("Type").GetString());
            var man = m.GetProperty("Manipulation");
            Assert.Equal("Monster", man.GetProperty("ObjectType").GetString());
            Assert.Equal(8058, man.GetProperty("PrimaryId").GetInt32());
            Assert.Equal(1, man.GetProperty("SecondaryId").GetInt32());
            Assert.Equal("Body", man.GetProperty("BodySlot").GetString());
            Assert.Equal(i + 1, man.GetProperty("Variant").GetInt32());
            Assert.Equal(200, man.GetProperty("Entry").GetProperty("VfxId").GetInt32());
        }
        Assert.Equal(200, plan.VfxId);
        Assert.Equal(@"D:\mods\_milieu_sync\dome_y.avfx", plan.Paths["chara/monster/m8058/obj/body/b0001/vfx/eff/vm0200.avfx"]);
        Assert.Equal(@"D:\mods\_milieu_sync\sky_x.atex", plan.Paths[Atex]);
    }

    [Fact] // [AC-synced-delivery-4]
    public void Ring_Manips_AccessoryRFinger()
    {
        var (plan, manips) = Plan(SyncTarget.MadBirdRing, SyncFixtures.RingImc(3));
        Assert.Equal(3, manips.Count);
        foreach (var m in manips.Select(x => x.GetProperty("Manipulation")))
        {
            Assert.Equal("Accessory", m.GetProperty("ObjectType").GetString());
            Assert.Equal(28, m.GetProperty("PrimaryId").GetInt32());
            Assert.Equal("RFinger", m.GetProperty("EquipSlot").GetString());
            Assert.False(m.TryGetProperty("SecondaryId", out _));
            Assert.False(m.TryGetProperty("BodySlot", out _));
        }
        // Entries come from part index 3.
        for (var v = 1; v <= 3; v++) Assert.Equal(SyncFixtures.Entry(3, v).WithVfx(200), EntryOf(manips[v - 1]));
        Assert.Equal("chara/accessory/a0028/vfx/eff/va0200.avfx", plan.AvfxGamePath);
    }

    [Fact] // [AC-synced-delivery-5]
    public void OnlyVfxIdChanges_VanillaBase()
    {
        var (_, manips) = Plan(SyncTarget.MadBirdRing, SyncFixtures.RingImc(3, 4, 0, 9));
        var vanilla = new[] { SyncFixtures.Entry(3, 1, 4), SyncFixtures.Entry(3, 2), SyncFixtures.Entry(3, 3, 9) };
        for (var i = 0; i < 3; i++) Assert.Equal(vanilla[i] with { VfxId = 200 }, EntryOf(manips[i]));
    }

    [Fact] // [AC-synced-delivery-5]
    public void UserOverrideWithOwnVfx_ReplacedByN()
    {
        var o = new ImcEntry(7, 3, 0x1FF, 5, 1);
        var (plan, manips) = Plan(SyncTarget.MadBirdRing, SyncFixtures.RingImc(2), SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(1), o)]));
        Assert.Equal(o.WithVfx(200), EntryOf(manips[0]));
        var (b, _) = SyncPlanner.ResolveBase(SyncTarget.MadBirdRing, SyncFixtures.RingImc(2), SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(1), o)]));
        Assert.Contains((byte)5, b!.UsedVfxIds);
        Assert.Equal(200, plan.VfxId);
    }

    [Fact] // [AC-synced-delivery-5]
    public void VfxId_AvoidsVanillaAndOverrideIds()
    {
        var meta = SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(2), new ImcEntry(1, 0, 0, 201, 0))]);
        var (plan, _) = Plan(SyncTarget.MadBirdRing, SyncFixtures.RingImc(3, 0, 1, 200), meta);
        Assert.Equal(202, plan.VfxId);
        Assert.Contains("chara/accessory/a0028/vfx/eff/va0202.avfx", plan.Paths.Keys);
    }

    [Fact] // [EDGE-synced-delivery-6]
    public void UserImcOverride_Preserved()
    {
        var o = new ImcEntry(3, 0, (ushort)(0x2A5 | (5 << 10)), 0, 1);
        var (_, manips) = Plan(SyncTarget.MadBirdRing, SyncFixtures.RingImc(2), SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(1), o)]));
        var first = EntryOf(manips[0]);
        Assert.Equal((3, 0x2A5, 5, 200), (first.MaterialId, first.AttributeMask, first.SoundId, first.VfxId));
        Assert.Equal(SyncFixtures.Entry(3, 2).WithVfx(200), EntryOf(manips[1]));
    }

    [Fact] // [EDGE-synced-delivery-26]
    public void OnlyTargetPart_AllVariants()
    {
        var (_, manips) = Plan(SyncTarget.MadBirdRing, SyncFixtures.RingImc(3));
        Assert.Equal(3, manips.Count);
        Assert.All(manips, m => Assert.Equal("RFinger", m.GetProperty("Manipulation").GetProperty("EquipSlot").GetString()));
        Assert.Equal([1, 2, 3], manips.Select(m => m.GetProperty("Manipulation").GetProperty("Variant").GetInt32()));
    }

    [Fact] // [EDGE-synced-delivery-30]
    public void OverrideVfxId200_Skipped()
    {
        var meta = SyncFixtures.V1([(SyncTarget.Cushion.KeyOf(1), new ImcEntry(1, 0, 0, 200, 0))]);
        var (plan, _) = Plan(SyncTarget.Cushion, SyncFixtures.CushionImc(2), meta);
        Assert.Equal(201, plan.VfxId);
    }

    [Fact] // [EDGE-synced-delivery-8] [EDGE-synced-delivery-9]
    public void Failures_UnreadableAndNoFreeId()
    {
        Assert.Equal(MessageKey.SyncGameDataUnreadable, SyncPlanner.ResolveBase(SyncTarget.Cushion, null, "").Failure);
        var full = Enumerable.Range(200, 55).Select(i => (byte)i).ToHashSet();
        var b = new SyncBase(SyncTarget.Cushion, [SyncFixtures.Entry(0, 1)], full);
        Assert.Equal(MessageKey.SyncNoFreeVfxId, SyncPlanner.Build(b, Atex, "a", "b").Failure);
    }
}
