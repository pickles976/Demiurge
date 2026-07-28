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
            => shape == EditShape.Sphere
                ? SphereDistance(offsetFromCentre, extent.X)
                : BoxDistance(offsetFromCentre, extent);

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
                                    EditMode mode, BlockType fill, EditShape editShape = EditShape.Box)
        {
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
                        float shape = ShapeDistance(world - centre, halfExtent, editShape);

                        int i = ChunkTransforms.LocalVoxelIndex(x, y - ChunkConstants.WorldMinY, z);

                        float existing = chunk[i].Distance;
                        float combined = mode == EditMode.Add
                            ? MathF.Min(existing, shape)
                            : MathF.Max(existing, -shape);

                        // No edit may open the world floor — see ChunkConstants.BedrockThickness.
                        combined = ChunkConstants.ClampToWorldFloor(y, combined);

                        var voxel = chunk[i];
                        voxel.Distance = combined;

                        // Density is the authority on what exists; material only labels it. Three
                        // cases, and the third is the one that matters: rock that was already
                        // solid keeps whatever it was, so an edit can't repaint a vein.
                        if (combined >= 0f) voxel.Material = BlockType.BlockType_Air;
                        else if (existing >= 0f) voxel.Material = fill;

                        chunk[i] = voxel;
                    }
                }
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
                                                          EditShape editShape = EditShape.Box)
        {
            var (low, high) = AffectedBounds(centre, halfExtent);

            var first = ChunkTransforms.ChunkAt(low.X, low.Z);
            var last = ChunkTransforms.ChunkAt(high.X, high.Z);

            for (int cz = first.z; cz <= last.z; cz++)
                for (int cx = first.x; cx <= last.x; cx++)
                    if (map.Get(new ChunkIndex { x = cx, z = cz }) is { } chunk)
                        ApplyBox(chunk, centre, halfExtent, mode, fill, editShape);

            return (new Vector3(low.X, low.Y, low.Z), new Vector3(high.X, high.Y, high.Z));
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
