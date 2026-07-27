using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// The shared kinematic step. This is the code the server runs authoritatively and the client both
/// predicts and replays with, so a disagreement here is a rubber-banding player rather than a
/// failing assertion — worth testing even though the rest of the movement path is not.
/// </summary>
public class PlayerMovementTests
{
    const float Ground = SyntheticTerrain.GroundHeight;
    const float Dt = NetworkConfig.FixedDt;

    static MoveState At(Vector3 feet) => new() { Position = feet, Velocity = Vector3.Zero, Grounded = false };

    /// <summary>Runs the step and returns the final state. Intent is world-space, as it is on the wire.</summary>
    static MoveState Run(ChunkMap map, MoveState state, Vector3 intent, PlayerStateFlags flags, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            PlayerMovement.Step(map, ref state, intent, flags, Dt);

        return state;
    }

    /// <summary>Settles a body onto the terrain, the way it stands after a moment of being spawned.</summary>
    static MoveState Settled(ChunkMap map, Vector3 feet)
        => Run(map, At(feet), Vector3.Zero, PlayerStateFlags.None, 60);

    /// <summary>Where the feet rest: pushed out to the skin gap, no further.</summary>
    static float RestingY(float surface) => surface + PlayerMovement.SkinWidth;

    // ---- Ground ----

    [Fact]
    public void FallsAndComesToRestOnFlatGround()
    {
        var map = SyntheticTerrain.Flat();

        var state = Run(map, At(new Vector3(2f, Ground + 6f, -3f)), Vector3.Zero, PlayerStateFlags.None, 90);

        Assert.True(state.Grounded);
        Assert.Equal(RestingY(Ground), state.Position.Y, 1);
        Assert.Equal(0f, state.Velocity.Y, 2);
    }

    /// <summary>
    /// Standing still must not creep. Gravity integrates downward every tick and the pushout cancels
    /// it every tick; if the two did not exactly balance, a player left alone would sink or climb.
    /// </summary>
    [Fact]
    public void StandingStillDoesNotDrift()
    {
        var map = SyntheticTerrain.Flat();

        var settled = Settled(map, new Vector3(0f, Ground + 1f, 0f));
        var later = Run(map, settled, Vector3.Zero, PlayerStateFlags.None, 300);

        Assert.Equal(settled.Position.Y, later.Position.Y, 3);
        Assert.True(later.Grounded);
    }

    [Fact]
    public void WalksAcrossAChunkBorderWithoutChangingHeight()
    {
        var map = SyntheticTerrain.Flat();

        // Starts left of x = 16 and walks well past it, so the body's samples straddle two chunks.
        var settled = Settled(map, new Vector3(12f, Ground + 1f, 0f));
        var crossed = Run(map, settled, Vector3.UnitX, PlayerStateFlags.None, 90);

        Assert.True(crossed.Position.X > 20f, $"expected to cross the border, got x = {crossed.Position.X}");
        Assert.Equal(settled.Position.Y, crossed.Position.Y, 2);
        Assert.True(crossed.Grounded);
    }

    // ---- Walls ----

    [Fact]
    public void WallStopsForwardMotionButNotSideways()
    {
        const float WallX = 6f;
        var map = SyntheticTerrain.Wall(WallX);

        var settled = Settled(map, new Vector3(0f, Ground + 1f, 0f));
        var slid = Run(map, settled, new Vector3(1f, 0f, 1f), PlayerStateFlags.None, 90);

        // Held off the wall by the body's radius, and no further into it than the skin allows.
        Assert.True(slid.Position.X < WallX - PlayerMovement.Body.Radius + 0.1f,
            $"penetrated the wall: x = {slid.Position.X}");
        Assert.True(slid.Position.X > WallX - PlayerMovement.Body.Radius - 0.3f,
            $"stopped short of the wall: x = {slid.Position.X}");

        // The tangential half of the move survives — that is the difference between sliding and sticking.
        Assert.True(slid.Position.Z > 3f, $"did not slide along the wall: z = {slid.Position.Z}");
    }

    // ---- Slopes ----

    [Fact]
    public void WalksUpASlopeWithinTheLimit()
    {
        var map = SyntheticTerrain.Slope(30f);

        var settled = Settled(map, new Vector3(0f, Ground + 1f, 0f));
        var climbed = Run(map, settled, Vector3.UnitX, PlayerStateFlags.None, 60);

        Assert.True(climbed.Position.X > settled.Position.X + 1f, $"made no progress: x = {climbed.Position.X}");
        Assert.True(climbed.Position.Y > settled.Position.Y + 0.5f, $"did not climb: y = {climbed.Position.Y}");
        Assert.True(climbed.Grounded);
    }

