using System.Numerics;
using Demiurge.GameServer;
using Riptide;

namespace Demiurge.ServerTests;

public class GrenadeSystemTests
{
    [Fact]
    public void BlastHasLinearFalloffAndDamagesThrowerFriendliesAndMobs()
    {
        var thrower = PlayerAt(1, 3f);
        var friendly = PlayerAt(2, 7f);
        var mob = PlayerAt(60000, 4f, isMob: true);
        var outside = PlayerAt(3, 10f);
        var killed = new List<ushort>();

        GrenadeSystem.ApplyBlastDamage(
            Vector3.Zero,
            [thrower, friendly, mob, outside],
            victim => killed.Add(victim.Id));

        Assert.Equal(0, thrower.Status!.Health.Current);
        Assert.Equal(50, friendly.Status!.Health.Current);
        Assert.Equal(0, mob.Status!.Health.Current);
        Assert.Equal(100, outside.Status!.Health.Current);
        Assert.True(thrower.Status.Dirty.HasFlag(NetComponents.Health));
        Assert.True(friendly.Status.Dirty.HasFlag(NetComponents.Health));
        Assert.True(mob.Status.Dirty.HasFlag(NetComponents.Health));
        Assert.False(outside.Status.Dirty.HasFlag(NetComponents.Health));
        Assert.Equal([thrower.Id, mob.Id], killed);
    }

    [Fact]
    public void GrenadeStackCyclesForOneAndAHalfSecondsBetweenThrows()
    {
        var server = new Server();
        var terrain = new ChunkMap();
        var objects = new ObjectReplication(server);
        var items = new ItemSystem(objects);
        var terrainEdits = new TerrainSystem(server, terrain);
        var grenades = new GrenadeSystem(objects, items, terrainEdits, terrain);
        var player = new ServerPlayer
        {
            Id = 1,
            Move = new MoveState { Position = new Vector3(0f, 10f, 0f) },
            Hotbar = HotbarSlot.Grenade,
        };
        var held = items.SpawnHotbar(player, ItemType.Grenade, HotbarSlot.Grenade);

        var fire = new PlayerFireData
        {
            Origin = player.Position + Vector3.UnitY,
            Direction = Vector3.UnitX,
            RenderTick = 20f,
            Hotbar = HotbarSlot.Grenade,
        };
        grenades.ApplyThrow(player, fire, tick: 20);

        Assert.True(objects.TryGet(held.NetworkId, out var stack));
        Assert.Equal(3, stack.Weapon.CurrentAmmo);
        Assert.Equal(
            20u + (uint)WeaponConfig.Require(ItemType.Grenade).ReloadTicks,
            player.NextGrenadeThrowTick);
        var thrown = Assert.Single(objects.All, obj => obj.Type == ObjectType.Grenade);
        Assert.Equal(ObjectType.Grenade, thrown.Type);
        Assert.True(thrown.Has.HasFlag(NetComponents.Transform));
        Assert.Equal(player.Position + Vector3.UnitY, thrown.Transform.Position);

        grenades.ApplyThrow(player, fire, tick: 21);
        Assert.Single(objects.All, obj => obj.Type == ObjectType.Grenade);
        Assert.Equal(3, stack.Weapon.CurrentAmmo);

        fire.RenderTick = player.NextGrenadeThrowTick;
        grenades.ApplyThrow(player, fire, player.NextGrenadeThrowTick);
        Assert.Equal(2, objects.All.Count(obj => obj.Type == ObjectType.Grenade));
        Assert.Equal(2, stack.Weapon.CurrentAmmo);
    }

    [Fact]
    public void FuseStartsAtReleaseAndDetonatesEvenWithoutTerrainContact()
    {
        var server = new Server();
        var terrain = new ChunkMap();
        var objects = new ObjectReplication(server);
        var items = new ItemSystem(objects);
        var grenades = new GrenadeSystem(
            objects,
            items,
            new TerrainSystem(server, terrain),
            terrain);
        var player = new ServerPlayer
        {
            Id = 1,
            Move = new MoveState { Position = new Vector3(0f, 10f, 0f) },
            Hotbar = HotbarSlot.Grenade,
        };
        items.SpawnHotbar(player, ItemType.Grenade, HotbarSlot.Grenade);
        const uint releaseTick = 20;
        grenades.ApplyThrow(player, new PlayerFireData
        {
            Origin = player.Position,
            Direction = Vector3.UnitX,
            RenderTick = releaseTick,
            Hotbar = HotbarSlot.Grenade,
        }, releaseTick);

        grenades.Tick(
            1f / NetworkConfig.TickRate,
            releaseTick + GrenadeConfig.FuseTicks - 1,
            []);
        Assert.Single(objects.All, obj => obj.Type == ObjectType.Grenade);

        grenades.Tick(
            1f / NetworkConfig.TickRate,
            releaseTick + GrenadeConfig.FuseTicks,
            []);
        Assert.DoesNotContain(objects.All, obj => obj.Type == ObjectType.Grenade);
    }

    [Fact]
    public void BounceIsDampedAndRetainsMoreTangentialThanNormalSpeed()
    {
        var bounced = GrenadeSystem.BounceVelocity(
            new Vector3(10f, -10f, 0f),
            Vector3.UnitY);

        Assert.Equal(10f * GrenadeConfig.TangentialRetention, bounced.X, 4);
        Assert.Equal(10f * GrenadeConfig.GroundRestitution, bounced.Y, 4);
        Assert.Equal(0f, bounced.Z);
    }

    private static ServerPlayer PlayerAt(ushort id, float x, bool isMob = false)
        => new()
        {
            Id = id,
            IsMob = isMob,
            Move = new MoveState { Position = new Vector3(x, 0f, 0f) },
            Status = new ServerObject
            {
                Has = NetComponents.Health,
                Health = new HealthState { Current = 100, Max = 100 },
            },
        };
}
