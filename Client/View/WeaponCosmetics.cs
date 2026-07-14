using Demiurge;
using Stride.Core.Mathematics;

// Client-only weapon cosmetics, keyed by the same WeaponType as WeaponConfig.
// Kept out of Common so the wire protocol and the server stay view-free.
public static class WeaponCosmetics
{
    public readonly record struct Entry(string ModelPath, string ShotSoundPath, Color TracerColor);

    public static Entry Get(WeaponType type) => type switch
    {
        WeaponType.Ak47 => new("assets/models/ak47.gltf", "assets/sfx/ak47_shot.wav", Color.Yellow),
        WeaponType.AWP => new("assets/models/sniper_rifle.gltf", "assets/sfx/ak47_shot.wav", Color.Yellow),

        // Unknown type off the wire: AK stand-ins rather than a crash.
        _ => new("assets/models/ak47.gltf", "assets/sfx/ak47_shot.wav", Color.Yellow),
    };
}
