using System.Numerics;

// Additive and subtractive terrain edits.
//
// The whole idea: edit the FIELD, not the voxels. Writing a constant density into a box gives a
// zero gradient inside it (so every normal collapses) and puts the surface wherever that constant
// happens to interpolate against its neighbours. Instead write the signed distance to the shape and
// combine with the CSG operators — negative is solid, so union is min and difference is max.
//
// Applies past the shape's bounds on purpose. A voxel 0.5 outside a trench but 5 deep in rock must
// go from -5 to -0.5, because it is now half a unit from a trench wall rather than 5 from the
// terrain surface. Skip that and the walls sit a fraction of a voxel off with stale normals.

namespace Demiurge
{
    /// <summary>Which primitive an edit combines into the field.</summary>
    public enum EditShape
    {
        /// <summary>Axis-aligned box. For built things — walls, floors, anything meant to look cut.</summary>
        Box,
        /// <summary>Sphere. For dug things, where flat faces and right angles are the artefact.</summary>
        Sphere,
        /// <summary>A smooth ellipsoid whose surface is displaced by bounded world-space noise.</summary>
        Organic,
    }

    public enum EditMode
    {
        /// <summary>Union: solid where either the terrain or the shape is solid.</summary>
        Add,
        /// <summary>Difference: solid where the terrain is solid and the shape is not.</summary>
        Subtract,
    }

    public static class TerrainEdits
    {
        /// <summary>How far past the shape the field still has to be rewritten: one voxel for the
        /// edge crossing, one more for the central difference behind it.</summary>
        const int Margin = 2;

        /// <summary>
        /// Largest disconnected solid component a subtractive brush may discard. Sample count is
        /// only one eligibility check; boundary contact and interior depth protect real overhangs.
        /// </summary>
        public const int MaxDisconnectedSolidSamples = 4;

        /// <summary>A small component with a deeply interior sample may be narrow but is not merely
        /// an isosurface remnant. Only shallow fragments are eligible for automatic cleanup.</summary>
        public const float MaxDisconnectedSolidDepth = 0.5f;

        /// <summary>Bounds stack usage and prevents large debug/building edits from doing a local
        /// cleanup whose boundary no longer means "near this edit". A normal dig scans 343 samples.</summary>
        const int MaxCleanupSamples = 4096;

        /// <summary>
        /// The inclusive world voxel range an edit rewrites — the shape's extent, rounded out to
        /// whole voxels, plus the <see cref="Margin"/>.
        ///
        /// One definition, used both to drive the write loop and to report what changed, because
        /// they must not disagree. They did: the reported range was once computed straight off the
        /// shape without rounding out, which under-reported it by up to a voxel — enough for the
        /// client to skip re-meshing a section at the edge of a dig and leave stale triangles
        /// hanging in the air.
        /// </summary>
        public static ((int X, int Y, int Z) Low, (int X, int Y, int Z) High)
            AffectedBounds(Vector3 centre, Vector3 halfExtent)
        {
            var min = centre - halfExtent;
            var max = centre + halfExtent;

            return (((int)MathF.Floor(min.X) - Margin, (int)MathF.Floor(min.Y) - Margin, (int)MathF.Floor(min.Z) - Margin),
                    ((int)MathF.Ceiling(max.X) + Margin, (int)MathF.Ceiling(max.Y) + Margin, (int)MathF.Ceiling(max.Z) + Margin));
        }

        /// <summary>Signed distance to a sphere. Negative inside, exact outside, smooth everywhere —
        /// which is the point: it has no faces to leave flat and no edges to leave sharp.</summary>
        public static float SphereDistance(Vector3 offsetFromCentre, float radius)
            => offsetFromCentre.Length() - radius;

        /// <summary>Distance to whichever primitive an edit is using. <paramref name="extent"/> is a
        /// half-extent for a box and a radius (its X) for a sphere.</summary>
        public static float ShapeDistance(Vector3 offsetFromCentre, Vector3 extent, EditShape shape)
            => ShapeDistance(offsetFromCentre, extent, shape, offsetFromCentre);

        /// <summary>
        /// Shape distance with an explicit world position. Only the organic brush uses world
        /// position, which keeps overlapping dabs on one coherent noise field instead of stamping
        /// the same local deformation repeatedly.
        /// </summary>
        public static float ShapeDistance(
            Vector3 offsetFromCentre,
            Vector3 extent,
            EditShape shape,
            Vector3 worldPosition)
            => shape switch
            {
                EditShape.Sphere => SphereDistance(offsetFromCentre, extent.X),
                EditShape.Box => BoxDistance(offsetFromCentre, extent),
                EditShape.Organic => OrganicDistance(offsetFromCentre, extent, worldPosition),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown edit shape"),
            };

