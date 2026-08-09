using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

public class CombatBehaviorTests
{
    [Fact]
    public void IncomingFireCreatesABoundedDefensiveWindow()
    {
        var brain = new MobBrain();

        brain.MarkUnderFire(100);

        Assert.True(brain.IsUnderFire(100));
        Assert.True(brain.IsUnderFire(
            100 + MobBrain.IncomingFireResponseTicks - 1));
        Assert.False(brain.IsUnderFire(
            100 + MobBrain.IncomingFireResponseTicks));
    }

    // ShouldAdvance, AimMoaForRange, PrefersToHoldFire and MaxEngagementRangeFor are gone. They
    // encoded weapon character as constants keyed on ItemType — a 75 m PPSh ceiling, a 150 m Mosin
    // ceiling, a flat 720 MOA aim error — because the flat aim term made the ballistics table's own
    // per-weapon dispersion arithmetically invisible to the AI.
    //
    // The behaviour they described is now derived, so the tests describe it the same way: as
    // orderings that must hold, not as the constants that used to produce them. A test written
    // against a heuristic encodes that heuristic's special cases and then obstructs the general
    // system that replaced it.

    [Theory]
    [InlineData(ItemType.Ppsh)]
    [InlineData(ItemType.Sks)]
    [InlineData(ItemType.Mosin)]
    public void EveryWeaponEventuallyStopsBeingWorthFiring(ItemType weapon)
    {
        // The engagement ceiling still exists — it is just a consequence of a round stopping being
        // worth its expected return, rather than a number somebody wrote down per weapon.
        Assert.True(Reach(weapon) > 0f, $"{weapon} cannot engage at any range");
        Assert.True(Reach(weapon) < 1000f, $"{weapon} never stops engaging");
    }

    [Fact]
    public void ReachOrdersWeaponsFromSubmachineGunToBoltAction()
    {
        Assert.True(Reach(ItemType.Ppsh) < Reach(ItemType.Sks));
        Assert.True(Reach(ItemType.Sks) < Reach(ItemType.Mosin));
    }

    [Fact]
    public void ADeadShotReachesFurtherThanAPoorOne()
    {
        Assert.True(
            Reach(ItemType.Sks, skillFactor: 0.5f) > Reach(ItemType.Sks, skillFactor: 2f),
            "the skill dial scales sighting error, so it must scale reach with it");
    }

    /// <summary>Furthest range at which this weapon still returns a firing solution.</summary>
    private static float Reach(ItemType weapon, float skillFactor = 1f)
    {
        float last = 0f;
        for (float range = 1f; range <= 1000f; range += 1f)
        {
            if (WeaponEffectiveness.Best(weapon, range, TargetExposure.Full, 0f, skillFactor).DamagePerSecond <= 0f)
                break;
            last = range;
        }
        return last;
    }

    [Theory]
    [InlineData(99f, true, true)]
    [InlineData(101f, true, false)]
    [InlineData(50f, false, false)]
    public void EnemyReloadTellIsKnownOnlyWhileReloadingWithinOneHundredMetres(
        float range,
        bool reloading,
        bool expected)
    {
        var observer = new ServerPlayer { Move = new MoveState { Position = Vector3.Zero } };
        var enemy = new ServerPlayer
        {
            Move = new MoveState { Position = new Vector3(0f, 0f, range) },
            State = reloading ? PlayerStateFlags.Reloading : PlayerStateFlags.None,
        };

        Assert.Equal(expected, MobSystem.CanRecognizeEnemyReload(observer, enemy));
    }
    /// <summary>
    /// A decider does not get to be an actor. CombatBehavior used to write mob.State, Yaw, Pitch and
    /// LastIntent directly, and it runs BEFORE MobSystem's own movement writes — so whether an NPC
    /// ended the tick Aiming or Moving depended on which module happened to run last. It now REPORTS
    /// them and MobSystem applies them once.
    /// </summary>
    [Fact]
    public void CombatReportsTheActorsStateWithoutWritingIt()
    {
        var server = new NullNetServer();
        var objects = new ObjectReplication(server);
        var weapons = new WeaponSystem(server, objects, new ChunkMap());
        var items = new ItemSystem(objects);
        var combat = new CombatBehavior(weapons, new ChunkMap());

        var mob = new ServerPlayer
        {
            Id = 60_000,
            IsMob = true,
            Move = new MoveState { Position = Vector3.Zero },
            Yaw = 1.25f,
            Pitch = 0.5f,
            State = PlayerStateFlags.Moving,
            LastIntent = Vector3.UnitX,
        };
        items.SpawnInfantryLoadout(mob);

        var brain = new MobBrain();
        brain.Contacts.Observe(60_001, new Vector3(0f, 0f, 20f), tick: 10);

        var outcome = combat.Tick(mob, brain, tick: 10, dt: 1f / NetworkConfig.TickRate);

        Assert.True(outcome.OwnsTick);
        Assert.True(outcome.Flags.HasFlag(PlayerStateFlags.Aiming));

        // The actor is exactly as it was handed over.
        Assert.Equal(1.25f, mob.Yaw);
        Assert.Equal(0.5f, mob.Pitch);
        Assert.Equal(PlayerStateFlags.Moving, mob.State);
        Assert.Equal(Vector3.UnitX, mob.LastIntent);
    }
}
