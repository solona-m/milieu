using System;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class SyncedDeliveryTests
{
    private readonly FakePenumbra pen = new() { ModDirectory = @"D:\mods" };
    private readonly FakeGameData data = new();
    private readonly FakeLog log = new();
    private readonly SyncedDelivery d;
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public SyncedDeliveryTests()
    {
        data.Files[SyncTarget.MadBirdRing.ImcPath] = SyncFixtures.RingImc(2);
        data.Files[SyncTarget.Cushion.ImcPath] = SyncFixtures.CushionImc(2);
        d = new SyncedDelivery(pen, data, log);
    }

    private static PreparedTexture Tex(string key = "00112233445566ff") =>
        new(key, $@"D:\mods\_milieu_sync\sky_{key}.atex", CacheStore.TextureGamePath(key), 8, 4, ImageKind.Jpeg, []);

    private MessageKey Begin(SyncTarget? t = null) => d.Begin(Tex(), @"D:\mods\_milieu_sync\dome_x.avfx", t ?? SyncTarget.MadBirdRing).Failure;

    [Fact] // [EDGE-synced-delivery-4]
    public void SetThrows_TreatedAsRefused()
    {
        pen.SetSyncedThrows = true;
        var (failure, message) = d.Begin(Tex(), "x", SyncTarget.MadBirdRing);
        Assert.Equal(MessageKey.SyncRefused, failure);
        Assert.Equal("Penumbra refused the synced backdrop (code -1).", message!.Text);
        Assert.Null(d.Pending);
    }

    [Fact] // [EDGE-synced-delivery-28]
    public void SharedCollection_WarnsWhenNotIndividual()
    {
        pen.Player = new PlayerCollectionInfo(A, false);
        Begin();
        var w = Assert.Single(d.Commit());
        Assert.Equal(MessageKey.SyncSharedCollection, w.Key);
        Assert.Contains("Mad Bird Ring", w.Text);
    }

    [Fact] // [EDGE-synced-delivery-28]
    public void SharedCollection_WarnsWhenBaseGuid()
    {
        pen.Player = new PlayerCollectionInfo(A, true);
        pen.Base = A;
        Begin();
        Assert.Equal(MessageKey.SyncSharedCollection, Assert.Single(d.Commit()).Key);
    }

    [Theory] // [EDGE-synced-delivery-28]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedCollection_NoWarningWhenIndividualAndNotBase(bool baseUnknown)
    {
        pen.Player = new PlayerCollectionInfo(A, true);
        pen.Base = baseUnknown ? null : Guid.NewGuid();
        Begin();
        Assert.Empty(d.Commit());
    }

    [Fact] // [EDGE-synced-delivery-29]
    public void BaseEntriesCached_NotReReadWhileLive()
    {
        Assert.Equal(MessageKey.None, Begin());
        d.Commit();
        // The live meta now holds Milieu's own entry (VfxId 200) for variant 1.
        pen.Meta = SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(1), SyncFixtures.Entry(3, 1, 200))]);
        Assert.Equal(MessageKey.None, Begin());
        Assert.Equal(1, pen.MetaReads);
        Assert.Equal(1, data.Reads);
        Assert.Equal(200, d.Pending!.Plan.VfxId);
    }

    [Fact] // [EDGE-synced-delivery-29]
    public void AbortPendingWithoutShown_ThenBegin_ReReadsMeta()
    {
        Begin();
        d.AbortPending();
        Begin();
        Assert.Equal(2, pen.MetaReads);
    }

    [Fact] // [EDGE-synced-delivery-29]
    public void Refused_ThenBegin_ReReadsMeta()
    {
        pen.SetSyncedResult = 11;
        Assert.Equal(MessageKey.SyncRefused, Begin());
        pen.SetSyncedResult = 0;
        Begin();
        Assert.Equal(2, pen.MetaReads);
    }

    [Fact] // [EDGE-synced-delivery-29]
    public void Forget_ThenBegin_ReReadsMeta()
    {
        Begin(); d.Commit();
        d.Forget();
        Begin();
        Assert.Equal(2, pen.MetaReads);
    }

    [Fact] // [EDGE-synced-delivery-29]
    public void RemoveAll_ThenBegin_ReReadsMeta()
    {
        Begin(); d.Commit();
        d.RemoveAll(redraw: true);
        Begin();
        Assert.Equal(2, pen.MetaReads);
    }

    [Fact] // [EDGE-synced-delivery-29]
    public void ReRead_IgnoresOverrideWithLastSessionVfxId()
    {
        Begin(); d.Commit();
        d.RemoveAll(redraw: true);
        // Penumbra hasn't applied the removal yet: the meta still holds Milieu's stale entry with VfxId 200.
        pen.Meta = SyncFixtures.V1([(SyncTarget.MadBirdRing.KeyOf(1), new ImcEntry(9, 9, 9, 200, 9))]);
        Begin();
        Assert.Equal(200, d.Pending!.Plan.VfxId);
        var json = PenumbraMeta.DecodeV0Json(d.Pending.Plan.Manips);
        Assert.Contains("\"MaterialId\":31", json);    // variant 1 is vanilla (Entry(3,1) → material 31), not the stale 9
        Assert.DoesNotContain("\"MaterialId\":9,", json);
    }

    [Fact] // [EDGE-synced-delivery-31]
    public void ClearEverywhere_IncludesSessionGuids()
    {
        pen.Listed = [Guid.NewGuid()];
        var temp = Guid.NewGuid();
        pen.Player = new PlayerCollectionInfo(temp, true);
        Begin(); d.Commit();
        d.Forget();
        d.ClearEverywhere();
        Assert.Contains(temp, pen.ClearEverywhereCalls.Single());
    }

    [Fact] // [EDGE-synced-delivery-3]
    public void NoPlayerCollection_Fails_NothingRegistered()
    {
        pen.Player = null;
        Assert.Equal(MessageKey.SyncNoPlayerCollection, Begin());
        Assert.Empty(pen.SyncSets);
    }

    [Fact] // [EDGE-synced-delivery-10]
    public void Begin_InNewCollection_ClearsOldFirst_FreshBase()
    {
        Begin(); d.Commit();
        var b = Guid.NewGuid();
        pen.Player = new PlayerCollectionInfo(b, true);
        Assert.True(d.CollectionMoved());
        Begin();
        Assert.Equal(A, pen.SyncClears.Single());
        Assert.Equal(b, pen.SyncSets[^1].Collection);
        Assert.Equal(2, pen.MetaReads);
    }

    [Fact] // [EDGE-synced-delivery-7]
    public void UnreadableMeta_LogsDebug_UsesVanilla()
    {
        pen.Meta = "not base64 !!";
        Assert.Equal(MessageKey.None, Begin());
        Assert.Contains(log.Lines, l => l.Level == "Debug" && l.Text.Contains("meta manipulations"));
        pen.Meta = "";
        d.RemoveAll(redraw: false);
        var lines = log.Lines.Count;
        Begin();
        Assert.Equal(lines, log.Lines.Count);   // an empty meta is readable: nothing logged
    }

    [Fact] // [EDGE-synced-delivery-10] [EDGE-synced-delivery-4]
    public void CollectionMoved_NewAddRefused_RedrawsAfterClearingOld()
    {
        Begin(); d.Commit();
        var redraws = pen.Redraws;
        pen.Player = new PlayerCollectionInfo(Guid.NewGuid(), true);
        pen.SetSyncedResult = 11;
        Assert.Equal(MessageKey.SyncRefused, Begin());
        Assert.Equal(A, pen.SyncClears.Single());
        Assert.Equal(redraws + 1, pen.Redraws);
        Assert.Null(d.Shown);
        Assert.Null(d.Pending);
    }
}
