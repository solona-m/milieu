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

    [Fact] // [AC-scene-lighting-9]
    public void IsGPosing_ReflectsClientState()
    {
        foreach (var gposing in new[] { true, false })
        {
            var client = Stub<Dalamud.Plugin.Services.IClientState>.Create(name => name == "get_IsGPosing" ? gposing : null);
            var state = new GameState(client, Stub<Dalamud.Plugin.Services.ICondition>.Create(_ => null),
                                      Stub<Dalamud.Plugin.Services.IObjectTable>.Create(_ => null));
            Assert.Equal(gposing, state.IsGPosing);
        }
    }

    /// <summary>An interface stub answering member calls by name (System.Reflection.DispatchProxy).</summary>
    public class Stub<T> : System.Reflection.DispatchProxy where T : class
    {
        private System.Func<string, object?> answer = _ => null;

        public static T Create(System.Func<string, object?> answer)
        {
            var proxy = Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).answer = answer;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            answer(method!.Name) ?? throw new System.NotSupportedException(method.Name);
    }
}
