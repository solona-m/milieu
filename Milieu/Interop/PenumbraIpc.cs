using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Milieu.Backdrop;

namespace Milieu.Interop;

/// <summary>
/// Penumbra IPC by label (labels and signatures copied from Penumbra.Api's IpcSubscribers, MIT). Local delivery creates
/// one temporary mod, tag "Milieu", priority 0, in all collections. Synced delivery (docs/adr/0006) creates one temporary
/// mod, tag "MilieuSync", priority 1,000,000, in the local player's collection only. Milieu reads Penumbra's enabled
/// state, collections, mod directory and the player's meta; it never calls a mod-settings or mod-management API.
/// </summary>
public sealed class PenumbraIpc : IPenumbraRedirects, IPenumbraSync, IDisposable
{
    public const string LabelApiVersion = "Penumbra.ApiVersion.V5";
    public const string LabelGetEnabledState = "Penumbra.GetEnabledState";
    public const string LabelGetCollection = "Penumbra.GetCollection";
    public const string LabelAddTemporaryModAll = "Penumbra.AddTemporaryModAll.V5";
    public const string LabelRemoveTemporaryModAll = "Penumbra.RemoveTemporaryModAll.V5";
    public const string LabelResolveDefaultPath = "Penumbra.ResolveDefaultPath";
    public const string LabelInitialized = "Penumbra.Initialized";
    public const string LabelDisposed = "Penumbra.Disposed";
    public const string LabelGetModDirectory = "Penumbra.GetModDirectory";
    public const string LabelGetCollectionForObject = "Penumbra.GetCollectionForObject.V5";
    public const string LabelGetCollections = "Penumbra.GetCollections.V5";
    public const string LabelAddTemporaryMod = "Penumbra.AddTemporaryMod.V5";
    public const string LabelRemoveTemporaryMod = "Penumbra.RemoveTemporaryMod.V5";
    public const string LabelResolvePlayerPath = "Penumbra.ResolvePlayerPath";
    public const string LabelGetPlayerMetaManipulations = "Penumbra.GetPlayerMetaManipulations";
    public const string LabelRedrawObject = "Penumbra.RedrawObject.V5";

    public const string Tag = "Milieu";
    public const int Priority = 0;
    public const string SyncTag = "MilieuSync";
    public const int SyncPriority = 1_000_000;
    public const int RequiredBreaking = 5;
    public const byte BaseCollectionType = 0xE0;   // ApiCollectionType.Default

    private readonly ILogSink log;
    private readonly string? sensitiveDir;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<bool> enabledState;
    private readonly ICallGateSubscriber<byte, (Guid, string)?> getCollection;
    private readonly ICallGateSubscriber<string, Dictionary<string, string>, string, int, int> addAll;
    private readonly ICallGateSubscriber<string, int, int> removeAll;
    private readonly ICallGateSubscriber<string, string> resolveDefault;
    private readonly ICallGateSubscriber<object> initialized;
    private readonly ICallGateSubscriber<object> disposed;
    private readonly ICallGateSubscriber<string> getModDirectory;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> getCollectionForObject;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> getCollections;
    private readonly ICallGateSubscriber<string, Guid, Dictionary<string, string>, string, int, int> addMod;
    private readonly ICallGateSubscriber<string, Guid, int, int> removeMod;
    private readonly ICallGateSubscriber<string, string> resolvePlayer;
    private readonly ICallGateSubscriber<string> playerMeta;
    private readonly ICallGateSubscriber<int, int, object> redrawObject;
    private readonly Action onInitialized;
    private readonly Action onDisposed;

    public PenumbraState State { get; private set; } = PenumbraState.Missing;

    public event Action? Initialized;
    public event Action? Disposed;

    public PenumbraIpc(IDalamudPluginInterface pi, ILogSink log, string? sensitiveDir = null)
    {
        this.log = log;
        this.sensitiveDir = sensitiveDir;
        apiVersion = pi.GetIpcSubscriber<(int, int)>(LabelApiVersion);
        enabledState = pi.GetIpcSubscriber<bool>(LabelGetEnabledState);
        getCollection = pi.GetIpcSubscriber<byte, (Guid, string)?>(LabelGetCollection);
        addAll = pi.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>(LabelAddTemporaryModAll);
        removeAll = pi.GetIpcSubscriber<string, int, int>(LabelRemoveTemporaryModAll);
        resolveDefault = pi.GetIpcSubscriber<string, string>(LabelResolveDefaultPath);
        initialized = pi.GetIpcSubscriber<object>(LabelInitialized);
        disposed = pi.GetIpcSubscriber<object>(LabelDisposed);
        getModDirectory = pi.GetIpcSubscriber<string>(LabelGetModDirectory);
        getCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>(LabelGetCollectionForObject);
        getCollections = pi.GetIpcSubscriber<Dictionary<Guid, string>>(LabelGetCollections);
        addMod = pi.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>(LabelAddTemporaryMod);
        removeMod = pi.GetIpcSubscriber<string, Guid, int, int>(LabelRemoveTemporaryMod);
        resolvePlayer = pi.GetIpcSubscriber<string, string>(LabelResolvePlayerPath);
        playerMeta = pi.GetIpcSubscriber<string>(LabelGetPlayerMetaManipulations);
        redrawObject = pi.GetIpcSubscriber<int, int, object>(LabelRedrawObject);
        onInitialized = () => Initialized?.Invoke();
        onDisposed = () => Disposed?.Invoke();
        initialized.Subscribe(onInitialized);
        disposed.Subscribe(onDisposed);
        Refresh();
    }

