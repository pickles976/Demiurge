using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public sealed class CoverBehaviorRevalidationTests
{
    [Fact]
    public void ExistingConcealmentSurvivesWhenItStillBlocksCrouchedSight()
    {
        var map = MobIntegrationTerrain.SoilHeightmap(
            (x, _) => x is >= 0 and <= 1
                ? MobIntegrationTerrain.Ground + 1.2f
                : MobIntegrationTerrain.Ground);
        var behavior = new CoverBehavior(map);
        Vector3 position = SurfaceQuery.SurfacePosition(map, -2f, 0f);
        Vector3 threat = SurfaceQuery.SurfacePosition(map, 4f, 0f);
        AiContact[] contacts = [new(61_000, threat, 0, 1f)];

        Assert.True(behavior.TryRevalidate(
            position,
            position,
            CoverKind.Concealment,
            contacts,
            out var refreshed));
        Assert.Equal(CoverKind.Concealment, refreshed.Kind);
        Assert.NotEmpty(refreshed.DependencyChunks);
    }

    [Fact]
    public void ExistingConcealmentFailsWhenCrouchedSightIsNowClear()
    {
        var map = MobIntegrationTerrain.SoilHeightmap(
            (_, _) => MobIntegrationTerrain.Ground);
        var behavior = new CoverBehavior(map);
        Vector3 position = SurfaceQuery.SurfacePosition(map, -2f, 0f);
        Vector3 threat = SurfaceQuery.SurfacePosition(map, 4f, 0f);
        AiContact[] contacts = [new(61_000, threat, 0, 1f)];

        Assert.False(behavior.TryRevalidate(
            position,
            position,
            CoverKind.Concealment,
            contacts,
            out _));
    }

    [Fact]
    public void AdvanceOnlyRequiresAStandableDestination()
    {
        var map = MobIntegrationTerrain.SoilHeightmap(
            (_, _) => MobIntegrationTerrain.Ground);
        var behavior = new CoverBehavior(map);
        Vector3 position = SurfaceQuery.SurfacePosition(map, 0f, 0f);
        Vector3 threat = SurfaceQuery.SurfacePosition(map, 4f, 0f);
        AiContact[] contacts = [new(61_000, threat, 0, 1f)];

        Assert.True(behavior.TryRevalidate(
            position,
            position,
            CoverKind.Advance,
            contacts,
            out _));
    }
}