    /// <summary>
    /// Past the limit the surface acts as a wall. The normal's vertical component is what makes this
    /// subtle: left in the pushout it walks the body straight up the face, so a too-steep contact has
    /// to be flattened rather than merely not counted as ground.
    /// </summary>
    [Fact]
    public void SlopePastTheLimitBlocksAndIsNotGround()
    {
        var map = SyntheticTerrain.Slope(70f);

        var settled = Settled(map, new Vector3(-2f, Ground + 1f, 0f));
        var blocked = Run(map, settled, Vector3.UnitX, PlayerStateFlags.None, 60);

        Assert.True(blocked.Position.Y < settled.Position.Y + 0.3f,
            $"climbed a 70-degree face: y went {settled.Position.Y} -> {blocked.Position.Y}");
    }

    // ---- Jumping ----

    /// <summary>
    /// The whole point of the number: a 1.5 m jump has to actually reach 1.5 m under the discrete
    /// integration, not the 1.37 m that the textbook launch speed gives at 30 Hz.
    /// </summary>
    [Fact]
    public void JumpReachesTheConfiguredHeight()
    {
        var map = SyntheticTerrain.Flat();
        var state = Settled(map, new Vector3(0f, Ground + 1f, 0f));

        float start = state.Position.Y;
        float apex = start;

        PlayerMovement.Step(map, ref state, Vector3.Zero, PlayerStateFlags.Jumping, Dt);

        for (int i = 0; i < 60; i++)
        {
            PlayerMovement.Step(map, ref state, Vector3.Zero, PlayerStateFlags.None, Dt);
            apex = MathF.Max(apex, state.Position.Y);
        }

        Assert.Equal(PlayerMovement.JumpHeight, apex - start, 1);
    }

    /// <summary>A 1.5 m jump has to clear a 1.5 m ledge — the reason the height was chosen.</summary>
    [Fact]
    public void JumpClearsALedgeOfTheConfiguredHeight()
    {
        const float LedgeX = 4f;
        float ledgeTop = Ground + PlayerMovement.JumpHeight;

        // Low ground up to LedgeX, a full JumpHeight step up past it.
        var map = SyntheticTerrain.Build((x, y, z) => x < LedgeX ? y - Ground : y - ledgeTop);

        var state = Settled(map, new Vector3(0f, Ground + 1f, 0f));

        // Hold Space on the way in, which is how a player does it: the step only acts on it while
        // grounded, so holding it re-jumps on each landing until one of them clears the ledge.
        var onLedge = Run(map, state, Vector3.UnitX, PlayerStateFlags.Jumping, 120);
        Assert.True(onLedge.Position.X > LedgeX + 0.5f, $"never got onto the ledge: x = {onLedge.Position.X}");

        // Then release and let it settle, or it is still mid-bounce and Grounded means nothing.
        var resting = Run(map, onLedge, Vector3.Zero, PlayerStateFlags.None, 60);

        Assert.Equal(RestingY(ledgeTop), resting.Position.Y, 1);
        Assert.True(resting.Grounded);
    }

    [Fact]
    public void CannotJumpWhileAirborne()
    {
        var map = SyntheticTerrain.Flat();

        var falling = At(new Vector3(0f, Ground + 8f, 0f));
        PlayerMovement.Step(map, ref falling, Vector3.Zero, PlayerStateFlags.None, Dt);

        float before = falling.Velocity.Y;
        PlayerMovement.Step(map, ref falling, Vector3.Zero, PlayerStateFlags.Jumping, Dt);

        Assert.False(falling.Grounded);
        Assert.True(falling.Velocity.Y < before, "a mid-air jump added upward velocity");
    }

    /// <summary>
    /// The ground is still well within snapping distance on the tick a jump starts. Snap to it and the
    /// jump is cancelled on the frame it began — the player just never leaves the floor.
    /// </summary>
    [Fact]
    public void GroundSnapDoesNotCancelTheJumpItStartsOn()
    {
        var map = SyntheticTerrain.Flat();
        var state = Settled(map, new Vector3(0f, Ground + 1f, 0f));

        float start = state.Position.Y;
        PlayerMovement.Step(map, ref state, Vector3.Zero, PlayerStateFlags.Jumping, Dt);

        Assert.False(state.Grounded);
        Assert.True(state.Position.Y > start + 0.1f, $"went nowhere: {start} -> {state.Position.Y}");
    }

