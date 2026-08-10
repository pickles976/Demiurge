using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// A player walking over a narrow slot must fall into it. The capsule is 0.4 m in radius, so a
/// one-metre trench is wider than the body and cannot be bridged; failing to fall means collision is
/// reporting solid ground where there is a hole.
///
/// Written to answer a specific question — whether the VoxelCursor batching changed collision — and
/// kept because "can I fall in a hole" is the cheapest possible canary for that whole path.
/// </summary>
public class NarrowTrenchFallTests(ITestOutputHelper output)
{
    private const float Ground = SyntheticTerrain.GroundHeight;   // 12.5
    private const float TrenchFloor = 6.5f;

    /// <summary>Flat ground with a slot of the given half-width cut along Z at x = 0.</summary>
    private static ChunkMap Trench(float halfWidth)
        => SyntheticTerrain.Build((x, y, z) =>
        {
            float field = y - Ground;
            float slot = MathF.Max(MathF.Abs(x) - halfWidth, TrenchFloor - y);
            return MathF.Max(field, -slot);
        });

    /// <summary>
    /// The case the synthetic box does not cover: a trench actually DUG with the shovel brush.
    /// A dig is a 0.7 m sphere at half strength, so a "one wide" trench is not a one-metre slot with
    /// vertical walls — it is a rounded groove whose usable width is less than the voxel count
    /// suggests, and the capsule is 0.8 m across.
    /// </summary>
    [Fact]
    public void APlayerCanDropIntoATrenchDugWithTheShovel()
    {
        var map = SyntheticTerrain.Flat();
        foreach (var chunk in map.Snapshot())
            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                var voxel = chunk[i];
                if (voxel.Distance < 0f) voxel.Material = BlockType.BlockType_Dirt;
                chunk[i] = voxel;
            }

        // One voxel wide in X, several long in Z, dug down three voxels — a slit trench.
        for (int z = -3; z <= 3; z++)
            for (int depth = 0; depth < 3; depth++)
                for (int bite = 0; bite < Digging.ClicksPerVoxel * 2; bite++)
                    TerrainEdits.ApplyBox(
                        map,
                        new Vector3(0f, MathF.Round(Ground) - depth, z),
                        Digging.Bite,
                        EditMode.SubtractSoil,
                        BlockType.BlockType_Air,
                        EditShape.Sphere,
                        Digging.BiteStrength);

        float floor = SurfaceQuery.HighestSurfaceY(map, 0, 0) ?? Ground;
        output.WriteLine($"dug trench floor at Y={floor:0.00}, rim {Ground}");
        Assert.True(floor < Ground - 1f, $"the dig did not make a trench: floor {floor:0.00}");

        // Walk across it from one side.
        var state = PlayerMovement.SpawnAt(map, -4f, 0.5f);
        for (int tick = 0; tick < 3 * NetworkConfig.TickRate; tick++)
            PlayerMovement.Step(
                map, ref state, new Vector3(1f, 0f, 0f),
                PlayerStateFlags.Moving, NetworkConfig.FixedDt);

        output.WriteLine($"ended {state.Position}");
        Assert.True(
            state.Position.Y < Ground - 0.5f,
            $"walked over the dug trench instead of dropping in: ended at {state.Position}");
    }

    [Theory]
    [InlineData(0.5f, "one voxel wide")]
    [InlineData(1.0f, "two voxels wide")]
    public void APlayerWalkingOverANarrowTrenchFallsIn(float halfWidth, string label)
    {
        var map = Trench(halfWidth);

        // Start clear of the slot and walk across it.
        var state = PlayerMovement.SpawnAt(map, -4f, 0.5f);
        float startY = state.Position.Y;

        for (int tick = 0; tick < 3 * NetworkConfig.TickRate; tick++)
            PlayerMovement.Step(
                map,
                ref state,
                new Vector3(1f, 0f, 0f),
                PlayerStateFlags.Moving,
                NetworkConfig.FixedDt);

        output.WriteLine(
            $"{label}: started Y={startY:0.00} at x=-4, ended {state.Position} "
          + $"(trench floor {TrenchFloor}, rim {Ground})");

        // Either it fell in, or it walked clean across — and walking across a hole wider than the
        // body is the bug.
        Assert.True(
            state.Position.Y < Ground - 1f,
            $"{label}: bridged the trench and stayed at Y={state.Position.Y:0.00}");
    }
}
