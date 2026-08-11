using System.Numerics;

namespace Demiurge
{
    /// <summary>A square of 2^Level chunks spanning the world's full height. The quadtree is 2D; the
    /// column is expanded into <see cref="LodSection"/>s only once a node is settled as a leaf.</summary>
    public readonly record struct LodNode(int X, int Z, int Level);

    /// <summary>
    /// Decides which level of detail each part of the world is drawn at.
    ///
    /// THE CURRENCY IS PIXELS OF ERROR. A level-L box has cells of 2^L world units, so its surface can
    /// sit that far from where the field says it is; divided by distance and scaled by the lens, that
    /// is an error in pixels, and a node is split when its error exceeds
    /// <see cref="PixelErrorBudget"/>. Everything else this class used to decide falls out of that one
    /// number rather than being asked separately: zooming narrows the lens and so refines, terrain
    /// behind you subtends nothing and so does not, and the old distance table is what the formula
    /// evaluates to at the hip field of view. See <see cref="TerrainView"/> for why the table was
    /// already this rule with the field of view baked in.
    ///
    /// KEYED OFF THE ACTIVE OBSERVER. Normal play supplies the player's stable eye and facing; F3 and
    /// the editor supply their fly camera. Consequently the unconditional detail bubble and the
    /// view-weighted refinement move to the terrain actually being inspected.
    ///
    /// TWO THINGS OVERRIDE THE ERROR RULE, both by forcing a split the budget cannot deny:
    ///
    ///  - a node straddling the world edge, because a coarse box there reads chunks that will never
    ///    arrive and would retry forever;
    ///  - a node within <see cref="UnconditionalRadius"/> of the eye, because a pure error rule leaves
    ///    the ground behind you coarse and a 180-degree turn in a firefight is not a rare event.
    ///
    /// REFINEMENT IS BUDGETED, worst-error-first. Under a scope the error rule alone would happily ask
    /// for the whole aim cone at LOD 0 out to 600 m, and an estimate of how many boxes that is is not
    /// a frame budget. Splitting through a priority queue means the ceiling costs detail in the places
    /// that needed it least, instead of costing frame time — which is the same argument node ceilings
    /// carry in docs/BARITONE.md, for the same reason.
    /// </summary>
    public sealed class TerrainLod
    {
        /// <summary>
        /// Pixels of geometric error a leaf is allowed to subtend. THE tuning knob: it sharpens or
        /// coarsens every level at every field of view at once, which is the point of having a single
        /// currency.
        ///
        /// The old distance table worked out to 12.8 px (see <see cref="TerrainView"/>). This is half
        /// that, which doubles every level's radius — LOD 0 reaches 224 m at hip rather than 112 m —
        /// because the reported problem was never only about aiming: thin structures read wrong at
        /// ordinary engagement range too.
        ///
        /// HALVING IT IS CHEAPER THAN IT LOOKS, and the reason is worth keeping. Cost does not scale
        /// as the inverse square here, because a narrow lens has already refined its whole cone out to
        /// the world edge and cannot spend more. Measured across this range, the 3x case — the peak,
        /// see <see cref="MaxDesiredSections"/> — moves 6,150 to 6,234 sections, i.e. not at all,
        /// while hip goes 3,126 to 5,184. So the budget buys hip-fire quality almost for free and the
        /// WORST case is unchanged.
        ///
        /// Below about 4.8 that stops being true: hip becomes the peak instead of 3x, and by 3.2 it
        /// reaches the ceiling and starts costing detail somewhere. Do not go lower without raising
        /// <see cref="MaxDesiredSections"/> with it.
        /// </summary>
        public const float PixelErrorBudget = 6.4f;

        /// <summary>
        /// Ground within this many world units of the eye is LOD 0 whatever the view says.
        ///
        /// This is the deliberate special case, and it buys the one thing a pure error model gets
        /// wrong here: you turn faster than terrain can be meshed. Four chunks is about the radius
        /// inside which a turn is a fight rather than a walk.
        /// </summary>
        public const float UnconditionalRadius = 64f;

        /// <summary>
        /// Ceiling on the leaf sections one selection may ask for. Forced splits are exempt — denying
        /// those reintroduces the bugs they exist to prevent — so this governs error-driven
        /// refinement only.
        ///
        /// IT DOES NOT CURRENTLY BIND, and that is worth knowing before tuning it. Cost peaks in the
        /// MIDDLE of the magnification range rather than at the top, because zooming trades cone width
        /// for depth and depth is capped by a 1 km world — past about 3x the narrowing wins. At the
        /// current budget the range runs roughly 5,184 sections at hip to 6,234 at the 3x peak, so the
        /// ceiling has about 30% of headroom over anything reachable.
        ///
        /// It is therefore a guard against a bigger world, a lower <see cref="PixelErrorBudget"/>, or
        /// another LOD level — not something the current game reaches. TerrainLodTests keeps that
        /// claim honest by asserting the ceiling is NOT hit at any magnification, so halving the
        /// budget again fails a test rather than silently blurring what somebody is looking at.
        /// </summary>
        public const int MaxDesiredSections = 8192;

        /// <summary>Degrees the frustum is widened by for selection. See <see cref="TerrainView"/>.</summary>
        public const float FrustumMarginDegrees = 8f;

        /// <summary>Owned by the caller and reused, because this runs on view change rather than once.</summary>
        readonly PriorityQueue<LodNode, float> splittable = new();
        readonly List<LodNode> leaves = new();

        /// <summary>Leaf sections the last <see cref="CollectDesired"/> settled on, and whether the
        /// ceiling was what stopped it. Reported so a budget that is actually biting is visible rather
        /// than inferred from terrain that looks vaguely soft.</summary>
        public int LastSectionCount { get; private set; }
        public bool LastHitCeiling { get; private set; }

