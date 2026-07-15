using Demiurge;
using Stride.Core.Mathematics;

// Client-only weapon shot effects (sound, tracer), keyed by ItemType like
// WeaponConfig. Split from ItemCosmetics so non-weapons don't carry shot
// fields. Unknown types get AK stand-ins — wrong FX beats a crash, and this
// table is fed straight off the wire (PlayerFired broadcasts).
public static class WeaponFx
{
    public readonly record struct Entry(string ShotSoundPath, Color TracerColor);

    public static Entry Get(ItemType type) => type switch
    {
        ItemType.Ak47 => new("assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.AWP => new("assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.Glock => new("assets/sfx/ak47_shot.wav", Color.Yellow),

        _ => new("assets/sfx/ak47_shot.wav", Color.Yellow),
    };
}
