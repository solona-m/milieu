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
}