        /// <summary>
        /// Every box that should currently exist. Cleared and refilled.
        /// </summary>
        public void CollectDesired(in TerrainView view, HashSet<LodSection> desired)
        {
            desired.Clear();
            splittable.Clear();
            leaves.Clear();

            int sections = 0;
            LastHitCeiling = false;

            int chunksPerRoot = 1 << LodSection.MaxLevel;

            for (int rx = FloorDiv(WorldGen.MeshableMin.x, chunksPerRoot); rx <= FloorDiv(WorldGen.MeshableMax.x, chunksPerRoot); rx++)
                for (int rz = FloorDiv(WorldGen.MeshableMin.z, chunksPerRoot); rz <= FloorDiv(WorldGen.MeshableMax.z, chunksPerRoot); rz++)
                    Admit(new LodNode(rx, rz, LodSection.MaxLevel), view, ref sections);

            // Worst error first. A split replaces one node's column with four half-size ones, which is
            // 8x its section count, so the growth is 7x what the node currently contributes.
            while (splittable.TryPeek(out var node, out float priority))
            {
                int cost = 7 * LodSection.SectionsPerColumn(node.Level);

                if (!float.IsNegativeInfinity(priority) && sections + cost > MaxDesiredSections)
                {
                    LastHitCeiling = true;
                    break;
                }

                splittable.Dequeue();
                sections -= LodSection.SectionsPerColumn(node.Level);

                for (int dx = 0; dx < 2; dx++)
                    for (int dz = 0; dz < 2; dz++)
                        Admit(new LodNode(node.X * 2 + dx, node.Z * 2 + dz, node.Level - 1), view, ref sections);
            }

            // Whatever the budget stopped us splitting is a leaf as it stands.
            while (splittable.TryDequeue(out var node, out _)) leaves.Add(node);

            foreach (var node in leaves)
                for (int y = 0; y < LodSection.SectionsPerColumn(node.Level); y++)
                    desired.Add(new LodSection(node.X, y, node.Z, node.Level));

            LastSectionCount = sections;
        }

        /// <summary>
        /// Files one node as a settled leaf or as a split candidate, and accounts for its sections.
        /// Nodes with no meshable ground are dropped and cost nothing.
        /// </summary>
        void Admit(LodNode node, in TerrainView view, ref int sections)
        {
            int chunks = 1 << node.Level;
            int minChunkX = node.X * chunks, maxChunkX = minChunkX + chunks - 1;
            int minChunkZ = node.Z * chunks, maxChunkZ = minChunkZ + chunks - 1;

            bool anyMeshable = maxChunkX >= WorldGen.MeshableMin.x && minChunkX <= WorldGen.MeshableMax.x
                            && maxChunkZ >= WorldGen.MeshableMin.z && minChunkZ <= WorldGen.MeshableMax.z;

            if (!anyMeshable) return;

            bool allMeshable = minChunkX >= WorldGen.MeshableMin.x && maxChunkX <= WorldGen.MeshableMax.x
                            && minChunkZ >= WorldGen.MeshableMin.z && maxChunkZ <= WorldGen.MeshableMax.z;

            if (node.Level == 0)
            {
                if (!allMeshable) return;   // partly outside the world: nothing to draw
                leaves.Add(node);
                sections += LodSection.SectionsPerColumn(0);
                return;
            }

            sections += LodSection.SectionsPerColumn(node.Level);

            if (TrySplitPriority(node, view, allMeshable, minChunkX, minChunkZ, chunks, out float priority))
                splittable.Enqueue(node, priority);
            else
                leaves.Add(node);
        }

        /// <summary>
        /// Whether a node wants splitting and how badly. Priority is a MIN-heap key, so it is the
        /// negated pixel error and forced splits are negative infinity.
        /// </summary>
        static bool TrySplitPriority(
            LodNode node,
            in TerrainView view,
            bool allMeshable,
            int minChunkX,
            int minChunkZ,
            int chunks,
            out float priority)
        {
            priority = float.NegativeInfinity;

            if (!allMeshable) return true;      // straddles the world edge

            float minX = minChunkX * ChunkConstants.ChunkWidth;
            float minZ = minChunkZ * ChunkConstants.ChunkWidth;
            float size = chunks * ChunkConstants.ChunkWidth;

            float distance = GroundDistance(minX, minZ, size, view.Origin);

            if (distance < UnconditionalRadius) return true;

            var min = new Vector3(minX, ChunkConstants.WorldMinY, minZ);
            var max = new Vector3(minX + size, ChunkConstants.WorldMaxY, minZ + size);

            if (!view.Intersects(min, max)) return false;   // off screen subtends nothing

            // The node's own cells are what would stop being drawn if it split, so its own stride is
            // the error being priced — the same quantity the old table indexed by level.
            float error = view.PixelError(1 << node.Level, distance);

            if (error <= PixelErrorBudget) return false;

            priority = -error;
            return true;
        }

        /// <summary>Distance from the eye to the node's box on the ground plane. Height is ignored:
        /// the quadtree is 2D, and a column spans everything above and below anyway.</summary>
        static float GroundDistance(float minX, float minZ, float size, Vector3 eye)
        {
            float dx = MathF.Max(0f, MathF.Max(minX - eye.X, eye.X - (minX + size)));
            float dz = MathF.Max(0f, MathF.Max(minZ - eye.Z, eye.Z - (minZ + size)));

            return MathF.Sqrt(dx * dx + dz * dz);
        }

        static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;
    }
}