    // ---- Ceilings, overhangs, caves ----

    [Fact]
    public void CeilingStopsAJumpShort()
    {
        // Head room is 0.7 m: standing height is 1.8, so the body's top is at Ground + 1.8.
        var map = SyntheticTerrain.Ceiling(Ground + PlayerMovement.Body.Height + 0.7f);

        var state = Settled(map, new Vector3(0f, Ground + 0.5f, 0f));
        float start = state.Position.Y;
        float apex = start;

        PlayerMovement.Step(map, ref state, Vector3.Zero, PlayerStateFlags.Jumping, Dt);

        for (int i = 0; i < 60; i++)
        {
            PlayerMovement.Step(map, ref state, Vector3.Zero, PlayerStateFlags.None, Dt);
            apex = MathF.Max(apex, state.Position.Y);
        }

        Assert.True(apex - start < 1.0f, $"passed through the ceiling: rose {apex - start:F2} m");
        Assert.True(state.Grounded, "did not come back down");
    }

    /// <summary>
    /// A floating slab: air above AND below. Nothing here is expressible as a heightmap, so this is
    /// the case that proves collision reads the 3D field rather than a surface height.
    /// </summary>
    [Fact]
    public void StandsOnAnOverhangWithAirBeneathIt()
    {
        const float SlabTop = 40f;
        var map = SyntheticTerrain.Slab(SlabTop - 2f, SlabTop);

        var state = Settled(map, new Vector3(3f, SlabTop + 1f, 3f));

        Assert.True(state.Grounded);
        Assert.Equal(RestingY(SlabTop), state.Position.Y, 1);
    }

    /// <summary>
    /// Floor and ceiling in the same column with air between them. The floor has to hold and the
    /// ceiling has to be there — a heightmap can express neither at once.
    /// </summary>
    [Fact]
    public void WalksAlongACaveFloor()
    {
        var map = SyntheticTerrain.Build((x, y, z) => MathF.Min(y - 20f, 23f - y));

        var state = Settled(map, new Vector3(0f, 21f, 0f));
        var walked = Run(map, state, Vector3.UnitX, PlayerStateFlags.None, 90);

        Assert.True(walked.Position.X > 5f, $"stuck in the cave: x = {walked.Position.X}");
        Assert.Equal(RestingY(20f), walked.Position.Y, 1);
        Assert.True(walked.Grounded);
    }

    /// <summary>
    /// A 3 m gap has less headroom than a 1.5 m jump needs for a 1.8 m body, so holding Space in a
    /// cave must not get you out through the roof however many times you bounce.
    /// </summary>
    [Fact]
    public void JumpingInACaveCannotEscapeThroughTheCeiling()
    {
        const float CeilingY = 23f;
        var map = SyntheticTerrain.Build((x, y, z) => MathF.Min(y - 20f, CeilingY - y));

        var state = Settled(map, new Vector3(0f, 21f, 0f));
        float highest = state.Position.Y;

        for (int i = 0; i < 300; i++)
        {
            PlayerMovement.Step(map, ref state, Vector3.UnitX, PlayerStateFlags.Jumping, Dt);
            highest = MathF.Max(highest, state.Position.Y);
        }

        Assert.True(highest < CeilingY - PlayerMovement.Body.Height,
            $"the body's head went through the ceiling: feet reached {highest:F2}");
    }

    // ---- Missing data ----

    /// <summary>
    /// Unloaded terrain is impassable. It is also the world's edge, since the generated region is
    /// finite — and on the client it is why prediction has to be suspended until its own chunks arrive.
    /// </summary>
    [Fact]
    public void StopsAtTheEdgeOfLoadedTerrain()
    {
        var map = SyntheticTerrain.BuildOne(new ChunkIndex { x = 0, z = 0 }, (x, y, z) => y - Ground);

        var settled = Settled(map, new Vector3(8f, Ground + 1f, 8f));
        var stopped = Run(map, settled, Vector3.UnitX, PlayerStateFlags.None, 90);

        // The chunk covers x in [0, 16), and the body samples a voxel or two past itself.
        Assert.True(stopped.Position.X < 16f, $"walked into unloaded terrain: x = {stopped.Position.X}");
        Assert.True(stopped.Position.X > 10f, $"stopped far too early: x = {stopped.Position.X}");
    }

