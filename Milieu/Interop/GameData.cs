using System;
using Dalamud.Plugin.Services;
using Milieu.Backdrop;

namespace Milieu.Interop;

/// <summary>The game's own files through Dalamud's data manager (vanilla data, before any mod redirect).</summary>
public sealed class GameData(IDataManager data) : IGameData
{
    public byte[]? ReadVanilla(string gamePath)
    {
        try { return data.GetFile(gamePath)?.Data; }
        catch (Exception) { return null; }
    }
}
