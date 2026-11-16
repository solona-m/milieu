using System;
using Milieu.Interop;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class PenumbraRedrawTests
{
    [Fact] // [AC-synced-lighting-7]
    public void RedrawAll_Used_PlayerOnlyAsFallback()
    {
        var log = new FakeLog();
        int all = 0, player = 0;
        PenumbraIpc.RedrawWithFallback(() => all++, () => player++, log);
        Assert.Equal((1, 0), (all, player));
    }

    [Fact] // [EDGE-synced-lighting-17]
    public void RedrawAllThrows_FallsBackToPlayer_BothThrowing_OnlyLogs()
    {
        var log = new FakeLog();
        var player = 0;
        PenumbraIpc.RedrawWithFallback(() => throw new InvalidOperationException(), () => player++, log);
        Assert.Equal(1, player);
        PenumbraIpc.RedrawWithFallback(() => throw new InvalidOperationException(), () => throw new InvalidOperationException(), log);
        Assert.All(log.Lines, l => Assert.Equal("Debug", l.Level));
    }
}
