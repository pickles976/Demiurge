using Demiurge;
using Stride.Core.Mathematics;

// Client-only item cosmetics, keyed by the same ItemType as ItemConfig.
// Kept out of Common so the wire protocol and the server stay view-free.
// AttachNode is WHERE on the owner's body an equipped item sits: a bone name
// (from the rig — dump the gltf's node names if unsure) links via
// ModelNodeLinkComponent; null follows the Player_{id} entity root instead.
public static class ItemCosmetics
{
    public readonly record struct Entry(
        string ModelPath,
        string? AttachNode,
        Vector3 Seat,
        Quaternion SeatRotation,
        string ShotSoundPath,
        Color TracerColor);

    // Bone-relative gun seat, carried over from the old WeaponAttachScript.
    private static readonly Vector3 HandSeat = new(0.0f, -3.75f / 16.0f, 0.425f / 16.0f);
    private static readonly Quaternion HandRotation = Quaternion.RotationX(MathF.PI / 2.0f) * Quaternion.RotationZ(MathF.PI);

    public static Entry Get(ItemType type) => type switch
    {
        ItemType.Ak47 => new("assets/models/ak47.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.AWP => new("assets/models/sniper_rifle.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.Glock => new("assets/models/glock.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.BodyArmor => new("assets/models/body_armor.gltf", null, Vector3.Zero, Quaternion.Identity, "assets/sfx/ak47_shot.wav", Color.Yellow),

        // Unknown type off the wire: AK stand-ins rather than a crash.
        _ => new("assets/models/ak47.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
    };
}
