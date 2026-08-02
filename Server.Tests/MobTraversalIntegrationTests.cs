using System.Numerics;
using Demiurge.GameServer;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// The most ordinary thing an NPC does: walk a long way to an objective over terrain that is not
/// flat. Every other navigation integration here builds a specific pathological shape — a pit, a
/// tunnel, a 62-degree slope — and proves the recovery for it works. None of them check that an NPC
/// crossing gently varied ground gets there at all, which is the case that actually happens all
/// match and the one a too-eager recovery heuristic breaks.
///
/// A hundred metres is chosen so the run cannot be satisfied by drifting: it is roughly the width of
/// the conquest engagement, far enough to need several path segments, and long enough that an NPC
/// which stalls even briefly per obstacle will not finish inside the budget.
///
/// The obstacles are deliberately mild — low mounds with wide gaps either side, always passable by
/// walking around. Nothing here needs digging, jumping or escape behaviour, which is the point: it
/// is a test that ordinary terrain is treated as ordinary.
/// </summary>
public sealed class MobTraversalIntegrationTests(ITestOutputHelper output)
{
    /// <summary>Metres from spawn to objective. Along +X, so the terrain only has to span one axis.</summary>
    private const float Distance = 100f;

    private const float StartX = -50f;
    private const float GoalX = StartX + Distance;

    /// <summary>Close enough to count as arrived — a formation slot is offset from the flag itself.</summary>
    private const float ArrivalRadius = 8f;

    /// <summary>
    /// Simulated seconds allowed. Walking speed covers 100 m in roughly 25 s, so this is generous by
    /// a factor of two and a bit; it is a budget for "does it get there", not a race.
    /// </summary>
    private const int BudgetSeconds = 60;

