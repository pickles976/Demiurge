namespace Demiurge.GameServer;

/// <summary>
/// What combat wants the actor to look like this tick, as a VALUE it hands back rather than as
/// writes it performs.
///
/// The distinction is the whole point. <see cref="CombatBehavior"/> used to set mob.Yaw, mob.Pitch,
/// mob.LastIntent and mob.State itself, and it runs before <see cref="MobSystem"/>'s own movement
/// writes — so an NPC's final State depended on which module happened to run last rather than on any
/// decision anybody made. Reporting means there is exactly one writer and the merge is visible in
/// the source.
///
/// Firing is NOT reported: it is an authoritative action on the world, not a description of the
/// actor, so <c>WeaponSystem.TryFireAi</c> still happens inside <see cref="CombatBehavior.Tick"/>.
/// Only its visible consequence — the Shooting flag — rides in <see cref="Flags"/>.
/// </summary>
internal readonly record struct CombatOutcome(
    bool OwnsTick,
    float Yaw,
    float Pitch,
    PlayerStateFlags Flags)
{
    /// <summary>No believed target, or none worth engaging: combat owns nothing this tick.</summary>
    public static CombatOutcome None => new(false, 0f, 0f, PlayerStateFlags.None);
}