        private static float OrganicDistance(
            Vector3 offsetFromCentre,
            Vector3 extent,
            Vector3 worldPosition)
        {
            var radius = Vector3.Max(Vector3.Abs(extent), new Vector3(0.1f));
            var normalized = offsetFromCentre / radius;
            float k0 = normalized.Length();
            float k1 = (offsetFromCentre / (radius * radius)).Length();
            float ellipsoid = k1 <= 1e-6f
                ? -MathF.Min(radius.X, MathF.Min(radius.Y, radius.Z))
                : k0 * (k0 - 1f) / k1;

            float minimumRadius = MathF.Min(radius.X, MathF.Min(radius.Y, radius.Z));
            float wavelength = MathF.Max(0.75f, minimumRadius * 0.8f);
            float amplitude = MathF.Min(0.75f, minimumRadius * 0.3f);
            Vector3 p = worldPosition / wavelength;
            float noise = (ValueNoise(p) + 0.5f * ValueNoise(p * 2.03f + new Vector3(17.1f))) / 1.5f;
            return ellipsoid + noise * amplitude;
        }

        private static float ValueNoise(Vector3 point)
        {
            int x = (int)MathF.Floor(point.X);
            int y = (int)MathF.Floor(point.Y);
            int z = (int)MathF.Floor(point.Z);
            float tx = Fade(point.X - x);
            float ty = Fade(point.Y - y);
            float tz = Fade(point.Z - z);

            float x00 = Lerp(Hash(x, y, z), Hash(x + 1, y, z), tx);
            float x10 = Lerp(Hash(x, y + 1, z), Hash(x + 1, y + 1, z), tx);
            float x01 = Lerp(Hash(x, y, z + 1), Hash(x + 1, y, z + 1), tx);
            float x11 = Lerp(Hash(x, y + 1, z + 1), Hash(x + 1, y + 1, z + 1), tx);
            return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
        }

        private static float Hash(int x, int y, int z)
        {
            uint value = (uint)x * 0x8da6b343u
                       ^ (uint)y * 0xd8163841u
                       ^ (uint)z * 0xcb1ab31fu
                       ^ 0x9e3779b9u;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return (value & 0x00ffffffu) * (2f / 0x00ffffffu) - 1f;
        }

        private static float Fade(float value)
            => value * value * value * (value * (value * 6f - 15f) + 10f);

        private static float Lerp(float a, float b, float amount) => a + (b - a) * amount;

        /// <summary>Signed distance to an axis-aligned box. Negative inside, exact outside.</summary>
        public static float BoxDistance(Vector3 offsetFromCentre, Vector3 halfExtent)
        {
            Vector3 q = Vector3.Abs(offsetFromCentre) - halfExtent;

            return Vector3.Max(q, Vector3.Zero).Length()
                 + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
        }

        /// <summary>
        /// Combines a world-space box into one chunk's density field. `fill` labels voxels that
        /// become solid; it is ignored when subtracting.
        /// </summary>
        public static void ApplyBox(TerrainChunk chunk, Vector3 centre, Vector3 halfExtent,
                                    EditMode mode, BlockType fill, EditShape editShape = EditShape.Box,
                                    float strength = 1f)
            => Apply(
                chunk, centre, halfExtent, mode, fill, editShape, strength,
                autoTerrainMaterial: true,
                repaintExistingSolid: false);

        /// <summary>
        /// Adds one editor block cell to the sampled field.
        ///
        /// The centre is an integer SDF sample and the faces lie half a voxel away, so the stamp has
        /// one negative interior sample. This is the same field operation as a generated wall,
        /// reduced to a 1x1x1 volume. Unlike a terrain union, an authored block owns the material
        /// inside its volume, including samples that overlap existing terrain.
        /// </summary>
        public static void ApplyBlockCell(TerrainChunk chunk, Vector3 centre, BlockType fill)
            => Apply(
                chunk, centre, new Vector3(0.5f), EditMode.Add, fill, EditShape.Box, 1f,
                autoTerrainMaterial: false,
                repaintExistingSolid: true);

