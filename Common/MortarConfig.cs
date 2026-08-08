namespace Demiurge;

/// <summary>
/// What a mortar is, as numbers. In Common because both ends need every one of them: the client
/// draws the fire sector and predicts the arc, the server decides what is legal and what the round
/// does, and a constant either of them owned alone would be a constant they could disagree about.
///
/// A mortar is deliberately not a gun with different statistics. It is emplaced rather than held,
/// it is aimed at a POINT rather than along a direction, and it reaches that point over an arc
/// rather than a line. The numbers here are the ones that make those three things true; the ones a
/// gun shares with it — magazine, reload, carry weight — stay in <see cref="WeaponConfig"/> where
/// every weapon's live.
/// </summary>
public static class MortarConfig
{
    /// <summary>
    /// How much of your speed carrying one costs. Half, which is the point of the thing: a mortar
    /// is a weapon you commit to a position, and the commitment is paid for in the walk there.
    /// </summary>
    public const float CarryMoveSpeedScale = 0.5f;

    /// <summary>Seconds to load the next bomb. The gunner is idle and exposed for all of it.</summary>
    public const float ReloadSeconds = 5f;

    /// <summary>
    /// How far off the emplaced facing the tube will traverse, in degrees each way. A mortar is
    /// laid on a line and then adjusted; re-laying it means picking it up and putting it down
    /// facing somewhere else, which is the cost of choosing the wrong line.
    /// </summary>
    public const float SectorHalfAngleDegrees = 30f;

    /// <summary>
    /// The reachable band. The minimum is the interesting half: a mortar cannot defend itself,
    /// because anything closer than this cannot be dropped on.
    /// </summary>
    public const float MinimumRange = 50f;
    public const float MaximumRange = 200f;

    /// <summary>
    /// A representative muzzle velocity, for code that asks a weapon how fast its projectile leaves
    /// without knowing this one solves for that per shot. Nothing in the firing path uses it: the
    /// arc is solved from the range asked for.
    /// </summary>
    public const float NominalSpeed = 60f;

    /// <summary>
    /// How much bigger the burst is than a grenade's, applied to the radii rather than to the
    /// damage — so a mortar bomb kills over a wider area instead of hitting harder in one spot.
    /// The damage inside that area already scales with a victim's maximum health through
    /// <see cref="GrenadeConfig.DamageFraction"/>, and the crater scales with it too.
    /// </summary>
    public const float BlastScale = 1.5f;

    /// <summary>The grenade's blast, one and a half times over — radii and crater together.</summary>
    public static BlastProfile Blast => GrenadeConfig.Blast.Scaled(BlastScale);

    public static float LethalRadius => Blast.LethalRadius;
    public static float DamageRadius => Blast.DamageRadius;

    /// <summary>Runaway guard: a bomb still flying after this is discarded rather than tracked
    /// forever. Generous against the longest arc — 200 m at 70 degrees is about nine seconds.</summary>
    public const float MaxFlightSeconds = 20f;

    /// <summary>
    /// How far off a bomb lands, in metres: the standard deviation of a gaussian scatter, and the
    /// radius of the circle the gunner is shown. A mortar is an area weapon, and this is what makes
    /// it one.
    /// </summary>
    public const float DispersionMetres = 5f;

    /// <summary>Whether a bearing, in radians relative to the emplaced facing, is inside the
    /// sector the tube can traverse.</summary>
    public static bool WithinSector(float relativeBearingRadians)
        => MathF.Abs(relativeBearingRadians) <= SectorHalfAngleDegrees * MathF.PI / 180f;

    /// <summary>Whether a range in metres is one the tube can reach.</summary>
    public static bool WithinRange(float metres)
        => metres >= MinimumRange && metres <= MaximumRange;
}
