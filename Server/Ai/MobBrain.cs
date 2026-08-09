using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>Per-NPC state which is not part of the replicated player representation.</summary>
internal sealed class MobBrain
{
    internal const int IncomingFireResponseTicks = 2 * NetworkConfig.TickRate;

    public int Team { get; init; }

    /// <summary>
    /// Settable, unlike before: squads re-form from live proximity every second, so an NPC can change
    /// squad mid-fight. See <see cref="SquadFormation"/>.
    /// </summary>
    public int SquadIndex { get; set; }

    /// <summary>
    /// How many bounds this member has completed. Each one shortens its standoff, which is what makes
    /// the squad close in rather than shuffle at a fixed range.
    /// </summary>
    public int BoundIndex { get; set; }

    /// <summary>
    /// Tick this actor's current bound began, or 0 when it is not moving.
    ///
    /// <see cref="SquadTactics"/> uses it to stop waiting for a mover that cannot arrive. Bounding is
    /// self-clocking — the next man goes when the last one gets there — and self-clocking deadlocks
    /// the moment somebody is pinned, blocked, or sent somewhere unreachable.
    /// </summary>
    public uint MovingSinceTick { get; set; }

    /// <summary>
    /// What this actor chose to do on its last step, as a label for the debug overlay. Written where
    /// the decision is made and read nowhere else, so it can never become an input: an intent that
    /// something downstream could branch on would be a second copy of a decision <see
    /// cref="ActorIntent"/> exists to keep singular. See <see cref="MobDebugFeed"/>.
    /// </summary>
    public string DebugIntent { get; set; } = "SPAWN";

    /// <summary>
    /// Bearing around the threat this actor has committed to for its current bound.
    ///
    /// A Commitment rather than a bare float because the 2 Hz replan would otherwise re-deal it every
    /// pass and swap a mover to the far side of the threat mid-manoeuvre — measured, that turned 37
    /// bounds into 13 m of displacement per man. The expiry also means a bearing cannot outlive the
    /// bound it belongs to if something forgets to clear it.
    /// </summary>
    public Commitment<float> BoundBearing { get; set; } = Commitment<float>.None;

    /// <summary>
    /// Fraction of the believed TARGET's silhouette perception last had a line to. Feeds the target
    /// radius in <see cref="WeaponEffectiveness"/>, so a target in cover is genuinely harder to hit
    /// rather than merely harder to see.
    ///
    /// This is how exposed the ENEMY is. It is not, and must not be confused with, how exposed this
    /// actor is — see <see cref="SelfExposure"/>. Feeding one into the other tells a squad it is
    /// protected whenever its target is, which makes holding look free and manoeuvre look suicidal.
    /// </summary>
    public TargetExposure PerceivedExposure { get; set; } = TargetExposure.Full;

    /// <summary>
    /// How much of THIS actor a shooter can reach where it currently stands, 0..1.
    ///
    /// Derived from what the actor has actually done about cover rather than from a raycast, because
    /// a per-actor self-exposure ray would cost one more ray per NPC per tick and the states that
    /// matter are already tracked: a man in a finished fighting position is hard to hit, a man at a
    /// cover position is harder than one in the open, and everyone else is a standing target.
    /// </summary>
    public SelfExposure SelfExposure => SelfExposure.Of(
        Entrenched ? EntrenchedSelfExposure.Fraction
        : AtCover ? CoveredExposure
        : 1f);

    /// <summary>Dug in below grade with a parapet in front: only the head and shoulders needed to
    /// shoot over it are available.</summary>
    public static readonly SelfExposure EntrenchedSelfExposure = SelfExposure.Of(0.15f);

    /// <summary>At a cover position but not dug in — using terrain that was already there.</summary>
    private const float CoveredExposure = 0.35f;

    /// <summary>
    /// Scales this actor's sighting error. 1 is a competent soldier, above 1 is worse. Execution
    /// only — scoring always uses the nominal value, so a poor shot does not correctly reason about
    /// being a poor shot.
    /// </summary>
    public float SkillFactor { get; set; } = 1f;

    /// <summary>
    /// Next tick this actor may fire, derived from the chosen firing solution's rate rather than from
    /// a per-weapon burst schedule.
    /// </summary>
    public uint NextShotTick { get; set; }

    /// <summary>
    /// Gunfire this actor remembers, ranked by salience rather than recency. Replaces a single
    /// last-write-wins slot in which a distant shot erased a point-blank one.
    /// </summary>
    public HeardShots Heard { get; } = new();
    public uint ObjectiveRevision { get; set; }
    /// <summary>True only after this actor's own path reaches its formation slot. Proximity to the
    /// shared flag is insufficient: a relocated flank member can respawn inside the capture radius
    /// while still being many metres from its assigned slot.</summary>
    public bool ObjectiveReached { get; set; }
    public NavigationAgent Navigation { get; } = new();
    public ContactMemory Contacts { get; } = new();
    public int PerceptionCursor { get; set; }

