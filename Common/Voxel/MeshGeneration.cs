
using System.Numerics;

namespace Demiurge
{

    /// <summary>
    /// Chunk-local geometry. System.Numerics, not Stride: Common has no engine dependency, so the
    /// Client converts this into a vertex buffer.
    /// </summary>
    public class MeshData
    {
        public static readonly MeshData Empty = new();

        public Vector3[] Positions = [];
        public Vector3[] Normals = [];
        public int[] Indices = [];
    }

    public class ChunkMesher
    {

        /// <summary>
        /// Extra sample layers past the corners needed for positioning.
        ///
        /// 2, not 1. A chunk has to build vertices for the cell layer at local -1 as well as its
        /// own, or the quad straddling a chunk border has no fourth corner and every border comes
        /// out as a one-cell gap. Those cells use corner -1, and a central difference there reaches
        /// sample -2. A cell-corner gradient would allow 1.
        /// </summary>
        const int Apron = 2;

        // 16 cells span 17 corners — cell 15's far corner belongs to the +X neighbour.
        public const int ScratchWidth = ChunkConstants.ChunkWidth + 1 + 2 * Apron; // 21
        public const int ScratchHeight = ChunkConstants.ChunkHeight + 2 * Apron; // 132
        public const int ScratchVolume = ScratchWidth * ScratchWidth * ScratchHeight;

        /// <summary>
        /// Cells start one BEFORE the chunk. A chunk emits the quads for grid edges leaving its own
        /// points 0..15, and those quads reach back to cell -1 — so cell -1 needs a vertex, even
        /// though it duplicates one the -X neighbour also builds. Both compute it from the same
        /// samples, so the duplicate lands in the same place and the seam is watertight.
        /// </summary>
        const int CellMin = -1;

        public const int CellsX = ChunkConstants.ChunkWidth + 1;      // 17, cx -1..15
        public const int CellsZ = ChunkConstants.ChunkWidth + 1;      // 17
        public const int CellsY = ChunkConstants.ChunkHeight + 1;     // 129, cy -1..127
        const int CellCount = CellsX * CellsY * CellsZ;               // 37,281

        static int CellIndex(int cx, int cy, int cz)                  // same y-major convention
            => ((cy - CellMin) * CellsX * CellsZ) + ((cz - CellMin) * CellsX) + (cx - CellMin);

        /// Corner c of a cell = CornerOffsets[c], index encoded as x + 2y + 4z. CellEdges depends
        /// on that encoding, so the two tables can't be reordered independently.
        static readonly (int x, int y, int z)[] CornerOffsets =
        [
            (0,0,0), (1,0,0), (0,1,0), (1,1,0),
            (0,0,1), (1,0,1), (0,1,1), (1,1,1),
        ];

        /// The 12 edges as corner-index pairs: every pair differing in exactly one bit.
        static readonly (int a, int b)[] CellEdges =
        [
            (0,1), (2,3), (4,5), (6,7),   // along x
            (0,2), (1,3), (4,6), (5,7),   // along y
            (0,4), (1,5), (2,6), (3,7),   // along z
        ];

        /// One step along axis 0/1/2. Indexed the same way as QuadCellOffsets.
        static readonly (int x, int y, int z)[] AxisSteps = [(1,0,0), (0,1,0), (0,0,1)];

        /// For an edge leaving a cell's minimum corner along axis 0/1/2, the 4 cells sharing it as
        /// offsets from that cell. Cyclic order — hopping diagonally gives two bowtie triangles.
        ///
        /// All three rows must turn the same way or that axis's faces come out inside-out. The
        /// order is the right-handed succession of the two axes that aren't the edge's:
        /// x -> (y,z), y -> (z,x), z -> (x,y). Writing y's as (x,z) reversed it and inverted every
        /// horizontal face, which is most of a heightmap.
        static readonly (int x, int y, int z)[][] QuadCellOffsets =
        [
            [ (0,-1,-1), (0,0,-1), (0,0,0), (0,-1,0) ],   // edge along x, varying (y,z)
            [ (-1,0,-1), (-1,0,0), (0,0,0), (0,0,-1) ],   // edge along y, varying (z,x)
            [ (-1,-1,0), (0,-1,0), (0,0,0), (-1,0,0) ],   // edge along z, varying (x,y)
        ];

