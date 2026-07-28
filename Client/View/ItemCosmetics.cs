using Demiurge;
using Demiurge.GameClient;
using Stride.Core.Mathematics;

// Client-only item cosmetics, keyed by the same ItemType as ItemConfig.
// Kept out of Common so the wire protocol and the server stay view-free.
// Weapon shot effects (sound, tracer) live in WeaponFx.
//
// WHICH BONE a worn item hangs off, and how it is ORIENTED there, comes from the
// SLOT (replicated in AttachmentState): every Hand item points like a gun, every
// Chest item lies like armor. A socket's Node is a bone name (from the rig — dump
// the gltf's node names if unsure) linked via ModelNodeLinkComponent; null follows
// the Player_{id} entity root instead.
//
// HOW FAR ALONG the item sits is the one thing that comes from the ITEM, because
// it depends on where that model's author put its origin. It is read from the
// model's `grip` locator rather than tuned by hand — see SeatFor.
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

    // A new EquipSlot needs a row here — that's the whole client cost of a slot.
    // Seats are the FALLBACK for models with no grip locator; a Hand item's real seat
    // comes from WeaponMount, which owns it so the sim's muzzle can be derived from the
    // same numbers. Head and Back are untuned placeholders: eyeball them when the first
    // helmet/backpack lands.
    private static readonly Dictionary<EquipSlot, Socket> SlotSockets = new()
    {
        [EquipSlot.Hand] = new("right_hand", Vector3.Zero, WeaponMount.HandRotation.ToStride()),
        [EquipSlot.Chest] = new("torso", Vector3.Zero, Quaternion.Identity),
        [EquipSlot.Head] = new("head", Vector3.Zero, Quaternion.Identity),
        [EquipSlot.Back] = new("torso", new Vector3(0f, 0f, -0.2f), Quaternion.Identity),
    };

    // Unknown slot off the wire: ride the player root rather than crash.
    public static Socket GetSocket(EquipSlot slot, ItemType type, WeaponMount mount)
    {
        if (!SlotSockets.TryGetValue(slot, out var socket))
            return new Socket(null, Vector3.Zero, Quaternion.Identity);

        return slot == EquipSlot.Hand
            ? socket with { Seat = mount.Seat(type).ToStride() }
            : socket;
    }
}
