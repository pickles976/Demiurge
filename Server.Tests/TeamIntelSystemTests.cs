using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

/// <summary>
/// The two ways a team learns where the enemy is, and the one way it must not.
///
/// Worth pinning despite being plumbing: the failure mode is a blank minimap, which looks exactly
/// like a team that genuinely knows nothing. Nobody would notice it was broken.
/// </summary>
public class TeamIntelSystemTests
{
    private const int Us = 1;
    private const int Them = 2;

    [Fact]
    public void WhatAnNpcSeesReachesItsTeam()
    {
        var intel = new TeamIntelSystem(new NullNetServer());
        var scout = Mob(60000, Us, new Vector3(0f, 0f, 0f));
        var belief = new ContactMemory();
        belief.Observe(actorId: 900, new Vector3(20f, 0f, 5f), tick: 10);

        intel.Update(10, [scout], _ => belief);

        var contact = Assert.Single(intel.SnapshotFor(Us, 10));
        Assert.Equal(900, contact.ActorId);
        Assert.Equal(new Vector3(20f, 0f, 5f), contact.Position);
        Assert.Equal(255, contact.Confidence);
    }

    /// <summary>A team does not inherit the other side's eyes.</summary>
    [Fact]
    public void OneTeamsSightingIsNotAnothersIntel()
    {
        var intel = new TeamIntelSystem(new NullNetServer());
        var scout = Mob(60000, Us, Vector3.Zero);
        var belief = new ContactMemory();
        belief.Observe(actorId: 900, new Vector3(20f, 0f, 5f), tick: 10);

        intel.Update(10, [scout], _ => belief);

        Assert.Empty(intel.SnapshotFor(Them, 10));
    }

    /// <summary>A dead man reports nothing, and neither does a body that is still on the actor
    /// list waiting for the respawn wave.</summary>
    [Fact]
    public void ADeadScoutContributesNothing()
    {
        var intel = new TeamIntelSystem(new NullNetServer());
        var scout = Mob(60000, Us, Vector3.Zero);
        scout.Status!.Health.Current = 0;
        var belief = new ContactMemory();
        belief.Observe(actorId: 900, new Vector3(20f, 0f, 5f), tick: 10);

        intel.Update(10, [scout], _ => belief);

        Assert.Empty(intel.SnapshotFor(Us, 10));
    }

    /// <summary>
    /// A heard shot locates its firer. Approximately: the error grows with range, so this asserts
    /// the marker lands within what GunshotHearing itself promises rather than on the exact metre.
    /// </summary>
    [Fact]
    public void AHeardShotPutsTheShooterOnTheMap()
    {
        var intel = new TeamIntelSystem(new NullNetServer());
        var shot = new Vector3(30f, 0f, 0f);

        intel.Heard(
            Us,
            shooterId: 900,
            GunshotHearing.PerceivedPosition(Vector3.Zero, shot, seed: 7),
            tick: 10);

        var contact = Assert.Single(intel.SnapshotFor(Us, 10));
        Assert.Equal(900, contact.ActorId);
        Assert.True(
            Vector3.Distance(contact.Position, shot)
                <= GunshotHearing.LocalisationError(30f) + 0.001f,
            $"the fix landed {Vector3.Distance(contact.Position, shot)} m out");
    }

    /// <summary>
    /// Belief fades and then lapses. Without this the map would accumulate every enemy ever seen
    /// and become a list of places somebody once was.
    /// </summary>
    [Fact]
    public void AContactNobodyRefreshesGoesCold()
    {
        var intel = new TeamIntelSystem(new NullNetServer());
        var scout = Mob(60000, Us, Vector3.Zero);
        var belief = new ContactMemory();
        belief.Observe(actorId: 900, new Vector3(20f, 0f, 5f), tick: 0);
        intel.Update(0, [scout], _ => belief);

        // Halfway through the retention window it is still there, and less certain than it was.
        uint half = ContactMemory.SquadRetentionTicks / 2;
        intel.Update(half, [scout], _ => new ContactMemory());
        var fading = Assert.Single(intel.SnapshotFor(Us, half));
        Assert.InRange(fading.Confidence, 1, 254);

        intel.Update(ContactMemory.SquadRetentionTicks + 1, [scout], _ => new ContactMemory());
        Assert.Empty(intel.SnapshotFor(Us, ContactMemory.SquadRetentionTicks + 1));
    }

    private static ServerPlayer Mob(ushort id, int team, Vector3 position)
        => new()
        {
            Id = id,
            IsMob = true,
            Team = team,
            Move = new MoveState { Position = position },
            Status = new ServerObject
            {
                Has = NetComponents.Health,
                Health = new HealthState { Current = 100, Max = 100 },
            },
        };
}
