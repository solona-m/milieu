using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Milieu.Backdrop;

namespace Milieu.Interop;

/// <summary>Login, readiness and player position from Dalamud services. Framework thread only.</summary>
public sealed class GameState(IClientState clientState, ICondition condition, IObjectTable objects) : IGameState
{
    public bool IsLoggedIn => clientState.IsLoggedIn;

    public bool BetweenAreas => IsBetweenAreas(f => condition[f]);

    public bool IsReady => Ready(IsLoggedIn, objects.LocalPlayer != null, clientState.IsGPosing, f => condition[f]);

    public Vector3? PlayerPosition => objects.LocalPlayer?.Position;

    public static bool IsBetweenAreas(Func<ConditionFlag, bool> flag) => flag(ConditionFlag.BetweenAreas) || flag(ConditionFlag.BetweenAreas51);

    /// <summary>
    /// A real cutscene, as Dalamud's UiBuilder defines CutsceneActive. <c>WatchingCutscene</c> is deliberately NOT here:
    /// it is the flag GPose sets (Dalamud's GposeActive), and the dome must stay up in GPose.
    /// </summary>
    public static bool IsCutscene(Func<ConditionFlag, bool> flag) => flag(ConditionFlag.OccupiedInCutSceneEvent) || flag(ConditionFlag.WatchingCutscene78);

    /// <summary>Ready = logged in, a local player, not between areas, not in a cutscene. GPose always counts as ready.</summary>
    public static bool Ready(bool loggedIn, bool hasPlayer, bool gposing, Func<ConditionFlag, bool> flag) =>
        loggedIn && hasPlayer && !IsBetweenAreas(flag) && (gposing || !IsCutscene(flag));
}
