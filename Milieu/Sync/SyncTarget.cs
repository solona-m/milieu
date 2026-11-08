using System.Collections.Generic;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>
/// Who sees the backdrop. <see cref="Local"/> is today's static VFX that only the user sees; the other two hand the dome
/// to an item's own VFX so PSync pairs download and see it too (experimental; docs/adr/0006).
/// </summary>
public enum DeliveryMode { Local = 0, Cushion = 1, MadBirdRing = 2 }

/// <summary>
/// An item that can carry the dome: where its IMC lives, which part and identifier Penumbra uses for it, and the VFX
/// path the game asks for once its IMC entry has a VFX id. Enum numbers follow Penumbra.GameData (ObjectType, EquipSlot,
/// BodySlot), because Penumbra's v1 meta string stores them as raw bytes.
/// </summary>
public sealed record SyncTarget(
    DeliveryMode Mode,
    string DisplayName,
    string ImcPath,
    int PartIndex,
    string ObjectType,
    byte ObjectTypeId,
    ushort PrimaryId,
    ushort SecondaryId,
    string? EquipSlot,
    byte EquipSlotId,
    string? BodySlot,
    byte BodySlotId,
    string VfxPathFormat,
    MessageKey ShownMessage)
{
    public const byte ObjectTypeAccessory = 3;
    public const byte ObjectTypeMonster = 6;
    public const byte EquipSlotRFinger = 12;
    public const byte BodySlotBody = 4;

    /// <summary>First VFX id Milieu tries; ids below are left to the game and to mods.</summary>
    public const byte FirstVfxId = 200;
    public const byte LastVfxId = 254;

    /// <summary>The Plush Cushion ornament (monster m8058, body b0001): its body VFX via the monster IMC.</summary>
    public static readonly SyncTarget Cushion = new(
        DeliveryMode.Cushion, "Plush Cushion",
        "chara/monster/m8058/obj/body/b0001/b0001.imc", 0,
        "Monster", ObjectTypeMonster, 8058, 1, null, 0, "Body", BodySlotBody,
        "chara/monster/m8058/obj/body/b0001/vfx/eff/vm{0:D4}.avfx", MessageKey.SyncShownCushion);

    /// <summary>
    /// The Mad Bird Ring on the right hand (accessory a0028). Accessory VFX are a Penumbra feature: its resolve hook asks for
    /// <c>va{VfxId:D4}.avfx</c> (Penumbra/Interop/Hooks/Resources/ResolvePathHooksBase.cs), the game alone never loads them.
    /// </summary>
    public static readonly SyncTarget MadBirdRing = new(
        DeliveryMode.MadBirdRing, "Mad Bird Ring",
        "chara/accessory/a0028/a0028.imc", 3,
        "Accessory", ObjectTypeAccessory, 28, 0, "RFinger", EquipSlotRFinger, null, 0,
        "chara/accessory/a0028/vfx/eff/va{0:D4}.avfx", MessageKey.SyncShownRing);

    public static SyncTarget? For(DeliveryMode mode) => mode switch
    {
        DeliveryMode.Cushion => Cushion,
        DeliveryMode.MadBirdRing => MadBirdRing,
        _ => null,
    };

    public string VfxPath(byte vfxId) => string.Format(System.Globalization.CultureInfo.InvariantCulture, VfxPathFormat, vfxId);

    /// <summary>The Penumbra identifier of one variant of this target's part.</summary>
    public ImcKey KeyOf(byte variant) => new(PrimaryId, variant, ObjectTypeId, SecondaryId, EquipSlotId, BodySlotId);

    /// <summary>The first id from 200 to 254 that nothing uses, or null when all are taken.</summary>
    public static byte? ChooseVfxId(IEnumerable<byte> used)
    {
        var taken = new HashSet<byte>(used);
        for (var id = FirstVfxId; id <= LastVfxId; id++)
            if (!taken.Contains(id)) return id;
        return null;
    }
}

/// <summary>Penumbra's ImcIdentifier, in its binary field order (PrimaryId, Variant, ObjectType, SecondaryId, EquipSlot, BodySlot).</summary>
public readonly record struct ImcKey(ushort PrimaryId, byte Variant, byte ObjectType, ushort SecondaryId, byte EquipSlot, byte BodySlot);