    /// <summary>
    /// Real milliseconds slept per tick. Navigation searches run on worker threads and install on
    /// the main thread, so a run with no wall-clock time never receives a path at all.
    /// </summary>
    private const int TickDelayMs = 3;

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(0x7A1)]
    [InlineData(0x51D)]
    [InlineData(0xC0FFEE)]
    [InlineData(0xBEEF)]
    public void NpcWalksOneHundredMetresPastSimpleObstacles(int seed)
    {
        var obstacles = Obstacles(seed);
        foreach (var obstacle in obstacles)
            output.WriteLine(
                $"mound x {obstacle.CentreX:0.0} z {obstacle.CentreZ:0.0} "
              + $"half {obstacle.HalfX:0.0}x{obstacle.HalfZ:0.0} rise {obstacle.Rise:0.0}");

        Run(seed, Heightmap(obstacles), $"seed 0x{seed:X}");
    }

    /// <summary>
    /// The control. Perfectly flat ground, no obstacle, nothing to recover from — if this one fails
    /// then the traversal problem is not about obstacles at all, which is a materially different
    /// diagnosis and worth being able to tell apart in one run.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void NpcWalksOneHundredMetresAcrossFlatGround()
        => Run(0xF1A7, (_, _) => MobIntegrationTerrain.Ground, "flat control");

    private void Run(int seed, Func<int, int, float> height, string label)
    {
        var terrain = MobIntegrationTerrain.SoilHeightmap(height, chunkRadius: 4);
        using var world = new MobIntegrationHarness(terrain, seed);

        world.Flags.Spawn(new Vector3(GoalX, height((int)GoalX, 0), 0.5f));
        var mob = world.AddMob(60_000, new Vector3(StartX, height((int)StartX, 0), 0.5f));

        Vector3 start = mob.Position;
        float best = float.MaxValue;
        uint arrivedTick = 0;

        for (uint tick = 1; tick <= BudgetSeconds * NetworkConfig.TickRate; tick++)
        {
            world.Step(tick, TickDelayMs);

            float remaining = MathF.Abs(GoalX - mob.Position.X);
            best = MathF.Min(best, remaining);

            if (remaining <= ArrivalRadius)
            {
                arrivedTick = tick;
                break;
            }
        }

        float covered = mob.Position.X - start.X;
        output.WriteLine(
            $"{label}: covered {covered:0.0} m of {Distance:0} "
          + $"| closest approach {best:0.0} m | final {mob.Position} "
          + $"| arrived tick {arrivedTick}");

        Assert.False(
            world.Mobs.TryDequeueStuckMob(out _),
            $"{label}: the NPC was classified as stuck after covering {covered:0.0} m");

        Assert.True(
            arrivedTick != 0,
            $"{label}: covered only {covered:0.0} m of {Distance:0} in {BudgetSeconds}s "
          + $"(closest approach {best:0.0} m, final {mob.Position})");
    }

    /// <summary>
    /// The same walk, but the corridor crosses a wide shallow bowl — which is what a trench cut with
    /// the map's 15 m spherical brush actually looks like from inside.
    ///
    /// This is the distinction that matters and the one no other test makes. A narrow deep pit
    /// SHOULD start escape digging; a broad depression with walkable sides should not, because
    /// walking out is available and is what the path follower is for. The escape heuristic decides
    /// between them from the surrounding grade, and grade is sampled over a 25x25 m neighbourhood —
    /// so the wider and shallower the depression, the more it looks like the deep pit it is not.
    ///
    /// Depth is fuzzed across the entry threshold on purpose: 1.2 m is under it, 3.5 m is well over,
    /// and every one of these is walkable at a 1:5 grade.
    /// </summary>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(1.2f)]
    [InlineData(2.0f)]
    [InlineData(3.5f)]
    public void NpcWalksThroughAWideShallowDepressionInsteadOfDiggingOutOfIt(float depth)
    {
        const float halfWidth = 15f;
        float wall = depth * 5f;   // 1:5, comfortably inside MaxSlopeDegrees

        float Height(int x, int z)
        {
            float fromCentre = MathF.Abs(x);
            if (fromCentre >= halfWidth + wall) return MobIntegrationTerrain.Ground;

            float drop = fromCentre <= halfWidth
                ? depth
                : depth * (1f - (fromCentre - halfWidth) / wall);

            return MobIntegrationTerrain.Ground - drop;
        }

        Run(0xD1D, Height, $"bowl depth {depth:0.0} m");
    }

    /// <summary>
    /// Walls that span the whole corridor with a single gap in them.
    ///
    /// This is a different problem from the mounds above and the reason those pass while NPCs are
    /// observed running into walls on the real map. A mound offset to one side is solved by
    /// *steering* — any local avoidance drifts around it. A wall that spans the corridor can only be
    /// solved by the search actually routing through the opening, which means the path has to leave
    /// the straight line toward the objective before it can come back to it.
    ///
    /// The walls are 3 m, above the 1.5 m a jump clears, so the gap is the only way through. The gap
    /// is 4-8 m wide — several capsule widths, not a needle — and its offset is fuzzed so the run
    /// has to weave rather than following one lucky lane.
    /// </summary>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(0x3A11)]
    [InlineData(0x9C2)]
    [InlineData(0xD004)]
    public void NpcFindsTheGapInAWallInsteadOfWalkingIntoIt(int seed)
    {
        var walls = Walls(seed);
        foreach (var wall in walls)
            output.WriteLine(
                $"wall x {wall.CentreX:0.0} thickness {wall.HalfX * 2f:0.0} "
              + $"| gap z {wall.GapZ:0.0} width {wall.GapHalf * 2f:0.0}");

        float Height(int x, int z)
        {
            foreach (var wall in walls)
                if (MathF.Abs(x - wall.CentreX) <= wall.HalfX
                    && MathF.Abs(z - wall.GapZ) > wall.GapHalf)
                    return MobIntegrationTerrain.Ground + WallHeight;

            return MobIntegrationTerrain.Ground;
        }

        Run(seed, Height, $"walls seed 0x{seed:X}");
    }

    /// <summary>Above what a jump clears (1.5 m), so the gap is the only way past.</summary>
    private const float WallHeight = 3f;

    private readonly record struct Wall(float CentreX, float HalfX, float GapZ, float GapHalf);

    private static Wall[] Walls(int seed)
    {
        var random = new Random(seed);
        var walls = new List<Wall>();

        for (float x = StartX + 25f; x < GoalX - 15f; x += 30f + (float)random.NextDouble() * 8f)
            walls.Add(new Wall(
                CentreX: x,
                HalfX: 1f,
                // Offset well off the straight line, so reaching it is a real detour.
                GapZ: (float)(random.NextDouble() * 24.0 - 12.0),
                GapHalf: 2f + (float)random.NextDouble() * 2f));

        return [.. walls];
    }

    private readonly record struct Mound(
        float CentreX, float CentreZ, float HalfX, float HalfZ, float Rise);

    /// <summary>
    /// Mounds spaced along the corridor, each offset to one side so a walkable gap always remains.
    /// The lateral offset is at least the half-width plus a capsule's clearance, so "go around it"
    /// is available without any terrain modification.
    /// </summary>
    private static Mound[] Obstacles(int seed)
    {
        var random = new Random(seed);
        var mounds = new List<Mound>();

        for (float x = StartX + 18f; x < GoalX - 12f; x += 16f + (float)random.NextDouble() * 6f)
        {
            float halfZ = 3f + (float)random.NextDouble() * 3f;
            float side = random.Next(2) == 0 ? -1f : 1f;

            mounds.Add(new Mound(
                CentreX: x,
                // Offset so the mound covers the direct line but never the whole corridor.
                CentreZ: side * (halfZ - 1.5f),
                HalfX: 1.5f + (float)random.NextDouble() * 2f,
                HalfZ: halfZ,
                Rise: 1f + (float)random.NextDouble() * 1.5f));
        }

        return [.. mounds];
    }

    private static Func<int, int, float> Heightmap(Mound[] mounds)
        => (x, z) =>
        {
            float height = MobIntegrationTerrain.Ground;

            foreach (var mound in mounds)
                if (MathF.Abs(x - mound.CentreX) <= mound.HalfX
                    && MathF.Abs(z - mound.CentreZ) <= mound.HalfZ)
                    height = MathF.Max(height, MobIntegrationTerrain.Ground + mound.Rise);

            return height;
        };
}
