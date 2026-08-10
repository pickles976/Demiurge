using Demiurge;
using Stride.Core.Mathematics;

// Client-only weapon shot effects (sound, tracer), keyed by ItemType like
// WeaponConfig. Split from ItemCosmetics so non-weapons don't carry shot
// fields. This table is fed straight off the wire (PlayerFired broadcasts), after the
// datapack-hash handshake has guaranteed that both peers resolve the same handles.
public static class WeaponFx
{
    /// <summary>
    /// One working of the bolt: when it starts after the shot, how long it travels back, how long
    /// it stays there, how long it runs forward — and the noise it makes doing it.
    ///
    /// Timing and sound are ONE record because they are one event. The sample is a recording of a
    /// real bolt, so its lift, its pull and its close land at fixed instants inside the file; pacing
    /// the animation off a second set of numbers would let picture and sound drift apart the first
    /// time either was tuned.
    ///
    /// Nothing here is a server fact. What the cycle has to fit inside IS one —
    /// <see cref="WeaponStats.TicksPerShot"/> — and every cycle below is deliberately shorter than
    /// its weapon's, so a rifle is never still working its bolt when it is allowed to fire again.
    /// </summary>
    public readonly record struct BoltCycle(
        float DelaySeconds,
        float TravelSeconds,
        float HoldSeconds,
        float ReturnSeconds,
        string? SoundPath = null)
    {
        /// <summary>The gas system does the work, so there is nothing to hear and nothing to wait
        /// for: back in a snap, forward a little slower — a cycling bolt, not a pendulum. Both
        /// inside one 10 rounds/second interval, so a held burst never starts a cycle on top of the
        /// one before it.</summary>
        public static readonly BoltCycle SelfLoading = new(0f, 0.025f, 0f, 0.055f);

        public float TotalSeconds => DelaySeconds + TravelSeconds + HoldSeconds + ReturnSeconds;
    }

    /// <summary>
    /// <paramref name="ShotSoundPaths"/> is a list because one sample repeated at ten rounds a
    /// second turns into a machine-gun buzz that stops reading as separate shots. Pick one per
    /// shot via <see cref="ShotSound"/>; a single-entry list behaves exactly as before.
    /// </summary>
    /// <summary>
    /// <paramref name="ReloadVolume"/> trims one weapon's reload against the others. It is per
    /// weapon rather than a constant on the reload path because the samples are recordings at
    /// whatever level they were captured at, and evening them out is a property of the recording,
    /// not of what a reload should sound like.
    /// </summary>
    /// <summary>
    /// <paramref name="Bolt"/> is null for the ordinary case — a self-loading action, whose cycle
    /// is <see cref="BoltCycle.SelfLoading"/>. Only a weapon whose bolt is worked by hand needs its
    /// own row. Read it through <see cref="Cycle"/> rather than the field.
    /// </summary>
    public readonly record struct Entry(
        IReadOnlyList<string> ShotSoundPaths,
        Color TracerColor,
        string? ReloadSoundPath = null,
        string? DistantReportSoundPath = null,
        float ReloadVolume = 1f,
        BoltCycle? Bolt = null)
    {
        public Entry(
            string shotSoundPath,
            Color tracerColor,
            string? reloadSoundPath = null,
            string? distantReportSoundPath = null,
            float reloadVolume = 1f,
            BoltCycle? bolt = null)
            : this([shotSoundPath], tracerColor, reloadSoundPath, distantReportSoundPath, reloadVolume, bolt) { }

        public BoltCycle Cycle => Bolt ?? BoltCycle.SelfLoading;
    }

    public static Entry Get(ItemType type)
    {
        var presentation = ItemCatalog.Get(type).Presentation;
        IReadOnlyList<string> shots = presentation.ShotSounds.Count > 0
            ? presentation.ShotSounds
            : ["assets/sfx/rifle_shot_far.wav"];
        BoltCycle? bolt = presentation.Bolt is { } source
            ? new BoltCycle(
                source.DelaySeconds,
                source.TravelSeconds,
                source.HoldSeconds,
                source.ReturnSeconds,
                source.SoundPath)
            : null;
        return new Entry(
            shots,
            ParseColor(presentation.TracerColor),
            presentation.ReloadSound,
            presentation.DistantReportSound,
            presentation.ReloadVolume,
            bolt);
    }

    private static Color ParseColor(string hex)
    {
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        byte a = hex.Length == 9 ? Convert.ToByte(hex.Substring(7, 2), 16) : (byte)255;
        return new Color(r, g, b, a);
    }

    /// <summary>
    /// Past this, a shot is not the crack of a rifle near you — it is a report rolling in from
    /// somewhere else, and it gets its own recording rather than the near sample turned down.
    /// </summary>
    public const float DistantReportMetres = 200f;

    /// <summary>
    /// How loud a far-off report plays. It is a volume rather than a falloff because the sound is
    /// deliberately placed at a fixed short range along the true bearing (see PlayShotReport), well
    /// inside SoundFalloff.DistantReport's reference distance — so distance buys direction here and
    /// nothing else, and attenuation has to be asked for directly.
    /// </summary>
    public const float DistantReportVolume = 0.6f;

    /// <summary>
    /// The far-off report of a rifle-calibre weapon, and the default for anything without its own.
    /// There is one distance tier: a second recording for a longer range was tried and removed,
    /// because a report placed at a fixed 30 m along the true bearing (see PlayShotReport) sounds
    /// the same at 400 m as at 200 m — the extra tier cost a file and changed nothing audible.
    /// </summary>
    public const string RifleReport = "assets/sfx/rifle_shot_far.wav";

    /// <summary>
    /// The recording for a shot heard from <paramref name="metres"/> away, or null to use the
    /// weapon's own near sample.
    /// </summary>
    public static string? DistantReportFor(in Entry entry, float metres)
        => metres < DistantReportMetres ? null
         : entry.DistantReportSoundPath ?? RifleReport;

    /// <summary>One of this weapon's shot samples. Shared Random: this only ever runs on the
    /// main thread, from the shot-effects script.</summary>
    public static string ShotSound(in Entry entry)
    {
        var paths = entry.ShotSoundPaths;
        return paths.Count == 1 ? paths[0] : paths[Random.Shared.Next(paths.Count)];
    }
}
