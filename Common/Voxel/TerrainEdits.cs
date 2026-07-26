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
                                    EditMode mode, BlockType fill)
        {
            (int originX, int originZ) = ChunkTransforms.ChunkOrigin(chunk.index);

            // Local bounds of the affected region, clamped to the chunk. A box reaching past a
            // border affects the neighbour too — that's the caller's problem, not this function's.
            int minX = Math.Max(0, (int)MathF.Floor(centre.X - halfExtent.X) - Margin - originX);
            int maxX = Math.Min(ChunkConstants.ChunkWidth - 1, (int)MathF.Ceiling(centre.X + halfExtent.X) + Margin - originX);
            int minZ = Math.Max(0, (int)MathF.Floor(centre.Z - halfExtent.Z) - Margin - originZ);
            int maxZ = Math.Min(ChunkConstants.ChunkWidth - 1, (int)MathF.Ceiling(centre.Z + halfExtent.Z) + Margin - originZ);
            int minY = Math.Max(ChunkConstants.WorldMinY, (int)MathF.Floor(centre.Y - halfExtent.Y) - Margin);
            int maxY = Math.Min(ChunkConstants.WorldMaxY - 1, (int)MathF.Ceiling(centre.Y + halfExtent.Y) + Margin);

            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        var world = new Vector3(originX + x, y, originZ + z);
                        float shape = BoxDistance(world - centre, halfExtent);

                        int i = ChunkTransforms.LocalVoxelIndex(x, y - ChunkConstants.WorldMinY, z);

                        float existing = chunk.voxels[i].Distance;
                        float combined = mode == EditMode.Add
                            ? MathF.Min(existing, shape)
                            : MathF.Max(existing, -shape);

                        // No edit may open the world floor — see ChunkConstants.BedrockThickness.
                        combined = ChunkConstants.ClampToWorldFloor(y, combined);

                        chunk.voxels[i].Distance = combined;

                        // Density is the authority on what exists; material only labels it. Three
                        // cases, and the third is the one that matters: rock that was already
                        // solid keeps whatever it was, so an edit can't repaint a vein.
                        if (combined >= 0f) chunk.voxels[i].Material = BlockType.BlockType_Air;
                        else if (existing >= 0f) chunk.voxels[i].Material = fill;
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