        private static void Apply(
            TerrainChunk chunk,
            Vector3 centre,
            Vector3 halfExtent,
            EditMode mode,
            BlockType fill,
            EditShape editShape,
            float strength,
            bool autoTerrainMaterial,
            bool repaintExistingSolid)
        {
            strength = Math.Clamp(strength <= 0f ? 1f : strength, 0f, 1f);
            (int originX, int originZ) = ChunkTransforms.ChunkOrigin(chunk.index);
            var (low, high) = AffectedBounds(centre, halfExtent);

            // Local bounds of the affected region, clamped to the chunk. A box reaching past a
            // border affects the neighbour too — that's the caller's problem, not this function's.
            int minX = Math.Max(0, low.X - originX);
            int maxX = Math.Min(ChunkConstants.ChunkWidth - 1, high.X - originX);
            int minZ = Math.Max(0, low.Z - originZ);
            int maxZ = Math.Min(ChunkConstants.ChunkWidth - 1, high.Z - originZ);
            int minY = Math.Max(ChunkConstants.WorldMinY, low.Y);
            int maxY = Math.Min(ChunkConstants.WorldMaxY - 1, high.Y);

            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        var world = new Vector3(originX + x, y, originZ + z);
                        float shape = ShapeDistance(world - centre, halfExtent, editShape, world);

                        int i = ChunkTransforms.LocalVoxelIndex(x, y - ChunkConstants.WorldMinY, z);

                        float existing = chunk[i].Distance;
                        float full = mode == EditMode.Add
                            ? MathF.Min(existing, shape)
                            : MathF.Max(existing, -shape);

                        float combined = strength >= 1f ? full : Partial(existing, full, halfExtent, mode, strength);

                        // No edit may open the world floor — see ChunkConstants.BedrockThickness.
                        combined = ChunkConstants.ClampToWorldFloor(y, combined);

                        var voxel = chunk[i];
                        voxel.Distance = combined;

                        // Density is the authority on what exists; material only labels it. Three
                        // cases, and the third is the one that matters: rock that was already
                        // solid keeps whatever it was, so an edit can't repaint a vein.
                        if (combined >= 0f) voxel.Material = BlockType.BlockType_Air;
                        else if (existing >= 0f || repaintExistingSolid && shape < 0f)
                        {
                            // Grass is the auto-surface terrain fill. Classify the CSG surface from
                            // its local gradient so authored hills obey the same visual walkability
                            // rule as generated terrain: walkable faces are grass and steeper faces
                            // are stone.
                            voxel.Material = autoTerrainMaterial && fill == BlockType.BlockType_Grass
                                ? AutoTerrainMaterial(
                                    voxel.Distance,
                                    shape,
                                    world,
                                    world - centre,
                                    halfExtent,
                                    editShape)
                                : fill;
                        }

                        chunk[i] = voxel;
                    }
                }
            }
        }

        private static BlockType AutoTerrainMaterial(
            float storedDistance,
            float shapeDistance,
            Vector3 world,
            Vector3 offset,
            Vector3 extent,
            EditShape shape)
        {
            const float gradientStep = 0.25f;
            float dx = ShapeDistance(
                           offset + Vector3.UnitX * gradientStep,
                           extent,
                           shape,
                           world + Vector3.UnitX * gradientStep)
                     - ShapeDistance(
                           offset - Vector3.UnitX * gradientStep,
                           extent,
                           shape,
                           world - Vector3.UnitX * gradientStep);
            float dy = ShapeDistance(
                           offset + Vector3.UnitY * gradientStep,
                           extent,
                           shape,
                           world + Vector3.UnitY * gradientStep)
                     - ShapeDistance(
                           offset - Vector3.UnitY * gradientStep,
                           extent,
                           shape,
                           world - Vector3.UnitY * gradientStep);
            float dz = ShapeDistance(
                           offset + Vector3.UnitZ * gradientStep,
                           extent,
                           shape,
                           world + Vector3.UnitZ * gradientStep)
                     - ShapeDistance(
                           offset - Vector3.UnitZ * gradientStep,
                           extent,
                           shape,
                           world - Vector3.UnitZ * gradientStep);

            float horizontal = MathF.Sqrt(dx * dx + dz * dz);
            float slope = MathF.Abs(dy) <= 1e-6f ? float.PositiveInfinity : horizontal / MathF.Abs(dy);
            return ChunkGenerator.DensityToMaterial(storedDistance, shapeDistance, slope);
        }

        static float Partial(float existing, float full, Vector3 extent, EditMode mode, float strength)
        {
            if (mode == EditMode.Add)
            {
                if (full >= existing) return existing;
                float step = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) * 2f * strength;
                return MathF.Max(full, existing - step);
            }
            else
            {
                if (full <= existing) return existing;
                float step = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) * 2f * strength;
                return MathF.Min(full, existing + step);
            }
        }

        // ---- Debug shapes ----
        //
        // The wall and the trench are the SAME box — down the middle of a chunk, running along Z,
        // 2 wide in X, from WorldMinY up 32. Only the operator differs.

        const float DebugHeight = 32f;
        const float DebugWidth = 2f;

        static (Vector3 centre, Vector3 halfExtent) MiddleBox(ChunkIndex index)
        {
            (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);
            const float half = ChunkConstants.ChunkWidth / 2f;

            return (new Vector3(originX + half, ChunkConstants.WorldMinY + DebugHeight / 2f, originZ + half),
                    new Vector3(DebugWidth / 2f, DebugHeight / 2f, half));
        }

        /// <summary>
        /// The same edit across every chunk it touches, and the entry point anything gameplay-side
        /// should use.
        ///
        /// The per-chunk <see cref="ApplyBox(TerrainChunk, Vector3, Vector3, EditMode, BlockType)"/>
        /// clamps to its own chunk and leaves the rest to the caller — fine for a whole-chunk debug
        /// wall, wrong for a dig, which lands on a chunk border as often as anywhere else and would
        /// otherwise be carved out on one side of the seam and left solid on the other.
        ///
        /// The <see cref="Margin"/> is included in the span for the same reason it exists inside
        /// ApplyBox: a neighbouring chunk with no part of the SHAPE in it can still hold grid points
        /// whose distance the shape changes.
        ///
        /// Returns the world-space bounds actually touched, so a caller can mark exactly that much
        /// for re-meshing rather than guessing.
        /// </summary>
        public static (Vector3 Min, Vector3 Max) ApplyBox(ChunkMap map, Vector3 centre, Vector3 halfExtent,
                                                          EditMode mode, BlockType fill,
                                                          EditShape editShape = EditShape.Box,
                                                          float strength = 1f)
        {
            var (low, high) = AffectedBounds(centre, halfExtent);

            var first = ChunkTransforms.ChunkAt(low.X, low.Z);
            var last = ChunkTransforms.ChunkAt(high.X, high.Z);

            for (int cz = first.z; cz <= last.z; cz++)
                for (int cx = first.x; cx <= last.x; cx++)
                    if (map.Get(new ChunkIndex { x = cx, z = cz }) is { } chunk)
                        ApplyBox(chunk, centre, halfExtent, mode, fill, editShape, strength);

            // Small subtractive brushes can leave one or two negative samples detached from the
            // terrain. Surface nets correctly reconstructs those samples as a tiny closed mesh.
            // Remove them from the shared field so rendering, raycasts, and collision still agree.
            if (mode == EditMode.Subtract && editShape is EditShape.Sphere or EditShape.Organic)
                CullTinySolidComponents(map, low, high, MaxDisconnectedSolidSamples);

            return (new Vector3(low.X, low.Y, low.Z), new Vector3(high.X, high.Y, high.Z));
        }

        /// <summary>
        /// Removes tiny, shallow face-connected solid components fully enclosed by a local scan
        /// box. Components touching the box boundary are protected because they may connect to
        /// terrain outside it, and components with a deeply negative sample are protected because
        /// they contain meaningful interior volume. Returns the number of solid lattice samples
        /// changed to air.
        ///
        /// This intentionally uses six-neighbour connectivity. An edge- or corner-only attachment
        /// is not robust at the field's sample rate and is itself a common source of tiny ambiguous
        /// surface-net geometry.
        /// </summary>
        public static int CullTinySolidComponents(
            ChunkMap map,
            (int X, int Y, int Z) low,
            (int X, int Y, int Z) high,
            int maxComponentSamples = MaxDisconnectedSolidSamples)
        {
            int minY = Math.Max(low.Y, ChunkConstants.WorldMinY);
            int maxY = Math.Min(high.Y, ChunkConstants.WorldMaxY - 1);
            int widthX = high.X - low.X + 1;
            int widthY = maxY - minY + 1;
            int widthZ = high.Z - low.Z + 1;

            if (maxComponentSamples < 1 || widthX < 1 || widthY < 1 || widthZ < 1) return 0;

            int volume = checked(widthX * widthY * widthZ);
            if (volume > MaxCleanupSamples) return 0;

            Span<byte> state = stackalloc byte[volume]; // 0 air, 1 unvisited solid, 2 visited solid
            Span<sbyte> density = stackalloc sbyte[volume];
            Span<int> queue = stackalloc int[volume];

            for (int ly = 0; ly < widthY; ly++)
                for (int lz = 0; lz < widthZ; lz++)
                    for (int lx = 0; lx < widthX; lx++)
                    {
                        int index = Index(lx, ly, lz, widthX, widthZ);
                        if (!map.TryGetVoxel(low.X + lx, minY + ly, low.Z + lz, out var voxel))
                            return 0; // An incomplete neighborhood is unsafe to classify.
                        density[index] = voxel.Density;
                        if (voxel.Distance < 0f) state[index] = 1;
                    }

            int removed = 0;

            for (int start = 0; start < volume; start++)
            {
                if (state[start] != 1) continue;

                int head = 0;
                int tail = 0;
                queue[tail++] = start;
                state[start] = 2;
                bool touchesBoundary = false;
                bool hasDeepInterior = false;

                while (head < tail)
                {
                    int index = queue[head++];
                    Decode(index, widthX, widthZ, out int lx, out int ly, out int lz);
                    if (density[index] * Voxel.InverseScale < -MaxDisconnectedSolidDepth)
                        hasDeepInterior = true;

                    if (lx == 0 || lx == widthX - 1 ||
                        ly == 0 || ly == widthY - 1 ||
                        lz == 0 || lz == widthZ - 1)
                        touchesBoundary = true;

                    if (lx > 0) EnqueueSolid(index - 1, state, queue, ref tail);
                    if (lx + 1 < widthX) EnqueueSolid(index + 1, state, queue, ref tail);
                    if (lz > 0) EnqueueSolid(index - widthX, state, queue, ref tail);
                    if (lz + 1 < widthZ) EnqueueSolid(index + widthX, state, queue, ref tail);
                    int layer = widthX * widthZ;
                    if (ly > 0) EnqueueSolid(index - layer, state, queue, ref tail);
                    if (ly + 1 < widthY) EnqueueSolid(index + layer, state, queue, ref tail);
                }

                if (touchesBoundary || hasDeepInterior || tail > maxComponentSamples) continue;

                for (int i = 0; i < tail; i++)
                {
                    Decode(queue[i], widthX, widthZ, out int lx, out int ly, out int lz);
                    int worldX = low.X + lx;
                    int worldY = minY + ly;
                    int worldZ = low.Z + lz;
                    var chunk = map.Get(ChunkTransforms.ChunkAt(worldX, worldZ))!;
                    int voxelIndex = ChunkTransforms.WorldVoxelIndex(worldX, worldY, worldZ);
                    var voxel = chunk[voxelIndex];

                    // Mirror the old near-surface distance instead of saturating it. All-positive
                    // samples emit no geometry, while the gentle magnitude preserves useful
                    // gradients for any retained surface nearby.
                    voxel.Distance = MathF.Max(Voxel.InverseScale, -voxel.Distance);
                    voxel.Material = BlockType.BlockType_Air;
                    chunk[voxelIndex] = voxel;
                    removed++;
                }
            }

            return removed;
        }

        static int Index(int x, int y, int z, int widthX, int widthZ)
            => (y * widthZ + z) * widthX + x;

        static void Decode(int index, int widthX, int widthZ, out int x, out int y, out int z)
        {
            x = index % widthX;
            int yz = index / widthX;
            z = yz % widthZ;
            y = yz / widthZ;
        }

        static void EnqueueSolid(int index, Span<byte> state, Span<int> queue, ref int tail)
        {
            if (state[index] != 1) return;
            state[index] = 2;
            queue[tail++] = index;
        }

        /// <summary>A wall down the middle of the chunk, 2 wide, 32 tall from WorldMinY.</summary>
        public static void AddWall(TerrainChunk chunk, BlockType fill = BlockType.BlockType_Stone)
        {
            var (centre, halfExtent) = MiddleBox(chunk.index);
            ApplyBox(chunk, centre, halfExtent, EditMode.Add, fill);
        }

        /// <summary>The same volume carved out instead of filled in.</summary>
        public static void CarveTrench(TerrainChunk chunk)
        {
            var (centre, halfExtent) = MiddleBox(chunk.index);
            ApplyBox(chunk, centre, halfExtent, EditMode.Subtract, BlockType.BlockType_Air);
        }
    }
}
