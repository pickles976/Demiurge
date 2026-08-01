using Demiurge;
using Stride.Core.Mathematics;

// Client-only weapon shot effects (sound, tracer), keyed by ItemType like
// WeaponConfig. Split from ItemCosmetics so non-weapons don't carry shot
// fields. Unknown types get AK stand-ins — wrong FX beats a crash, and this
// table is fed straight off the wire (PlayerFired broadcasts).
public static class WeaponFx
{
    /// <summary>
    /// <paramref name="ShotSoundPaths"/> is a list because one sample repeated at ten rounds a
    /// second turns into a machine-gun buzz that stops reading as separate shots. Pick one per
    /// shot via <see cref="ShotSound"/>; a single-entry list behaves exactly as before.
    /// </summary>
    public readonly record struct Entry(IReadOnlyList<string> ShotSoundPaths, Color TracerColor)
    {
        public Entry(string shotSoundPath, Color tracerColor)
            : this([shotSoundPath], tracerColor) { }
    }

    public static Entry Get(ItemType type) => type switch
    {
        ItemType.Ak47 => new("assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.Sks => new(
            [
                "assets/sfx/sks_shot_1.wav",
                "assets/sfx/sks_shot_2.wav",
                "assets/sfx/sks_shot_3.wav",
            ],
            Color.Yellow),
        ItemType.AWP => new("assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.Glock => new("assets/sfx/ak47_shot.wav", Color.Yellow),

        _ => new("assets/sfx/ak47_shot.wav", Color.Yellow),
    };

    /// <summary>One of this weapon's shot samples. Shared Random: this only ever runs on the
    /// main thread, from the shot-effects script.</summary>
    public static string ShotSound(in Entry entry)
    {
        var paths = entry.ShotSoundPaths;
        return paths.Count == 1 ? paths[0] : paths[Random.Shared.Next(paths.Count)];
    }
}
