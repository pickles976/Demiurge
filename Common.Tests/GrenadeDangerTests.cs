using System.Numerics;

namespace Demiurge.Tests;

public class GrenadeDangerTests
{
    private static LiveBlast At(float x, float seconds = 1.5f)
        => new(new Vector3(x, 0f, 0f), seconds, GrenadeConfig.Blast);

    [Fact]
    public void AGrenadeAtYourFeetIsLethalAndPointsYouAway()
    {
        float risk = GrenadeDanger.Evaluate(Vector3.Zero, [At(1f)], out var away);

        Assert.True(risk > 0f);
        Assert.True(away.X < 0f, "away from the grenade, not toward it");
        Assert.Equal(0f, away.Y);
    }

    [Fact]
    public void AGrenadeBeyondItsDamageRadiusIsNotWorthMoving()
        => Assert.Equal(
            0f,
            GrenadeDanger.Evaluate(Vector3.Zero, [At(GrenadeConfig.DamageRadius + 1f)], out _));

    /// <summary>
    /// A grenade you cannot get clear of before it goes off is not worth running from — the running
    /// is what leaves cover. Time to detonate has to enter the answer, or NPCs sprint into the open
    /// for a fuse that expires first.
    /// </summary>
    [Fact]
    public void AGrenadeAboutToDetonateIsNotWorthRunningFrom()
    {
        float plenty = GrenadeDanger.Evaluate(Vector3.Zero, [At(2f, seconds: 2.5f)], out _);
        float none = GrenadeDanger.Evaluate(Vector3.Zero, [At(2f, seconds: 0.05f)], out _);

        Assert.True(plenty > none, $"plenty {plenty} should beat none {none}");
    }

    /// <summary>Two grenades either side must not average into "stand still".</summary>
    [Fact]
    public void SurroundedByTwoBlastsStillProducesAnEscapeDirection()
    {
        float risk = GrenadeDanger.Evaluate(
            Vector3.Zero,
            [At(2f), new LiveBlast(new Vector3(-2f, 0f, 0f), 1.5f, GrenadeConfig.Blast)],
            out var away);

        Assert.True(risk > 0f);
        Assert.True(away.LengthSquared() > 0.5f, "an escape direction, not a cancelled-out zero");
    }

    /// <summary>Nothing live is nothing to do. The common case, and it must cost nothing.</summary>
    [Fact]
    public void NoGrenadesIsNoDanger()
        => Assert.Equal(0f, GrenadeDanger.Evaluate(Vector3.Zero, [], out _));
}
