using Demiurge;
using Stride.Core.Mathematics;

// Client-only weapon shot effects (sound, tracer), keyed by ItemType like
// WeaponConfig. Split from ItemCosmetics so non-weapons don't carry shot
// fields. Unknown types get AK stand-ins — wrong FX beats a crash, and this
// table is fed straight off the wire (PlayerFired broadcasts).
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
        ItemType.Ppsh => new(
            [
                "assets/sfx/ppsh_shot_1.wav",
                "assets/sfx/ppsh_shot_2.wav",
            ],
            Color.Yellow,
            ReloadSoundPath: "assets/sfx/ppsh_reload.wav",
            DistantReportSoundPath: "assets/sfx/ppsh_report_far_off_200m.wav",
            ReloadVolume: 0.6f),
        // The cycle is timed against the recording rather than eyeballed: the sample's own lift and
        // pull land 0.2-0.4 s in, its close lands at 0.9 s and its lock at 1.3 s, so the bolt starts
        // moving a beat after the shot, is back while the pull is audible, and runs forward through
        // the closing thump.
        //
        // The two MOVING legs are the hand's speed and are tuned as a pair — a stroke that takes
        // longer to draw than to run home reads as a stuck bolt. Delay and hold are the sync
        // surface against the recording and are tuned separately; the delay in particular is the
        // gap between the shot and the hand reaching the handle, not part of the working.
        //
        // Whatever the four come to has to stay under the 1.5 s cadence, or the rifle fires
        // mid-cycle: this is the one weapon whose animation nearly fills its own shot interval.
        ItemType.Mosin => new(
            [
                "assets/sfx/mosin_shot_1.wav",
                "assets/sfx/mosin_shot_2.wav",
            ],
            Color.Yellow,
            ReloadSoundPath: "assets/sfx/mosin_reload.wav",
            Bolt: new BoltCycle(
                DelaySeconds: 0.15f,
                TravelSeconds: 0.286f,
                HoldSeconds: 0.65f,
                ReturnSeconds: 0.286f,
                SoundPath: "assets/sfx/mosin_bolt.wav")),
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

    /// <summary>
    /// How loud a far-off report plays. It is a volume rather than a falloff because the sound is
    /// deliberately placed at a fixed short range along the true bearing (see PlayShotReport), well
    /// inside SoundFalloff.DistantReport's reference distance — so distance buys direction here and
    /// nothing else, and attenuation has to be asked for directly.
    /// </summary>
    public const float DistantReportVolume = 0.6f;

    private const string DistantReport = "assets/sfx/far_off_rifle_report_200m.wav";
    private const string VeryDistantReport = "assets/sfx/far_off_rifle_report_400m.wav";

    /// <summary>
    /// The recording for a shot heard from <paramref name="metres"/> away, or null to use the
    /// weapon's own near sample.
    /// </summary>
    public static string? DistantReportFor(in Entry entry, float metres)
        => metres >= DistantReportMetres && entry.DistantReportSoundPath is { } weaponReport
            ? weaponReport
         : metres >= VeryDistantReportMetres ? VeryDistantReport
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
