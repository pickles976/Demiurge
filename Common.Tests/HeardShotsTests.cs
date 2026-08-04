using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// What an NPC remembers about gunfire.
///
/// The bug being fixed: a single last-write-wins slot means a distant shot erases a point-blank one,
/// so an NPC forgets the player standing behind it because somebody fired across the map a tick
/// later. Memory must be ranked by how much the shot matters, not by which arrived last.
/// </summary>
public class HeardShotsTests
{
    [Fact]
    public void APointBlankShotSurvivesADistantOneArrivingLater()
    {
        var shots = new HeardShots();
        shots.Hear(shooterId: 7, new Vector3(2f, 0f, 0f), distance: 2f, tick: 100);
        shots.Hear(shooterId: 9, new Vector3(55f, 0f, 0f), distance: 55f, tick: 101);

        Assert.True(shots.TryMostSalient(101, out var best));
        Assert.Equal(7, best.ShooterId);
    }

    [Fact]
    public void ACloserShotDisplacesAnEarlierDistantOne()
    {
        var shots = new HeardShots();
        shots.Hear(9, new Vector3(55f, 0f, 0f), 55f, 100);
        shots.Hear(7, new Vector3(2f, 0f, 0f), 2f, 101);

        Assert.True(shots.TryMostSalient(101, out var best));
        Assert.Equal(7, best.ShooterId);
    }

    [Fact]
    public void RepeatedFireFromOneShooterDoesNotFillTheMemory()
    {
        var shots = new HeardShots();
        for (uint tick = 0; tick < 30; tick++)
            shots.Hear(7, new Vector3(20f, 0f, 0f), 20f, tick);

        Assert.Equal(1, shots.Count);
    }

    [Fact]
    public void ShotsExpireAfterTheInvestigationWindow()
    {
        var shots = new HeardShots();
        shots.Hear(7, Vector3.Zero, 5f, tick: 10);

        uint expired = 10 + GunshotHearing.InvestigationTicks;
        shots.Prune(expired);

        Assert.False(shots.TryMostSalient(expired, out _));
        Assert.Equal(0, shots.Count);
    }

    [Fact]
    public void NothingHeardMeansNothingToReport()
        => Assert.False(new HeardShots().TryMostSalient(0, out _));

    [Fact]
    public void LocalisationErrorGrowsWithDistanceAndIsNearlyExactUpClose()
    {
        float close = GunshotHearing.LocalisationError(2f);
        float far = GunshotHearing.LocalisationError(GunshotHearing.MaximumDistance);

        Assert.True(close < 1f, $"a shot 2 m away should localise within a metre, got {close}");
        Assert.True(far > close * 5f, $"a shot at max range should be vague, got {far}");
    }

    [Fact]
    public void PerceivedPositionStaysWithinTheErrorOfTheTruth()
    {
        var listener = Vector3.Zero;
        var truth = new Vector3(40f, 0f, 0f);

        for (uint seed = 1; seed < 200; seed++)
        {
            var perceived = GunshotHearing.PerceivedPosition(listener, truth, seed);
            float slip = Vector3.Distance(perceived, truth);
            Assert.True(
                slip <= GunshotHearing.LocalisationError(40f) + 1e-3f,
                $"seed {seed} slipped {slip:0.00} m");
        }
    }
}
