using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Milieu.Backdrop;

namespace Milieu.Interop;

/// <summary>
/// Penumbra IPC by label (labels and signatures copied from Penumbra.Api's IpcSubscribers, MIT). Milieu creates one
/// temporary mod, tag "Milieu", priority 0, in all collections, and only reads Penumbra's enabled state and Base
/// collection. It never calls a mod-settings or mod-management API.
/// </summary>
public sealed class PenumbraIpc : IPenumbraRedirects, IDisposable
{
    public const string LabelApiVersion = "Penumbra.ApiVersion.V5";
    public const string LabelGetEnabledState = "Penumbra.GetEnabledState";
    public const string LabelGetCollection = "Penumbra.GetCollection";
    public const string LabelAddTemporaryModAll = "Penumbra.AddTemporaryModAll.V5";
    public const string LabelRemoveTemporaryModAll = "Penumbra.RemoveTemporaryModAll.V5";
    public const string LabelResolveDefaultPath = "Penumbra.ResolveDefaultPath";
    public const string LabelInitialized = "Penumbra.Initialized";
    public const string LabelDisposed = "Penumbra.Disposed";

    public const string Tag = "Milieu";
    public const int Priority = 0;
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
