using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// The sense/decide/act invariant: an NPC's visible output for a tick is written in one place, from
/// one value. Overwriting was never a race — everything here runs on the main thread — it was five
/// branches of one function each constructing the whole answer and returning.
/// </summary>
public class MobDecideActTests
{
    [Fact]
    public void ComposedStateCarriesTheActionAndNothingElse()
    {
        var action = new MobAction
        {
            Intent = Vector3.UnitX,
            Jump = true,
            Sprint = true,
            Crouch = false,
            Aiming = true,
            Yaw = 1.5f,
            Pitch = -0.25f,
            TurnTo = true,
        };

        MobSystem.ComposeState(action, out var state);

        Assert.True(state.HasFlag(PlayerStateFlags.Moving));
        Assert.True(state.HasFlag(PlayerStateFlags.Jumping));
        Assert.True(state.HasFlag(PlayerStateFlags.Sprinting));
        Assert.True(state.HasFlag(PlayerStateFlags.Aiming));
        Assert.False(state.HasFlag(PlayerStateFlags.Crouching));
        Assert.False(state.HasFlag(PlayerStateFlags.Shooting));
        Assert.False(state.HasFlag(PlayerStateFlags.Reloading));
    }

    /// <summary>
    /// Composed from nothing, not merged onto what the actor already had. Three of the five old exits
    /// merged (<c>mob.State.With(...)</c>) and two replaced (<c>PlayerStateFlags.None.With(...)</c>),
    /// so whether a flag survived a tick depended on which branch produced it.
    /// </summary>
    [Fact]
    public void AnEmptyActionIsAManStandingStill()
    {
        MobSystem.ComposeState(default, out var state);
        Assert.Equal(PlayerStateFlags.None, state);
    }

    [Fact]
    public void ProneActionProducesOneStanceOnly()
    {
        MobSystem.ComposeState(new MobAction { Crouch = true, Prone = true }, out var state);

        Assert.True(state.HasFlag(PlayerStateFlags.Prone));
        Assert.False(state.HasFlag(PlayerStateFlags.Crouching));
    }

    [Theory]
    [InlineData(true, false, false, true, 60f, 100u, 0u, true)]
    [InlineData(false, false, false, true, 60f, 100u, 0u, false)]
    [InlineData(true, true, false, true, 60f, 100u, 0u, false)]
    [InlineData(true, false, true, true, 60f, 100u, 0u, false)]
    [InlineData(true, false, false, false, 60f, 100u, 0u, false)]
    [InlineData(true, false, false, true, 20f, 100u, 0u, false)]
    [InlineData(true, false, false, true, 60f, 100u, 101u, false)]
    public void ProneRequiresALongRangeEngagementAndNoStancePenalty(
        bool underFire,
        bool atCover,
        bool digging,
        bool engaging,
        float engagementRange,
        uint tick,
        uint nextProneTick,
        bool expected)
        => Assert.Equal(
            expected,
            MobSystem.ShouldGoProne(
                Vector3.Zero,
                underFire,
                atCover,
                digging,
                engaging,
                engagementRange,
                tick,
                nextProneTick));

    [Fact]
    public void MovingActorNeverGoesProne()
        => Assert.False(MobSystem.ShouldGoProne(
            Vector3.UnitX,
            underFire: true,
            atCover: false,
            digging: false,
            engaging: true,
            engagementRange: 60f,
            tick: 100,
            nextProneTick: 0));

    /// <summary>
    /// The label an NPC carries must be what it actually did. `ai track states` reads DebugIntent,
    /// and an entrenched man with no live contact used to report OBJECTIVE while cycling between his
    /// foxhole centre and his peek station — the decision said one thing and two branches below it
    /// did another.
    /// </summary>
    [Fact]
    public void EntrenchmentIsADecisionRatherThanSomethingThatHappensAfterOne()
    {
        ActorIntent dugIn = new ActorIntent.HoldFightingPosition();
        ActorIntent objective = new ActorIntent.PursueObjective();

        Assert.Equal("DUGIN", dugIn.DebugLabel);
        Assert.NotEqual(dugIn.DebugLabel, objective.DebugLabel);
    }
}
