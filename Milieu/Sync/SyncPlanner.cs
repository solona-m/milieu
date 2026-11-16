using System.Collections.Generic;
using System.Linq;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>The effective IMC entries of the target's variants (the user's override, else vanilla) and every VFX id in use.</summary>
public sealed record SyncBase(SyncTarget Target, IReadOnlyList<ImcEntry> Entries, IReadOnlySet<byte> UsedVfxIds, bool MetaUnreadable = false);

/// <summary>
/// What one synced registration hands Penumbra: redirects, the IMC manipulation string, the chosen VFX id, and the
/// per-variant entries Milieu registered (variant i + 1 at index i), used to recognise Milieu's own override later.
/// </summary>
public sealed record SyncPlan(IReadOnlyDictionary<string, string> Paths, string Manips, byte VfxId, string AvfxGamePath,
                              IReadOnlyList<ImcEntry> Entries);

/// <summary>Pure planning for synced delivery: no IPC, no files.</summary>
public static class SyncPlanner
{
    /// <summary>
    /// Reads the vanilla IMC for the target's part and applies the user's IMC overrides from the player meta. An override at
    /// variant i is not taken for a user override only when it equals <paramref name="ownEntries"/>[i], the entry Milieu
    /// itself registered earlier this session (Penumbra may still report it after a removal). Any other override, even
    /// one using Milieu's last VFX id, is the user's: its id is avoided. A foreign override identical in every field can't
    /// be told apart; that is harmless, as Milieu's priority mod wins the IMC entry and the path.
    /// </summary>
    public static (SyncBase? Base, MessageKey Failure) ResolveBase(SyncTarget target, byte[]? vanillaImc, string? playerMeta,
                                                                   IReadOnlyList<ImcEntry>? ownEntries = null)
    {
        if (!ImcFile.TryRead(vanillaImc, target.PartIndex, out var vanilla)) return (null, MessageKey.SyncGameDataUnreadable);
        var metaReadable = PenumbraMeta.TryReadImcOverrides(playerMeta, out var overrides);
        var used = new HashSet<byte>();
        var entries = new List<ImcEntry>(vanilla.Count);
        for (var i = 0; i < vanilla.Count; i++)
        {
            var v = vanilla[i];
            used.Add(v.VfxId);
            var entry = v;
            var own = ownEntries != null && i < ownEntries.Count && overrides.TryGetValue(target.KeyOf((byte)(i + 1)), out var mine)
                      && mine == ownEntries[i];
            if (!own && overrides.TryGetValue(target.KeyOf((byte)(i + 1)), out var o))
            {
                entry = o;
                used.Add(o.VfxId);
            }
            entries.Add(entry);
        }
        return (new SyncBase(target, entries, used, !metaReadable), MessageKey.None);
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
        return (new SyncPlan(paths, PenumbraMeta.EncodeImcV0(b.Target, entries), n, avfxGame, entries.Select(x => x.Item2).ToList()),
                MessageKey.None);
    }
}
