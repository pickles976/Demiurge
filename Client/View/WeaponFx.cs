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
    public readonly record struct Entry(
        IReadOnlyList<string> ShotSoundPaths,
        Color TracerColor,
        string? ReloadSoundPath = null)
    {
        public Entry(string shotSoundPath, Color tracerColor, string? reloadSoundPath = null)
            : this([shotSoundPath], tracerColor, reloadSoundPath) { }
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
            Color.Yellow,
            ReloadSoundPath: "assets/sfx/sks_reload.wav"),
        ItemType.AWP => new("assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.Glock => new("assets/sfx/ak47_shot.wav", Color.Yellow),

        _ => new("assets/sfx/ak47_shot.wav", Color.Yellow),
    };

    /// <summary>
    /// Past this, a shot is not the crack of a rifle near you — it is a report rolling in from
    /// somewhere else, and it gets its own recording rather than the near sample turned down.
    /// </summary>
    public const float DistantReportMetres = 200f;
    public const float VeryDistantReportMetres = 400f;

    private const string DistantReport = "assets/sfx/far_off_rifle_report_200m.wav";
    private const string VeryDistantReport = "assets/sfx/far_off_rifle_report_400m.wav";

    /// <summary>
    /// The recording for a shot heard from <paramref name="metres"/> away, or null to use the
    /// weapon's own near sample.
    /// </summary>
    public static string? DistantReportFor(float metres)
        => metres >= VeryDistantReportMetres ? VeryDistantReport
         : metres >= DistantReportMetres ? DistantReport
         : null;

    /// <summary>One of this weapon's shot samples. Shared Random: this only ever runs on the
    /// main thread, from the shot-effects script.</summary>
    public static string ShotSound(in Entry entry)
    {
        var paths = entry.ShotSoundPaths;
        return paths.Count == 1 ? paths[0] : paths[Random.Shared.Next(paths.Count)];
    }
}