    /// <summary>
    /// Which body point perception last had a clear line to, and on whom. Combat aims here instead of
    /// assuming centre mass, so a target exposing only its head over cover is actually shot at rather
    /// than being fired into the dirt in front of it.
    /// </summary>
    public ushort PerceivedTargetId { get; set; }
    public float PerceivedAimHeight { get; set; } = GunConfig.PlayerCenterHeight;
    public ushort CombatTargetId { get; set; }
    public uint TargetAcquiredTick { get; set; }
    public Vector3 AimDirection { get; set; }
    public uint ShotSequence { get; set; }
    public int BurstShotsRemaining { get; set; }
    public uint NextBurstTick { get; set; }
    public uint NextPrecisionShotTick { get; set; }
    public uint NextSuppressionShotTick { get; set; }
    public uint NextGrenadeDecisionTick { get; set; }
    public bool HasCoverDestination { get; set; }
    public bool AtCover { get; set; }
    public Vector3 CoverDestination { get; set; }
    public Vector3 CoverPeekPosition { get; set; }
    public CoverKind CoverKind { get; set; }
    public ushort CoverThreatId { get; set; }
    public Vector3 CoverThreatPosition { get; set; }
    public long CoverTerrainVersion { get; set; }
    public ChunkIndex[] CoverDependencyChunks { get; set; } = [];
    public uint NextCoverQueryTick { get; set; }
    public uint CoverArrivedTick { get; set; }
    public bool Entrenching { get; private set; }
    public bool Entrenched { get; set; }
    /// <summary>Persists after leaving the first fighting position so an assault unit does not
    /// mistake every subsequent bound for its initial entrenchment requirement.</summary>
    public bool HasCompletedInitialEntrenchment { get; private set; }
    public Vector3 EntrenchOrigin { get; private set; }
    public Vector3 EntrenchToward { get; private set; }
    public float EntrenchGrade { get; private set; }
    public ushort HeardActorId { get; set; }
    public Vector3 HeardPosition { get; set; }
    public uint HeardTick { get; set; }
    public uint HeardRevision { get; set; }
    public uint AppliedHeardRevision { get; set; }
    public bool ShouldCloseDistance { get; set; }
    public bool AssaultDashActive { get; set; }
    public uint UnderFireUntilTick { get; private set; }

    public void MarkUnderFire(uint tick)
        => UnderFireUntilTick = Math.Max(
            UnderFireUntilTick,
            tick + IncomingFireResponseTicks);

    public bool IsUnderFire(uint tick)
        => tick < UnderFireUntilTick;

    public void ClearUnderFire() => UnderFireUntilTick = 0;

    public void ClearCombatTarget()
    {
        CombatTargetId = 0;
        TargetAcquiredTick = 0;
        BurstShotsRemaining = 0;
        NextPrecisionShotTick = 0;
        ShouldCloseDistance = false;
    }

    public void ClearCover()
    {
        HasCoverDestination = false;
        AtCover = false;
        CoverDestination = default;
        CoverPeekPosition = default;
        CoverKind = CoverKind.None;
        CoverThreatId = 0;
        CoverThreatPosition = default;
        CoverTerrainVersion = 0;
        CoverDependencyChunks = [];
        CoverArrivedTick = 0;
        ClearEntrenchment();
        Navigation.Path.Clear();
    }

    public void BeginEntrenchment(Vector3 origin, Vector3 toward, float grade)
    {
        Entrenching = true;
        Entrenched = false;
        EntrenchOrigin = origin;
        EntrenchToward = toward;
        EntrenchGrade = grade;
    }

    public void CompleteEntrenchment()
    {
        Entrenching = false;
        Entrenched = true;
        HasCompletedInitialEntrenchment = true;
    }

    public void ResetEntrenchmentHistory()
        => HasCompletedInitialEntrenchment = false;

    public void ClearEntrenchment()
    {
        Entrenching = false;
        Entrenched = false;
        EntrenchOrigin = default;
        EntrenchToward = default;
        EntrenchGrade = 0f;
    }

    public bool HasRecentGunshot(uint tick)
        => HeardActorId != 0
           && tick >= HeardTick
           && tick - HeardTick < GunshotHearing.InvestigationTicks;

    public void ClearGunshot()
    {
        HeardActorId = 0;
        HeardPosition = default;
        HeardTick = 0;
        HeardRevision = 0;
        AppliedHeardRevision = 0;
    }
}
