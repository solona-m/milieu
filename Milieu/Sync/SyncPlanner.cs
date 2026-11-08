using System.Collections.Generic;
using System.Linq;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>The effective IMC entries of the target's variants (the user's override, else vanilla) and every VFX id in use.</summary>
public sealed record SyncBase(SyncTarget Target, IReadOnlyList<ImcEntry> Entries, IReadOnlySet<byte> UsedVfxIds);

/// <summary>What one synced registration hands Penumbra: redirects, the IMC manipulation string, and the chosen VFX id.</summary>
public sealed record SyncPlan(IReadOnlyDictionary<string, string> Paths, string Manips, byte VfxId, string AvfxGamePath);

/// <summary>Pure planning for synced delivery: no IPC, no files.</summary>
public static class SyncPlanner
{
    /// <summary>
    /// Reads the vanilla IMC for the target's part and applies the user's IMC overrides from the player meta. An override
    /// whose VfxId equals <paramref name="ignoreVfxId"/> (Milieu's own entry from earlier this session) is not taken for a
    /// user override.
    /// </summary>
    public static (SyncBase? Base, MessageKey Failure) ResolveBase(SyncTarget target, byte[]? vanillaImc, string? playerMeta, byte? ignoreVfxId = null)
    {
        if (!ImcFile.TryRead(vanillaImc, target.PartIndex, out var vanilla)) return (null, MessageKey.SyncGameDataUnreadable);
        var overrides = PenumbraMeta.ReadImcOverrides(playerMeta);
        var used = new HashSet<byte>();
        var entries = new List<ImcEntry>(vanilla.Count);
        for (var i = 0; i < vanilla.Count; i++)
        {
            var v = vanilla[i];
            used.Add(v.VfxId);
            var entry = v;
            if (overrides.TryGetValue(target.KeyOf((byte)(i + 1)), out var o) && (ignoreVfxId == null || o.VfxId != ignoreVfxId))
            {
                entry = o;
                used.Add(o.VfxId);
            }
            entries.Add(entry);
        }
        return (new SyncBase(target, entries, used), MessageKey.None);
    }

    /// <summary>Chooses the VFX id, sets it on every variant and builds the manipulation string and the redirects.</summary>
    public static (SyncPlan? Plan, MessageKey Failure) Build(SyncBase b, string atexGamePath, string atexDisk, string avfxDisk)
    {
        if (SyncTarget.ChooseVfxId(b.UsedVfxIds) is not { } n) return (null, MessageKey.SyncNoFreeVfxId);
        var entries = b.Entries.Select((e, i) => ((byte)(i + 1), e.WithVfx(n))).ToList();
        var avfxGame = b.Target.VfxPath(n);
        var paths = new Dictionary<string, string>
        {
            [avfxGame] = avfxDisk,
            [atexGamePath] = atexDisk,
        };
        return (new SyncPlan(paths, PenumbraMeta.EncodeImcV0(b.Target, entries), n, avfxGame), MessageKey.None);
    }
}