    /// <summary>
    /// Buried past the quantization clamp there is no gradient — no depth and no escape direction.
    /// Undefined behaviour here means a player who ends up inside a hill is stuck there permanently.
    /// </summary>
    [Fact]
    public void BuriedBodyClimbsOut()
    {
        var map = SyntheticTerrain.Build((x, y, z) => y < 40f ? -8f : y - 40f);

        var state = At(new Vector3(0f, 20f, 0f));
        float start = state.Position.Y;

        state = Run(map, state, Vector3.Zero, PlayerStateFlags.None, 60);

        Assert.True(state.Position.Y > start, $"stayed buried: {start} -> {state.Position.Y}");
    }

    // ---- The actual world ----

    /// <summary>
    /// The synthetic fields above are exact but hand-picked. This one runs on the real generator, which
    /// is where a wrong sign or a transposed axis would show up as a player standing in the air or
    /// buried to the waist. Spawn, walk a while, and stay on the surface the mesher would draw.
    /// </summary>
    [Fact]
    public void SpawnsAndWalksOnGeneratedTerrain()
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);

        var state = PlayerMovement.SpawnAt(map, 0f, 0f);

        // Settling must not move the body: SpawnAt already puts the feet on the surface.
        float spawnY = state.Position.Y;
        state = Run(map, state, Vector3.Zero, PlayerStateFlags.None, 30);

        Assert.True(state.Grounded, "did not spawn on the ground");
        Assert.True(MathF.Abs(state.Position.Y - spawnY) < 0.2f,
            $"spawn was not resting: {spawnY:F3} -> {state.Position.Y:F3}");

        // Walk a few metres and check the feet track the surface the mesher would draw there, rather
        // than floating over it or sinking into it.
        var walked = Run(map, state, new Vector3(1f, 0f, 0.4f), PlayerStateFlags.None, 120);

        Assert.True(Vector2.Distance(new Vector2(walked.Position.X, walked.Position.Z),
                                     new Vector2(state.Position.X, state.Position.Z)) > 3f,
            "made no horizontal progress on real terrain");
        Assert.True(walked.Grounded, "lost the ground while walking");

        float? surface = SurfaceQuery.HighestSurfaceY(map,
            (int)MathF.Floor(walked.Position.X), (int)MathF.Floor(walked.Position.Z));

        Assert.NotNull(surface);
        Assert.True(MathF.Abs(walked.Position.Y - surface.Value) < 0.6f,
            $"feet at {walked.Position.Y:F3}, surface at {surface.Value:F3}");
    }

    // ---- Determinism ----

    /// <summary>
    /// Reconciliation replays pending moves from authoritative state and compares against what it had
    /// predicted. If the step is not deterministic, that comparison is noise and every tick reads as a
    /// correction — so this is the property the whole prediction scheme rests on.
    /// </summary>
    [Fact]
    public void SameInputsProduceBitwiseIdenticalResults()
    {
        var map = SyntheticTerrain.Slope(25f);
        var intents = new[] { Vector3.UnitX, new Vector3(1f, 0f, 1f), -Vector3.UnitZ, Vector3.Zero };

        MoveState Sequence()
        {
            var state = Settled(map, new Vector3(0f, Ground + 1f, 0f));

            for (int i = 0; i < 120; i++)
            {
                var flags = i % 17 == 0 ? PlayerStateFlags.Jumping : PlayerStateFlags.None;
                PlayerMovement.Step(map, ref state, intents[i % intents.Length], flags, Dt);
            }

            return state;
        }

        var first = Sequence();
        var second = Sequence();

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(first.Velocity, second.Velocity);
        Assert.Equal(first.Grounded, second.Grounded);
    }

    /// <summary>
    /// The reconciliation shape itself: replaying from a snapshot of the state reproduces the run that
    /// followed it. This is exactly what LocalPlayer.Reconcile does when the server's position arrives.
    /// </summary>
    [Fact]
    public void ReplayFromASnapshotReproducesTheRun()
    {
        var map = SyntheticTerrain.Slope(15f);
        var moves = new[] { Vector3.UnitX, Vector3.UnitX, new Vector3(1f, 0f, -1f), Vector3.Zero, Vector3.UnitZ };

        var authoritative = Settled(map, new Vector3(0f, Ground + 1f, 0f));

        var predicted = authoritative;
        foreach (var intent in moves)
            PlayerMovement.Step(map, ref predicted, intent, PlayerStateFlags.None, Dt);

        var replayed = authoritative;
        foreach (var intent in moves)
            PlayerMovement.Step(map, ref replayed, intent, PlayerStateFlags.None, Dt);

        Assert.Equal(predicted.Position, replayed.Position);
        Assert.Equal(predicted.Velocity, replayed.Velocity);
    }
}
