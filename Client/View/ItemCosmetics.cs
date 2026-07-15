using Demiurge;
using Stride.Core.Mathematics;

// Client-only item cosmetics, keyed by the same ItemType as ItemConfig.
// Kept out of Common so the wire protocol and the server stay view-free.
// Weapon shot effects (sound, tracer) live in WeaponFx.
//
// WHERE a worn item sits comes from the SLOT (replicated in AttachmentState),
// not the item: every Hand item seats like a gun, every Chest item like armor.
// A socket's Node is a bone name (from the rig — dump the gltf's node names if
// unsure) linked via ModelNodeLinkComponent; null follows the Player_{id}
// entity root instead.
public static class ItemCosmetics
{
    public readonly record struct Socket(string? Node, Vector3 Seat, Quaternion Rotation);

    public static string Model(ItemType type) => type switch
    {
        ItemType.Ak47 => "assets/models/ak47.gltf",
        ItemType.AWP => "assets/models/sniper_rifle.gltf",
        ItemType.Glock => "assets/models/glock.gltf",
        ItemType.BodyArmor => "assets/models/body_armor.gltf",

        // Unknown type off the wire: AK stand-in rather than a crash.
        _ => "assets/models/ak47.gltf",
    };

    // Bone-relative gun seat, carried over from the old WeaponAttachScript.
    private static readonly Vector3 HandSeat = new(0.0f, -3.75f / 16.0f, 0.425f / 16.0f);
    private static readonly Quaternion HandRotation = Quaternion.RotationX(MathF.PI / 2.0f) * Quaternion.RotationZ(MathF.PI);

    // A new EquipSlot needs a row here — that's the whole client cost of a slot.
    // Head and Back are untuned placeholders: eyeball them when the first
    // helmet/backpack lands.
    private static readonly Dictionary<EquipSlot, Socket> SlotSockets = new()
    {
        [EquipSlot.Hand] = new("right_hand", HandSeat, HandRotation),
        [EquipSlot.Chest] = new("torso", Vector3.Zero, Quaternion.Identity),
        [EquipSlot.Head] = new("head", Vector3.Zero, Quaternion.Identity),
        [EquipSlot.Back] = new("torso", new Vector3(0f, 0f, -0.2f), Quaternion.Identity),
    };

    // Unknown slot off the wire: ride the player root rather than crash.
    public static Socket GetSocket(EquipSlot slot) =>
        SlotSockets.TryGetValue(slot, out var socket) ? socket : new Socket(null, Vector3.Zero, Quaternion.Identity);
}
