using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Milieu.Backdrop;

namespace Milieu.Interop;

/// <summary>Login, readiness and player position from Dalamud services. Framework thread only.</summary>
public sealed class GameState(IClientState clientState, ICondition condition, IObjectTable objects) : IGameState
{
    public bool IsLoggedIn => clientState.IsLoggedIn;

    public bool BetweenAreas => condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];

    public bool InCutscene => condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78]
                              || condition[ConditionFlag.OccupiedInCutSceneEvent];

    /// <summary>GPose deliberately does not count: screenshots are the main use.</summary>
    public bool IsReady => IsLoggedIn && objects.LocalPlayer != null && !BetweenAreas && !InCutscene;

    public Vector3? PlayerPosition => objects.LocalPlayer?.Position;
}