        /// Density at a CHUNK-LOCAL corner. The one place local -> scratch happens.
        static float DensityAt(Voxel[] scratch, int lx, int ly, int lz)
            => scratch[ScratchIndex(lx + Apron, ly + Apron, lz + Apron)].Density;

        /// Central differences at a chunk-local corner; the +/-1 past a chunk edge is what Apron
        /// buys. Not negated — density is positive in air, so this points away from solid already.
        static Vector3 GradientAt(Voxel[] scratch, int lx, int ly, int lz)
            => new Vector3(
                DensityAt(scratch, lx + 1, ly, lz) - DensityAt(scratch, lx - 1, ly, lz),
                DensityAt(scratch, lx, ly + 1, lz) - DensityAt(scratch, lx, ly - 1, lz),
                DensityAt(scratch, lx, ly, lz + 1) - DensityAt(scratch, lx, ly, lz - 1)) * 0.5f;

        /// <summary>
        /// Scratch coords -> flat index; the fill and the mesher must both go through it. Same
        /// y-major layout as <see cref="ChunkTransforms.LocalVoxelIndex"/>
        /// with ScratchWidth as the stride. ScratchHeight is absent because the y stride is one
        /// horizontal slab, W*W.
        /// </summary>
        static int ScratchIndex(int sx, int sy, int sz)
            => (sy * ScratchWidth * ScratchWidth) + (sz * ScratchWidth) + sx;


        /// <summary>
        /// Copies the chunk plus its apron into a flat buffer. False if a needed neighbour chunk
        /// isn't loaded, leaving the buffer holding stale data from whichever chunk it held before
        /// — don't read it unless this returned true.
        /// </summary>
        public static bool TryFillScratch(ChunkMap map, ChunkIndex index, Voxel[] scratch)
        {
            // World coords of scratch slot (0,0,0). The -Apron is why sx=0 reads the neighbour.
            (int chunkOriginX, int chunkOriginZ) = ChunkTransforms.ChunkOrigin(index);

            var originX = chunkOriginX - Apron;
            var originZ = chunkOriginZ - Apron;
            var originY = ChunkConstants.WorldMinY - Apron;

            for (var sy = 0; sy < ScratchHeight; sy++)
            {
                for (var sz = 0; sz < ScratchWidth; sz++)
                {
                    for (var sx = 0; sx < ScratchWidth; sx++)
                    {
                        if (!map.TryGetVoxel(originX + sx, originY + sy, originZ + sz, out var v)) return false;
                        scratch[ScratchIndex(sx, sy, sz)] = v;
                    }
                }
            }
            return true;
        }

