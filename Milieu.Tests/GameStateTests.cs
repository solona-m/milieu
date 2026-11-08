using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Milieu.Interop;
using Xunit;

namespace Milieu.Tests;

public class GameStateTests
{
    private static System.Func<ConditionFlag, bool> Flags(params ConditionFlag[] set)
    {
        var s = new HashSet<ConditionFlag>(set);
        return s.Contains;
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-33]
    public void GPose_StaysReady()
    {
        // GPose sets WatchingCutscene (Dalamud's GposeActive); that alone must not count as a cutscene.
        Assert.True(GameState.Ready(true, true, gposing: true, Flags(ConditionFlag.WatchingCutscene)));
        Assert.True(GameState.Ready(true, true, gposing: false, Flags(ConditionFlag.WatchingCutscene)));
        Assert.False(GameState.IsCutscene(Flags(ConditionFlag.WatchingCutscene)));
        // Even GPose entered from a cutscene-ish state keeps the dome.
        Assert.True(GameState.Ready(true, true, gposing: true, Flags(ConditionFlag.WatchingCutscene78)));
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-34]
    public void Cutscene_And_BetweenAreas_NotReady()
    {
        Assert.False(GameState.Ready(true, true, false, Flags(ConditionFlag.OccupiedInCutSceneEvent)));
        Assert.False(GameState.Ready(true, true, false, Flags(ConditionFlag.WatchingCutscene78)));
        Assert.False(GameState.Ready(true, true, false, Flags(ConditionFlag.BetweenAreas)));
        Assert.False(GameState.Ready(true, true, true, Flags(ConditionFlag.BetweenAreas51)));   // GPose never overrides a zone change
        Assert.False(GameState.Ready(false, true, false, Flags()));
        Assert.False(GameState.Ready(true, false, false, Flags()));
        Assert.True(GameState.Ready(true, true, false, Flags()));
    }
}
