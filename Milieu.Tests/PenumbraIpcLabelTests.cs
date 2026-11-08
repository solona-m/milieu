using System;
using Milieu.Interop;
using Xunit;

namespace Milieu.Tests;

public class PenumbraIpcLabelTests
{
    [Fact] // [EDGE-hdri-backdrop-2]
    public void VersionGate()
    {
        Assert.True(PenumbraIpc.IsCompatible((5, 19)));
        Assert.True(PenumbraIpc.IsCompatible((5, 0)));
        Assert.False(PenumbraIpc.IsCompatible((4, 30)));
        Assert.False(PenumbraIpc.IsCompatible((6, 0)));
    }

    [Fact] // [EDGE-hdri-backdrop-2]
    public void Labels_Pinned()
    {
        Assert.Equal("Penumbra.ApiVersion.V5", PenumbraIpc.LabelApiVersion);
        Assert.Equal("Penumbra.GetEnabledState", PenumbraIpc.LabelGetEnabledState);
        Assert.Equal("Penumbra.GetCollection", PenumbraIpc.LabelGetCollection);
        Assert.Equal("Penumbra.AddTemporaryModAll.V5", PenumbraIpc.LabelAddTemporaryModAll);
        Assert.Equal("Penumbra.RemoveTemporaryModAll.V5", PenumbraIpc.LabelRemoveTemporaryModAll);
        Assert.Equal("Penumbra.ResolveDefaultPath", PenumbraIpc.LabelResolveDefaultPath);
        Assert.Equal("Penumbra.Initialized", PenumbraIpc.LabelInitialized);
        Assert.Equal("Penumbra.Disposed", PenumbraIpc.LabelDisposed);
        Assert.Equal("Milieu", PenumbraIpc.Tag);
        Assert.Equal(0, PenumbraIpc.Priority);
        Assert.Equal(0xE0, PenumbraIpc.BaseCollectionType);
    }

    [Fact] // [EDGE-hdri-backdrop-39]
    public void BaseCollection_NullOrEmptyGuid_IsMissing()
    {
        Assert.True(PenumbraIpc.IsMissingCollection(null));
        Assert.True(PenumbraIpc.IsMissingCollection((Guid.Empty, "None")));
        Assert.False(PenumbraIpc.IsMissingCollection((Guid.NewGuid(), "Default")));
    }


    [Fact] // [AC-hdri-backdrop-9] [EDGE-hdri-backdrop-38] [EDGE-hdri-backdrop-39]
    public void Classify_BlockedStateOrder()
    {
        var calls = new System.Collections.Generic.List<string>();
        (int, int) V(int b) { calls.Add("v"); return (b, 19); }
        bool E(bool e) { calls.Add("e"); return e; }
        (Guid, string)? C((Guid, string)? c) { calls.Add("c"); return c; }
        var ok = (Guid.NewGuid(), "Default");

        Assert.Equal(Milieu.Backdrop.PenumbraState.Missing, PenumbraIpc.Classify(() => throw new InvalidOperationException("not ready"), () => E(true), () => C(ok)));
        Assert.Equal(Milieu.Backdrop.PenumbraState.Incompatible, PenumbraIpc.Classify(() => V(4), () => E(true), () => C(ok)));
        calls.Clear();
        Assert.Equal(Milieu.Backdrop.PenumbraState.ModsDisabled, PenumbraIpc.Classify(() => V(5), () => E(false), () => C(null)));
        Assert.Equal(new[] { "v", "e" }, calls);   // the collection is not asked once mods are known disabled
        Assert.Equal(Milieu.Backdrop.PenumbraState.NoBaseCollection, PenumbraIpc.Classify(() => V(5), () => E(true), () => C((Guid.Empty, "None"))));
        Assert.Equal(Milieu.Backdrop.PenumbraState.Available, PenumbraIpc.Classify(() => V(5), () => E(true), () => C(ok)));
    }

    [Fact] // [AC-synced-delivery-5]
    public void SyncTagAndPriority_Pinned()
    {
        Assert.Equal("MilieuSync", PenumbraIpc.SyncTag);
        Assert.Equal(1_000_000, PenumbraIpc.SyncPriority);
        Assert.Equal("Milieu", PenumbraIpc.Tag);
        Assert.Equal(0, PenumbraIpc.Priority);
        Assert.Equal("Penumbra.GetModDirectory", PenumbraIpc.LabelGetModDirectory);
        Assert.Equal("Penumbra.GetCollectionForObject.V5", PenumbraIpc.LabelGetCollectionForObject);
        Assert.Equal("Penumbra.GetCollections.V5", PenumbraIpc.LabelGetCollections);
        Assert.Equal("Penumbra.AddTemporaryMod.V5", PenumbraIpc.LabelAddTemporaryMod);
        Assert.Equal("Penumbra.RemoveTemporaryMod.V5", PenumbraIpc.LabelRemoveTemporaryMod);
        Assert.Equal("Penumbra.ResolvePlayerPath", PenumbraIpc.LabelResolvePlayerPath);
        Assert.Equal("Penumbra.GetPlayerMetaManipulations", PenumbraIpc.LabelGetPlayerMetaManipulations);
        Assert.Equal("Penumbra.RedrawObject.V5", PenumbraIpc.LabelRedrawObject);
    }
}