        /// <summary>Surface nets over a filled scratch buffer. Positions are chunk-local.</summary>
        public static MeshData GenerateMeshFromSurfaceNet(Voxel[] scratch)
        {
            var cellVertex = new int[CellCount];
            Array.Fill(cellVertex, -1);

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();

            Span<float> d = stackalloc float[8];
            Span<int> quad = stackalloc int[4];

            // --- Pass 1: one vertex per cell the surface passes through ----
            // Starts at CellMin, so the -1 layer that border quads reach back into gets vertices.
            for (int cy = CellMin; cy < CellMin + CellsY; cy++)
            {
                for (int cz = CellMin; cz < CellMin + CellsZ; cz++)
                {
                    for (int cx = CellMin; cx < CellMin + CellsX; cx++)
                    {
                        // count the number of solid or non-solid neighbors
                        int solid = 0;
                        for (int c = 0; c < 8; c++)
                        {
                            var o = CornerOffsets[c];
                            d[c] = DensityAt(scratch, cx + o.x, cy + o.y, cz + o.z);
                            if (d[c] < 0f) solid++;
                        }
                        if (solid == 0 || solid == 8) continue;   // no sign change, no surface

                        var sumPos = Vector3.Zero;
                        var sumNormal = Vector3.Zero;
                        int crossings = 0;

                        // 
                        foreach (var (a, b) in CellEdges)
                        {
                            if ((d[a] < 0f) == (d[b] < 0f)) continue; // if both signs are the same, surface doesnt pass between the points defining this edge

                            float t = d[a] / (d[a] - d[b]);      // treat density as linear along the edge

                            var oa = CornerOffsets[a];
                            var ob = CornerOffsets[b];
                            int ax = cx + oa.x, ay = cy + oa.y, az = cz + oa.z;
                            int bx = cx + ob.x, by = cy + ob.y, bz = cz + ob.z;

                            sumPos += Vector3.Lerp(new Vector3(ax, ay, az), new Vector3(bx, by, bz), t);
                            sumNormal += Vector3.Lerp(GradientAt(scratch, ax, ay, az),
                                                        GradientAt(scratch, bx, by, bz), t);
                            crossings++;
                        }

                        cellVertex[CellIndex(cx, cy, cz)] = positions.Count;

                        // Surface nets is this line: the average of the crossings. Dual contouring
                        // replaces it with a QEF solve and changes nothing else here.
                        positions.Add(sumPos / crossings);

                        // A zero sum would make Normalize return NaN, which renders as invisible
                        // geometry rather than as an error.
                        normals.Add(sumNormal.LengthSquared() > 1e-12f
                            ? Vector3.Normalize(sumNormal)
                            : Vector3.UnitY);
                    }
                }
            }

            if (positions.Count == 0) return MeshData.Empty;

            // --- Pass 2: one quad per sign-changing edge ---
            // Iterates the grid points this chunk OWNS — local 0..15 horizontally, 0..127
            // vertically. Every world grid point is owned by exactly one chunk, so every quad is
            // emitted exactly once, and the ones on the border knit rather than being dropped.
            for (int py = 0; py < ChunkConstants.ChunkHeight; py++)
            {
                for (int pz = 0; pz < ChunkConstants.ChunkWidth; pz++)
                {
                    for (int px = 0; px < ChunkConstants.ChunkWidth; px++)
                    {
                        float dp = DensityAt(scratch, px, py, pz);

                        for (int axis = 0; axis < 3; axis++)
                        {
                            var step = AxisSteps[axis];
                            float dq = DensityAt(scratch, px + step.x, py + step.y, pz + step.z);
                            if ((dp < 0f) == (dq < 0f)) continue;

                            // The 4 cells sharing this edge. Their coords land in -1..15 (and
                            // -1..127 for y) by construction, so the range guard can't fire — it's
                            // kept because an out-of-range read here would be a silent seam rather
                            // than a crash.
                            bool complete = true;
                            for (int i = 0; i < 4; i++)
                            {
                                var o = QuadCellOffsets[axis][i];
                                int qx = px + o.x, qy = py + o.y, qz = pz + o.z;

                                if (qx < CellMin || qy < CellMin || qz < CellMin ||
                                    qx >= CellMin + CellsX || qy >= CellMin + CellsY || qz >= CellMin + CellsZ)
                                {
                                    complete = false;
                                    break;
                                }

                                quad[i] = cellVertex[CellIndex(qx, qy, qz)];
                                if (quad[i] < 0) { complete = false; break; }
                            }
                            if (!complete) continue;

                            // Same quad either way; which winding faces outward depends on which end
                            // of the edge is solid. Stride culls back faces with
                            // FrontFaceCounterClockwise = false, so the visible face is the one
                            // whose Cross(p1-p0, p2-p0) points AGAINST the outward normal —
                            // pinned by WindingMatchesTheOrientationStrideDraws.
                            if (dp < 0f)
                            {
                                indices.Add(quad[0]); indices.Add(quad[2]); indices.Add(quad[1]);
                                indices.Add(quad[0]); indices.Add(quad[3]); indices.Add(quad[2]);
                            }
                            else
                            {
                                indices.Add(quad[0]); indices.Add(quad[1]); indices.Add(quad[2]);
                                indices.Add(quad[0]); indices.Add(quad[2]); indices.Add(quad[3]);
                            }
                        }
                    }
                }
            }

            return new MeshData
            {
                Positions = [.. positions],
                Normals = [.. normals],
                Indices = [.. indices],
            };
        }
    }

}