    public static bool IsCompatible((int Breaking, int Features) version) => version.Breaking == RequiredBreaking;

    /// <summary>
    /// The blocked-state order: Missing (any call throws, e.g. IpcNotReadyError) → Incompatible → ModsDisabled →
    /// NoBaseCollection → Available. Later calls are made only when the earlier ones passed.
    /// </summary>
    public static PenumbraState Classify(Func<(int, int)> version, Func<bool> enabled, Func<(Guid, string)?> baseCollection)
    {
        try
        {
            if (!IsCompatible(version())) return PenumbraState.Incompatible;
            if (!enabled()) return PenumbraState.ModsDisabled;
            if (IsMissingCollection(baseCollection())) return PenumbraState.NoBaseCollection;
            return PenumbraState.Available;
        }
        catch (Exception)
        {
            return PenumbraState.Missing;
        }
    }

    /// <summary>A Base collection of None is reported as null or as the empty collection's identity.</summary>
    public static bool IsMissingCollection((Guid Id, string Name)? collection) => collection == null || collection.Value.Id == Guid.Empty;

    public void Refresh()
    {
        State = Classify(() => apiVersion.InvokeFunc(), () => enabledState.InvokeFunc(), () => getCollection.InvokeFunc(BaseCollectionType));
        ModDirectory = State == PenumbraState.Missing ? null : ReadModDirectory();
    }

    // ---------------------------------------------------------------- synced delivery (IPenumbraSync)

    public string? ModDirectory { get; private set; }

    private string? ReadModDirectory()
    {
        try
        {
            var dir = getModDirectory.InvokeFunc();
            return !string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir) ? dir : null;
        }
        catch (Exception) { return null; }
    }

    private string?[] Sensitive => [sensitiveDir, ModDirectory];

    public PlayerCollectionInfo? PlayerCollection()
    {
        try
        {
            var (valid, individual, (id, _)) = getCollectionForObject.InvokeFunc(0);
            return valid && id != Guid.Empty ? new PlayerCollectionInfo(id, individual) : null;
        }
        catch (Exception e)
        {
            log.Debug("[Milieu] GetCollectionForObject failed: " + LogText.Describe(e, Sensitive));
            return null;
        }
    }

    public Guid? BaseCollection()
    {
        try
        {
            var c = getCollection.InvokeFunc(BaseCollectionType);
            return IsMissingCollection(c) ? null : c!.Value.Item1;
        }
        catch (Exception) { return null; }
    }

    public int SetSynced(Guid collection, IReadOnlyDictionary<string, string> paths, string manips)
    {
        try { return addMod.InvokeFunc(SyncTag, collection, new Dictionary<string, string>(paths), manips, SyncPriority); }
        catch (Exception e)
        {
            log.Warning("[Milieu] AddTemporaryMod failed: " + LogText.Describe(e, Sensitive));
            return -1;
        }
    }

    public void ClearSynced(Guid collection)
    {
        try { removeMod.InvokeFunc(SyncTag, collection, SyncPriority); }
        catch (Exception e) { log.Debug("[Milieu] RemoveTemporaryMod failed: " + LogText.Describe(e, Sensitive)); }
    }

    public void ClearSyncedEverywhere(IEnumerable<Guid> alsoClear)
    {
        var all = new HashSet<Guid>(alsoClear);
        try { foreach (var id in getCollections.InvokeFunc().Keys) all.Add(id); }
        catch (Exception e) { log.Debug("[Milieu] GetCollections failed: " + LogText.Describe(e, Sensitive)); }
        foreach (var id in all) ClearSynced(id);
    }

    public string ResolvePlayer(string gamePath)
    {
        try { return resolvePlayer.InvokeFunc(gamePath); }
        catch (Exception) { return gamePath; }
    }

    public string PlayerMeta()
    {
        try { return playerMeta.InvokeFunc() ?? ""; }
        catch (Exception e)
        {
            log.Debug("[Milieu] GetPlayerMetaManipulations failed: " + LogText.Describe(e, Sensitive));
            return "";
        }
    }

    public void RedrawPlayer()
    {
        try { redrawObject.InvokeAction(0, 0); }
        catch (Exception e) { log.Debug("[Milieu] RedrawObject failed: " + LogText.Describe(e, Sensitive)); }
    }

    public bool Set(IReadOnlyDictionary<string, string> redirects)
    {
        try
        {
            var ec = addAll.InvokeFunc(Tag, new Dictionary<string, string>(redirects), string.Empty, Priority);
            if (ec == 0) return true;
            log.Warning($"[Milieu] Penumbra refused the backdrop redirect (code {ec}).");
            return false;
        }
        catch (Exception e)
        {
            log.Warning("[Milieu] AddTemporaryModAll failed: " + LogText.Describe(e, sensitiveDir));
            return false;
        }
    }

    public void Clear()
    {
        try { removeAll.InvokeFunc(Tag, Priority); }
        catch (Exception e) { log.Debug("[Milieu] RemoveTemporaryModAll failed: " + LogText.Describe(e, sensitiveDir)); }
    }

    public string Resolve(string gamePath)
    {
        try { return resolveDefault.InvokeFunc(gamePath); }
        catch (Exception) { return gamePath; }
    }

    public void Dispose()
    {
        initialized.Unsubscribe(onInitialized);
        disposed.Unsubscribe(onDisposed);
    }
}
