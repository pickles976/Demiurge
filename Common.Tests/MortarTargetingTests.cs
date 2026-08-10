using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the fire-mission model. Each is a statement about what a mortar is FOR that must
/// survive any solver written under it — not a trace through this one. There is deliberately no test
/// that a particular point is chosen: the point is an output of the sum, and pinning it would pin
/// the sum.
/// </summary>
public class MortarTargetingTests
{
    private static readonly Vector3 Tube = Vector3.Zero;
    private const float TubeYaw = 0f;              // laid down +Z
    private const float Flight = 6f;               // representative, in the middle of the band
    private static readonly Vector3[] NoFriendlies = [];

    private static MortarTarget Standing(float x, float z, float value = 1f)
        => new(new Vector3(x, 0f, z), Vector3.Zero, value);

    private static MortarTarget Running(float x, float z, float speed = 4f)
        => new(new Vector3(x, 0f, z), new Vector3(speed, 0f, 0f));

    /// <summary>
    /// Three men together are three times the target one man is, and nothing in the solver knows what
    /// a cluster is. This is the whole reason the mission scores places rather than picking a man.
    /// </summary>
    [Fact]
    public void AClusterOutbidsALoneManEvenWhenTheLoneManIsCloser()
    {
        MortarTarget[] enemies =
        [
            Standing(0f, 70f),                     // alone, and nearer the tube
            Standing(40f, 120f),
            Standing(44f, 122f),
            Standing(42f, 126f),
        ];

        Assert.True(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, enemies, NoFriendlies, out var aim, out _));

        Assert.True(
            Vector3.Distance(aim, new Vector3(42f, 0f, 123f)) < 12f,
            $"the round should be laid on the group, not on the single man; aimed at {aim}");
    }

    /// <summary>
    /// A dug-in man is a PREDICTABLE man, and that is the only property a mortar cares about. Nothing
    /// asks whether he is in a hole — a man who is not going anywhere in the ten seconds the round is
    /// in the air is the same target whether he is entrenched, pinned, or serving a gun.
    /// </summary>
    [Fact]
    public void AStationaryManIsWorthMoreThanARunningOneAtTheSameRange()
    {
        MortarTarget[] enemies =
        [
            Running(-40f, 100f), Running(-44f, 103f), Running(-36f, 103f),
            Standing(40f, 100f), Standing(44f, 103f), Standing(36f, 103f),
        ];

        Assert.True(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, enemies, NoFriendlies, out var aim, out _));

        Assert.True(aim.X > 0f, $"the round belongs on the men who will still be there; aimed at {aim}");
    }

    /// <summary>
    /// And the same property one layer down, where it is a statement about the weapon rather than
    /// about this solver: a lone runner is not worth a round at all, while a lone man standing still
    /// is. That gap is the whole reason a mortar is worth pointing at entrenched positions.
    /// </summary>
    [Fact]
    public void ALoneRunnerIsNotWorthARoundAndALoneStandingManIs()
    {
        Assert.True(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, [Standing(0f, 100f)], NoFriendlies, out _, out _));
        Assert.False(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, [Running(0f, 100f)], NoFriendlies, out _, out _));
    }

    /// <summary>Counter-battery, and it is a weight rather than a mode: an enemy tube is worth more
    /// than a rifleman standing next to it, so the mission goes to the tube.</summary>
    [Fact]
    public void ACrewServedWeaponOutbidsARiflemanAtEqualRange()
    {
        MortarTarget[] enemies =
        [
            Standing(-40f, 100f),
            Standing(40f, 100f, MortarTargeting.CrewServedWeaponValue),
        ];

        Assert.True(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, enemies, NoFriendlies, out var aim, out _));

        Assert.True(aim.X > 0f, $"the mission should go to the enemy tube; aimed at {aim}");
    }

    /// <summary>
    /// Our own casualties are subtracted in the same tickets, so a target with a friendly standing on
    /// it loses to an identical one without. Priced, not vetoed — which is what lets the solver take
    /// a good mission close to our own line instead of refusing every one of them.
    /// </summary>
    [Fact]
    public void FriendlyCasualtiesCostTheMissionRatherThanCancellingIt()
    {
        MortarTarget[] enemies = [Standing(-40f, 100f), Standing(40f, 100f)];
        Vector3[] friendlies = [new(-40f, 0f, 100f)];

        Assert.True(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, enemies, friendlies, out var aim, out _));

        Assert.True(aim.X > 0f, $"the mission should avoid our own man; aimed at {aim}");
    }

    /// <summary>Danger close is a judgement the sum is allowed to make: three of theirs outweigh a
    /// risk to one of ours, and a model that cannot say so is the twenty-metre veto this replaced.</summary>
    [Fact]
    public void AGoodMissionSurvivesAFriendlyNearTheEdgeOfTheBurst()
    {
        MortarTarget[] enemies = [Standing(0f, 100f), Standing(4f, 103f), Standing(-4f, 103f)];
        Vector3[] friendlies = [new(0f, 0f, 88f)];

        Assert.True(
            MortarTargeting.TrySolve(
                Tube, TubeYaw, Flight, enemies, friendlies, out _, out float value),
            "three men together must still be worth a round with one of ours twelve metres off");
        Assert.True(value > MortarTargeting.MinimumMissionValue);
    }

    /// <summary>The tube's own sector and range band bound every answer. A mission it cannot fire is
    /// not a mission, however good the ground looks.</summary>
    [Theory]
    [InlineData(0f, 20f)]          // inside the minimum range
    [InlineData(0f, 400f)]         // past the maximum
    [InlineData(150f, 20f)]        // behind the sector
    [InlineData(0f, -100f)]        // behind the tube
    public void UnreachableGroundIsNeverChosen(float x, float z)
        => Assert.False(MortarTargeting.TrySolve(
            Tube,
            TubeYaw,
            Flight,
            [Standing(x, z), Standing(x + 2f, z + 2f), Standing(x - 2f, z + 2f)],
            NoFriendlies,
            out _,
            out _));

    /// <summary>Holding fire is a decision. A tube with nobody under its sector keeps its round and
    /// its position rather than announcing both for nothing.</summary>
    [Fact]
    public void NoWorthwhileMissionMeansNoMission()
        => Assert.False(MortarTargeting.TrySolve(
            Tube, TubeYaw, Flight, [], NoFriendlies, out _, out _));

    /// <summary>
    /// Continuity, for the same reason the strategic model needs it: the mission is re-solved every
    /// commander second, and a target that jumps in value as a man crosses an invisible line is a
    /// tube that traverses back and forth instead of firing.
    /// </summary>
    [Fact]
    public void CoverageFallsSmoothlyToNothingAtTheEdgeOfTheBurst()
    {
        var blast = MortarConfig.Blast;
        float previous = MortarTargeting.Coverage(Vector3.Zero, Vector3.Zero, 0f, blast);
        for (float distance = 1f; distance <= blast.DamageRadius + 10f; distance += 1f)
        {
            float next = MortarTargeting.Coverage(
                Vector3.Zero, new Vector3(distance, 0f, 0f), 0f, blast);
            Assert.True(next <= previous, $"coverage rose at {distance} m");
            Assert.True(next >= 0f);
            previous = next;
        }

        Assert.Equal(0f, previous, 4);
    }
}
