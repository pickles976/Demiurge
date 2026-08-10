using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

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
            NoCover,
            Vector3.Zero,
            [thrower, friendly, mob, outside],
            tick: 0,
            GrenadeConfig.Blast,
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

    /// <summary>
    /// The ragdoll shove has to reach the client to exist, and it reaches it by riding the same
    /// dirty bundle as the death — so the dirty bit is as much the behaviour here as the vector is.
    /// </summary>
    [Fact]
    public void ABlastShovesOnlyWhatItKilled()
    {
        var killed = PlayerAt(1, 3f);
        var wounded = PlayerAt(2, 7f);

        GrenadeSystem.ApplyBlastDamage(NoCover, Vector3.Zero, [killed, wounded], tick: 0, GrenadeConfig.Blast);

        Assert.True(killed.Status!.Dirty.HasFlag(NetComponents.Impulse));
        Assert.True(killed.Status.Impulse.Velocity.X > 0f, "thrown away from the blast");
        Assert.True(killed.Status.Impulse.Velocity.Y > 0f, "and lifted, the charge being below him");

        // Survivors keep walking; only a corpse reads an impulse, so sending one would be noise.
        Assert.False(wounded.Status!.Dirty.HasFlag(NetComponents.Impulse));
        Assert.Equal(Vector3.Zero, wounded.Status.Impulse.Velocity);
    }

    /// <summary>
    /// Scaling off health removed rather than off the blow would make a nearly-dead man barely move
    /// when a grenade goes off at his feet, which is the one case a player is most likely to see.
    /// </summary>
    [Fact]
    public void TheSameGrenadeThrowsAWoundedManAsFarAsAWholeOne()
    {
        var whole = PlayerAt(1, 3f);
        var nearlyDead = PlayerAt(2, 3f);
        nearlyDead.Status!.Health.Current = 1;

        GrenadeSystem.ApplyBlastDamage(NoCover, Vector3.Zero, [whole, nearlyDead], tick: 0, GrenadeConfig.Blast);

        Assert.Equal(0, whole.Status!.Health.Current);
        Assert.Equal(0, nearlyDead.Status.Health.Current);
        Assert.Equal(
            whole.Status.Impulse.Velocity.Length(),
            nearlyDead.Status.Impulse.Velocity.Length(),
            4);
    }

    [Fact]
    public void GrenadeStackCyclesForOneAndAHalfSecondsBetweenThrows()
    {
        var server = new NullNetServer();
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

        // Counted DOWN from the stack's capacity rather than against a literal: what this test is
        // about is the interval between throws, and retuning how many grenades a man carries should
        // not land as a failure here.
        int capacity = WeaponConfig.Require(ItemType.Grenade).MagazineCapacity;

        Assert.True(objects.TryGet(held.NetworkId, out var stack));
        Assert.Equal(capacity - 1, stack.Weapon.CurrentAmmo);
        Assert.Equal(
            20u + (uint)WeaponConfig.Require(ItemType.Grenade).ReloadTicks,
            player.NextGrenadeThrowTick);
        var thrown = Assert.Single(objects.All, obj => obj.Type == ObjectType.Grenade);
        Assert.Equal(ObjectType.Grenade, thrown.Type);
        Assert.True(thrown.Has.HasFlag(NetComponents.Transform));
        Assert.Equal(player.Position + Vector3.UnitY, thrown.Transform.Position);

        grenades.ApplyThrow(player, fire, tick: 21);
        Assert.Single(objects.All, obj => obj.Type == ObjectType.Grenade);
        Assert.Equal(capacity - 1, stack.Weapon.CurrentAmmo);

        fire.RenderTick = player.NextGrenadeThrowTick;
        grenades.ApplyThrow(player, fire, player.NextGrenadeThrowTick);
        Assert.Equal(2, objects.All.Count(obj => obj.Type == ObjectType.Grenade));
        Assert.Equal(capacity - 2, stack.Weapon.CurrentAmmo);
    }

    [Fact]
    public void FuseStartsAtReleaseAndDetonatesEvenWithoutTerrainContact()
    {
        var server = new NullNetServer();
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

    /// <summary>
    /// The wall is the whole point of a trench, and range alone used to shoot straight through it.
    /// Both men here are inside the LETHAL radius, so nothing but sight separates them.
    /// </summary>
    [Fact]
    public void ABlastDoesNotReachThroughAWall()
    {
        var terrain = Terrain(parapetCrest: 20f);
        var origin = new Vector3(2f, GroundHeight + 0.5f, 8f);
        var exposed = PlayerAt(1, new Vector3(0.5f, GroundHeight, 8f));
        var sheltered = PlayerAt(2, new Vector3(5.5f, GroundHeight, 8f));

        Assert.True(Vector3.Distance(origin, sheltered.Position) < GrenadeConfig.LethalRadius);

        GrenadeSystem.ApplyBlastDamage(terrain, origin, [exposed, sheltered], tick: 0, GrenadeConfig.Blast);

        Assert.Equal(0, exposed.Status!.Health.Current);
        Assert.Equal(100, sheltered.Status!.Health.Current);
        Assert.False(sheltered.Status.Dirty.HasFlag(NetComponents.Health));
    }

    /// <summary>
    /// Why three sight points rather than one. A metre of parapet covers this man's feet and his
    /// chest and not the top of his head, and a grenade bursting at head height over open ground
    /// on the far side can see exactly that much of him — which is enough.
    /// </summary>
    [Fact]
    public void AManWithHisHeadOverTheParapetIsNotSheltered()
    {
        var origin = new Vector3(2f, GroundHeight + GunConfig.HeadCenterHeight, 8f);
        var here = new Vector3(5.5f, GroundHeight, 8f);

        var peeking = PlayerAt(1, here);
        GrenadeSystem.ApplyBlastDamage(
            Terrain(parapetCrest: GroundHeight + 1f), origin, [peeking], tick: 0, GrenadeConfig.Blast);
        Assert.Equal(0, peeking.Status!.Health.Current);

        // The same man behind the same wall built a metre higher. Nothing else differs, so the
        // parapet is doing the work rather than the range: it is the head point being covered too.
        var down = PlayerAt(2, here);
        GrenadeSystem.ApplyBlastDamage(
            Terrain(parapetCrest: GroundHeight + 2f), origin, [down], tick: 0, GrenadeConfig.Blast);
        Assert.Equal(100, down.Status!.Health.Current);
    }

    /// <summary>
    /// A blast goes off ON the ground, so sighting from exactly the impact point means the first
    /// ankle-high fold of dirt between it and a man protects him completely. Rising half a metre is
    /// what stops that; a real parapet still has to work.
    /// </summary>
    [Fact]
    public void ALowLipDoesNotShelterAManButAParapetDoes()
    {
        // On the ground, right up against the near face of the lip — the worst case for sighting.
        var origin = new Vector3(3f, GroundHeight, 8f);
        var here = new Vector3(5.5f, GroundHeight, 8f);

        var behindLip = PlayerAt(1, here);
        GrenadeSystem.ApplyBlastDamage(
            Terrain(parapetCrest: GroundHeight + 0.45f), origin, [behindLip], tick: 0, GrenadeConfig.Blast);
        Assert.True(
            behindLip.Status!.Health.Current < 100,
            "half a metre of dirt is not cover from a grenade at its foot");

        // Chest high. Nothing the blast can see over from any height it plausibly bursts at.
        var behindParapet = PlayerAt(2, here);
        GrenadeSystem.ApplyBlastDamage(
            Terrain(parapetCrest: GroundHeight + 1.4f), origin, [behindParapet], tick: 0, GrenadeConfig.Blast);
        Assert.Equal(100, behindParapet.Status!.Health.Current);
    }

    private const float GroundHeight = 12f;

    /// <summary>Terrain that blocks nothing. An empty map is unloaded world, and a ray that leaves
    /// the loaded world is a miss, so every blast in the tests using it has clear sight.</summary>
    private static ChunkMap NoCover => new();

    /// <summary>
    /// Flat ground at <see cref="GroundHeight"/> with one wall standing on it, spanning x 3.5 to 4.5
    /// and rising to <paramref name="parapetCrest"/>. The field is the union of the two, which for
    /// signed distances is the smaller of them.
    /// </summary>
    private static ChunkMap Terrain(float parapetCrest)
    {
        var wallCentre = new Vector3(4f, (ChunkConstants.WorldMinY + parapetCrest) * 0.5f, 8f);
        var wallHalf = new Vector3(0.5f, (parapetCrest - ChunkConstants.WorldMinY) * 0.5f, 64f);

        var map = new ChunkMap();
        for (int cz = -1; cz <= 1; cz++)
            for (int cx = -1; cx <= 1; cx++)
            {
                var index = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(index);
                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    var column = ChunkTransforms.ColumnWorldPosition(index, ChunkTransforms.ColumnIndexOf(i));
                    int worldY = ChunkConstants.WorldMinY + ChunkTransforms.LocalYOf(i);
                    var at = new Vector3(column.X, worldY, column.Y);

                    float distance = ChunkConstants.ClampToWorldFloor(
                        worldY,
                        MathF.Min(worldY - GroundHeight, BoxDistance(at, wallCentre, wallHalf)));

                    var voxel = new Voxel { Distance = distance };
                    voxel.Material = ChunkGenerator.DensityToMaterial(voxel.Distance, distance);
                    chunk[i] = voxel;
                }
                map.Insert(chunk);
            }
        return map;
    }

    private static float BoxDistance(Vector3 point, Vector3 centre, Vector3 half)
    {
        var q = Vector3.Abs(point - centre) - half;
        return Vector3.Max(q, Vector3.Zero).Length()
             + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
    }

    private static ServerPlayer PlayerAt(ushort id, float x, bool isMob = false)
        => PlayerAt(id, new Vector3(x, 0f, 0f), isMob);

    private static ServerPlayer PlayerAt(ushort id, Vector3 position, bool isMob = false)
        => new()
        {
            Id = id,
            IsMob = isMob,
            Move = new MoveState { Position = position },
            Status = new ServerObject
            {
                Has = NetComponents.Health,
                Health = new HealthState { Current = 100, Max = 100 },
            },
        };
}
