using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Trees as things that can be killed and can fall.
    ///
    /// A tree dies in two ways and they are the same death: enough blast, or the ground going out
    /// from under it. Both leave a dead trunk, so this owns one notion of killing a tree and both
    /// causes go through it.
    ///
    /// Falling is the flag's fall, deliberately: a vertical drop to whatever the terrain now is,
    /// tracked against the map's edit version so digging is what re-asks the question. A toppling
    /// trunk would be a different and much larger thing — it would need an orientation on the wire,
    /// and a tree lying across ground is a shape nothing else in the game has.
    /// </summary>
    public sealed class TreeSystem
    {
        private sealed class Tree
        {
            public required ServerObject Object { get; init; }
            public float GroundY { get; set; }
            public float VerticalVelocity { get; set; }
            public bool Dead { get; set; }
        }

        private readonly ObjectReplication objects;
        private readonly ChunkMap terrain;
        private readonly List<Tree> trees = [];

        /// <summary>
        /// The trunks movement collides against, kept beside the trees themselves so the two cannot
        /// drift. The CLIENT builds the same structure from what it has been sent — see
        /// TreeColliders on why the shared movement step needs both ends to agree.
        /// </summary>
        public TreeColliders Colliders { get; } = new();
        private long observedTerrainVersion = long.MinValue;

        public TreeSystem(ObjectReplication objects, ChunkMap terrain)
        {
            this.objects = objects;
            this.terrain = terrain;
        }

        public ServerObject Spawn(Vector3 position, float yaw)
        {
            var obj = objects.Spawn(
                ObjectType.Tree,
                NetComponents.Transform | NetComponents.Health,
                position,
                tree =>
                {
                    tree.Transform.Yaw = yaw;
                    tree.Health = new HealthState
                    {
                        Current = TreePlacement.MaxHealth,
                        Max = TreePlacement.MaxHealth,
                    };
                });

            // Its own floor is where it stands. Resolving it now rather than on the first terrain
            // edit means a tree planted over a hole does not begin life falling.
            trees.Add(new Tree { Object = obj, GroundY = position.Y });
            Colliders.Add(position);
            return obj;
        }

        /// <summary>
        /// Blast damage. Trees take it and bullets they do not — a rifle round does not fell a tree,
        /// and a map where it does is a map where every treeline evaporates in the first minute.
        /// <see cref="WeaponSystem"/> excludes them on the projectile side.
        ///
        /// No line-of-sight test, unlike the one for men: the trunk IS the obstruction, and asking
        /// whether a blast can see it means asking whether it can see itself.
        /// </summary>
        public void ApplyBlast(Vector3 origin, in BlastProfile blast)
        {
            foreach (var tree in trees)
            {
                if (tree.Dead) continue;

                // Measured to the middle of the trunk rather than to its base, so a charge going off
                // level with a tree's waist is not judged by how far it is from its roots.
                var centre = tree.Object.Transform.Position
                    + Vector3.UnitY * (TreePlacement.TrunkHeight * 0.5f);
                float fraction = blast.DamageFraction(Vector3.Distance(origin, centre));
                if (fraction <= 0f) continue;

                int damage = Math.Max(1, (int)MathF.Round(TreePlacement.MaxHealth * fraction));
                Kill(tree, damage);
            }
        }

        public void Tick(float dt)
        {
            // Digging is what re-asks where the ground is. Re-resolving every tree every tick would
            // be hundreds of downward rays a tick for an answer that changes only when somebody
            // moves earth.
            long version = terrain.EditVersion;
            if (version != observedTerrainVersion)
            {
                observedTerrainVersion = version;
                foreach (var tree in trees)
                {
                    var position = tree.Object.Transform.Position;
                    float maxDistance = ChunkConstants.WorldMaxY - ChunkConstants.WorldMinY;
                    float floor = TerrainRaycast.Cast(
                            terrain,
                            position + Vector3.UnitY * 0.2f,
                            -Vector3.UnitY,
                            maxDistance)?.Point.Y
                        ?? ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness;

                    // Never raised by terrain built up under it, only dropped by terrain taken away.
                    float ground = MathF.Min(position.Y, floor);
                    if (ground < tree.GroundY - UndercutTolerance)
                    {
                        // Undermined. A tree with nothing under it is not a living tree, whatever
                        // its health said a moment ago.
                        tree.GroundY = ground;
                        Kill(tree, TreePlacement.MaxHealth);
                    }
                    else
                    {
                        tree.GroundY = ground;
                    }
                }
            }

            foreach (var tree in trees)
            {
                var position = tree.Object.Transform.Position;
                if (position.Y <= tree.GroundY + 0.001f)
                {
                    tree.VerticalVelocity = 0f;
                    continue;
                }

                tree.VerticalVelocity -= 9.81f * dt;
                float nextY = MathF.Max(tree.GroundY, position.Y + tree.VerticalVelocity * dt);
                if (nextY == position.Y) continue;

                tree.Object.Transform.Position = new Vector3(position.X, nextY, position.Z);
                tree.Object.Dirty |= NetComponents.Transform;
            }
        }

        /// <summary>
        /// How far the ground may drop under a tree before it counts as undermined. Terrain edits
        /// nearby nudge the resolved surface by a few centimetres without anybody having dug at the
        /// trunk, and a tree should not die because a shell landed twenty metres away.
        /// </summary>
        private const float UndercutTolerance = 0.35f;

        private static void Kill(Tree tree, int damage)
        {
            var health = tree.Object.Health;
            health.Current = damage >= health.Current ? (ushort)0 : (ushort)(health.Current - damage);
            tree.Object.Health = health;
            tree.Object.Dirty |= NetComponents.Health;

            // Zero health IS the dead tree. No second object, no despawn and respawn: the client
            // swaps the model it draws when the health it already replicates reaches nothing.
            if (health.Current == 0) tree.Dead = true;
        }
    }
}
