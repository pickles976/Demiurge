
using System.Numerics;

namespace Demiurge
{

    /// <summary>A contiguous run of <see cref="MeshData.Indices"/> that shares one material.</summary>
    public readonly record struct Submesh(BlockType Material, int Start, int Count);

    /// <summary>
    /// Chunk-local geometry. System.Numerics, not Stride: Common has no engine dependency, so the
    /// Client converts this into a vertex buffer.
    ///
    /// Indices are grouped by material and <see cref="Submeshes"/> names the ranges, so one vertex
    /// buffer plus one index buffer can be drawn as several meshes with different textures.
    /// </summary>
    public class MeshData
    {
        public static readonly MeshData Empty = new();

        public Vector3[] Positions = [];
        public Vector3[] Normals = [];
        public int[] Indices = [];
        public Submesh[] Submeshes = [];
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

        /// <summary>
        /// How far outside its own bounds a section reads. A voxel change within this many voxels of
        /// a section's box invalidates that section's mesh even though none of its own voxels moved.
        /// </summary>
        public const int MeshDependencyRadius = Apron;

        /// <summary>
        /// Every section whose mesh depends on a changed world-voxel box — more than the sections the
        /// box sits in, because meshing reads an apron past a section's own bounds.
        ///
        /// Exact rather than conservative, and that's the point of having sections at all: a
        /// shovel-sized dig should invalidate two or three sections, not a 3x3 of 128-tall columns.
        /// Overlap is tested per candidate rather than derived in closed form because an interval test
        /// is much easier to confirm correct than the equivalent floor arithmetic.
        /// </summary>
        public static void CollectDependentSections(
            int minX, int minY, int minZ, int maxX, int maxY, int maxZ, ICollection<SectionIndex> into)
        {
            const int width = ChunkConstants.ChunkWidth;

            // A chunk two steps outside the box can't reach it: the apron is only Apron voxels wide.
            ChunkIndex first = ChunkTransforms.ChunkAt(minX, minZ);
            ChunkIndex last = ChunkTransforms.ChunkAt(maxX, maxZ);

            for (int cx = first.x - 1; cx <= last.x + 1; cx++)
            {
                for (int cz = first.z - 1; cz <= last.z + 1; cz++)
                {
                    (int originX, int originZ) = ChunkTransforms.ChunkOrigin(new ChunkIndex { x = cx, z = cz });

                    if (maxX < originX - Apron || minX > originX + width + Apron) continue;
                    if (maxZ < originZ - Apron || minZ > originZ + width + Apron) continue;

                    for (int sy = 0; sy < ChunkConstants.SectionsPerChunk; sy++)
                    {
                        int baseY = ChunkConstants.WorldMinY + sy * ChunkConstants.SectionHeight;
                        if (maxY < baseY - Apron || minY > baseY + ChunkConstants.SectionHeight + Apron) continue;

                        into.Add(new SectionIndex(cx, sy, cz));
                    }
                }
            }
        }

        // 16 cells span 17 corners — cell 15's far corner belongs to the next section along.
        // Cubic now: the vertical axis is a section, not the whole 128-tall column.
        public const int ScratchWidth = ChunkConstants.ChunkWidth + 1 + 2 * Apron; // 21
        public const int ScratchVolume = ScratchWidth * ScratchWidth * ScratchWidth;

        /// <summary>
        /// Cells start one BEFORE the section. A section emits the quads for grid edges leaving its
        /// own points 0..15, and those quads reach back to cell -1 — so cell -1 needs a vertex, even
        /// though it duplicates one the neighbouring section also builds. Both compute it from the
        /// same samples, so the duplicate lands in the same place and the seam is watertight. That
        /// now applies to the VERTICAL seam between sections exactly as it does to the horizontal
        /// seam between chunks; the two cases are the same case.
        /// </summary>
        const int CellMin = -1;

        // Cubic: 17 cells per axis, coords -1..15.
        public const int CellsPerAxis = ChunkConstants.ChunkWidth + 1;
        const int CellCount = CellsPerAxis * CellsPerAxis * CellsPerAxis;   // 4,913

        static int CellIndex(int cx, int cy, int cz)                        // same y-major convention
            => ((cy - CellMin) * CellsPerAxis * CellsPerAxis) + ((cz - CellMin) * CellsPerAxis) + (cx - CellMin);

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
        static float DensityAt(Sample[] scratch, int lx, int ly, int lz)
            => scratch[ScratchIndex(lx + Apron, ly + Apron, lz + Apron)].Distance;

        /// Material at a chunk-local corner. Only meaningful where the density is negative.
        static BlockType MaterialAt(Sample[] scratch, int lx, int ly, int lz)
            => scratch[ScratchIndex(lx + Apron, ly + Apron, lz + Apron)].Material;

        /// BlockType is contiguous from 0 (it's the wire protocol, append-only), so it indexes an
        /// array directly.
        static readonly int MaterialCount = Enum.GetValues<BlockType>().Length;

        /// Central differences at a chunk-local corner; the +/-1 past a chunk edge is what Apron
        /// buys. Not negated — density is positive in air, so this points away from solid already.
        static Vector3 GradientAt(Sample[] scratch, int lx, int ly, int lz)
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
        /// isn't loaded, in which case nothing is written — the buffer still holds whatever chunk it
        /// held before, so don't read it unless this returned true.
        ///
        /// <see cref="ChunkMap.TryGetVoxel"/> is the reference for a single world lookup; this is the
        /// bulk path and deliberately hoists the per-voxel work out of the inner loop. Both still go
        /// through <see cref="ChunkTransforms"/> for the actual arithmetic.
        /// </summary>
        public static bool TryFillScratch(ChunkMap map, SectionIndex section, Sample[] scratch)
        {
            // World coords of scratch slot (0,0,0). The -Apron is why sx=0 reads the neighbour.
            (int chunkOriginX, int chunkOriginZ) = ChunkTransforms.ChunkOrigin(section.Chunk);

            var originX = chunkOriginX - Apron;
            var originZ = chunkOriginZ - Apron;

            // Vertically the apron reaches into the section above and below, which live in the SAME
            // chunk array — so unlike the horizontal case it needs no extra lookup, just a world Y.
            var originY = section.BaseY - Apron;

            // Resolve the owning chunk once per (x,z) column rather than once per voxel: ChunkIndex
            // is 2D, so a vertical column cannot cross into another chunk. ~441 dictionary lookups
            // instead of ~58k. Doubles as the presence check, so a missing neighbour bails out
            // before anything is written.
            var columns = new TerrainChunk[ScratchWidth * ScratchWidth];

            for (int sz = 0; sz < ScratchWidth; sz++)
            {
                for (int sx = 0; sx < ScratchWidth; sx++)
                {
                    var chunk = map.Get(ChunkTransforms.ChunkAt(originX + sx, originZ + sz));
                    if (chunk is null) return false;

                    columns[sz * ScratchWidth + sx] = chunk;
                }
            }

            for (int sy = 0; sy < ScratchWidth; sy++)
            {
                int worldY = originY + sy;

                // Outside the world vertically there is no chunk and never will be, so a whole slab
                // is one sentinel: below is solid so no surface forms, above is air.
                if (worldY < ChunkConstants.WorldMinY || worldY >= ChunkConstants.WorldMaxY)
                {
                    var sentinel = Sample.From(worldY < ChunkConstants.WorldMinY ? Voxel.OutsideBelow : Voxel.OutsideAbove);
                    scratch.AsSpan(sy * ScratchWidth * ScratchWidth, ScratchWidth * ScratchWidth).Fill(sentinel);
                    continue;
                }

                for (int sz = 0; sz < ScratchWidth; sz++)
                {
                    for (int sx = 0; sx < ScratchWidth; sx++)
                    {
                        var chunk = columns[sz * ScratchWidth + sx];
                        int voxel = ChunkTransforms.WorldVoxelIndex(originX + sx, worldY, originZ + sz);

                        scratch[ScratchIndex(sx, sy, sz)] = Sample.From(chunk.voxels[voxel]);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Fills the scratch buffer for a box at any level of detail. Level 0 delegates to the fast path
        /// above; coarser levels sample every Stride-th voxel and BOX FILTER the block between samples.
        ///
        /// Filtering rather than point sampling is not optional. Taking every 4th voxel lets a thin ridge
        /// fall between samples and flip sign against the level below it, which puts a hole through the
        /// terrain that the finer level does not have. Averaging the signed distance keeps the surface
        /// roughly where the fine one had it.
        ///
        /// The absolute scale of the averaged distance does not matter: the mesher only reads it for edge
        /// crossings (a ratio) and gradients (normalized), both scale-invariant. So world-voxel distances
        /// go in unscaled even though a cell is now several voxels wide.
        /// </summary>
        public static bool TryFillScratch(ChunkMap map, LodSection section, Sample[] scratch)
        {
            if (section.Level == 0)
                return TryFillScratch(map, new SectionIndex(section.X, section.Y, section.Z), scratch);

            int stride = section.Stride;
            int originX = section.OriginX - Apron * stride;
            int originY = section.OriginY - Apron * stride;
            int originZ = section.OriginZ - Apron * stride;

            for (int sy = 0; sy < ScratchWidth; sy++)
            {
                for (int sz = 0; sz < ScratchWidth; sz++)
                {
                    for (int sx = 0; sx < ScratchWidth; sx++)
                    {
                        if (!TryDownsample(map, originX + sx * stride, originY + sy * stride,
                                           originZ + sz * stride, stride, out var sample))
                            return false;

                        scratch[ScratchIndex(sx, sy, sz)] = sample;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// One coarse sample: mean signed distance over the block, and the material of its SHALLOWEST
        /// SOLID voxel — the one nearest the isosurface from below.
        ///
        /// Not a most-common vote, which was the first thing tried and produced visible banding.
        /// DensityToMaterial gives a column exactly one voxel of grass over about three of dirt, so any
        /// block big enough to matter contains three times as much dirt as grass and the vote returns
        /// dirt. Worse, whether it does depends on where the surface happens to fall inside the block, so
        /// the error lands in stripes aligned to the level-of-detail grid rather than uniformly.
        ///
        /// The shallowest solid voxel is the right answer because it is the one the isosurface actually
        /// touches: it is the voxel a viewer sees. It also stays correct at the extremes — a block deep
        /// underground is all clamped stone, and one with no solid voxel at all is air.
        /// </summary>
        static bool TryDownsample(ChunkMap map, int x0, int y0, int z0, int stride, out Sample sample)
        {
            sample = default;

            float total = 0f;
            int count = 0;

            var material = BlockType.BlockType_Air;
            float shallowest = float.NegativeInfinity;

            for (int dz = 0; dz < stride; dz++)
            {
                for (int dy = 0; dy < stride; dy++)
                {
                    for (int dx = 0; dx < stride; dx++)
                    {
                        if (!map.TryGetVoxel(x0 + dx, y0 + dy, z0 + dz, out var voxel)) return false;

                        float distance = voxel.Distance;
                        total += distance;
                        count++;

                        if (distance >= 0f || distance <= shallowest) continue;

                        shallowest = distance;
                        material = voxel.Material;
                    }
                }
            }

            sample = new Sample { Distance = total / count, Material = material };
            return true;
        }

        /// <summary>
        /// Extends the mesh's outer boundary downward into a vertical curtain, so a gap at a level-of-
        /// detail seam shows skirt instead of sky.
        ///
        /// Two boxes at different levels contour from differently-filtered fields, so their surfaces do
        /// not meet along the shared edge — that mismatch is THE hard problem in chunked LOD, and the
        /// honest options are stitching (correct, complex) or hiding it. This hides it. Ugly if you stand
        /// on the seam, invisible at the distance where levels actually change, and it ships.
        ///
        /// A boundary edge is one used by a single triangle whose endpoints both sit on a side face of the
        /// box. Interior open edges — which dual contouring can produce around a hole — are left alone,
        /// since a curtain there would be a wall in mid-air.
        /// </summary>
        public static MeshData AddSkirt(MeshData mesh, float depth)
        {
            if (mesh.Indices.Length == 0 || depth <= 0f) return mesh;

            // Edge -> how many triangles use it, orientation ignored.
            var uses = new Dictionary<(int, int), int>();

            void Count(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                Count(mesh.Indices[i], mesh.Indices[i + 1]);
                Count(mesh.Indices[i + 1], mesh.Indices[i + 2]);
                Count(mesh.Indices[i + 2], mesh.Indices[i]);
            }

            var positions = new List<Vector3>(mesh.Positions);
            var normals = new List<Vector3>(mesh.Normals);
            var indices = new List<int>(mesh.Indices);

            // Submeshes index into the index buffer, so the skirt has to go in ONE run appended at the
            // end. It takes the first submesh's material; a curtain is only ever seen edge-on.
            int skirtStart = indices.Count;

            foreach (var ((a, b), count) in uses)
            {
                if (count != 1) continue;
                if (!OnBoxSide(mesh.Positions[a]) || !OnBoxSide(mesh.Positions[b])) continue;

                var down = new Vector3(0f, -depth, 0f);

                int a2 = positions.Count; positions.Add(mesh.Positions[a] + down); normals.Add(mesh.Normals[a]);
                int b2 = positions.Count; positions.Add(mesh.Positions[b] + down); normals.Add(mesh.Normals[b]);

                // Both windings: a curtain has no meaningful facing and must not vanish from one side.
                indices.AddRange([a, b, b2, a, b2, a2]);
                indices.AddRange([b, a, a2, b, a2, b2]);
            }

            if (indices.Count == skirtStart) return mesh;

            var submeshes = new List<Submesh>(mesh.Submeshes)
            {
                new(mesh.Submeshes.Length > 0 ? mesh.Submeshes[0].Material : BlockType.BlockType_Stone,
                    skirtStart, indices.Count - skirtStart)
            };

            return new MeshData
            {
                Positions = [.. positions],
                Normals = [.. normals],
                Indices = [.. indices],
                Submeshes = [.. submeshes],
            };
        }

        /// <summary>
        /// Whether a position sits in the outermost RING OF CELLS, which is what a boundary vertex means
        /// for a dual method. Not "on the box's face" — dual contouring puts its vertex inside the cell,
        /// so a boundary vertex lands around half a cell in and never touches the plane. Testing the
        /// plane finds nothing at all, which is a skirt that silently does not exist.
        ///
        /// Cells run CellMin..CellMin+CellsPerAxis-1, so a vertex of the first cell lies in [-1, 0] and
        /// one of the last lies in [15, 16].
        /// </summary>
        static bool OnBoxSide(Vector3 position)
        {
            const float Epsilon = 1e-3f;
            const float Low = CellMin + 1;                    // 0: everything below is the first cell
            const float High = CellMin + CellsPerAxis - 1;    // 15: everything above is the last

            return position.X <= Low + Epsilon || position.X >= High - Epsilon
                || position.Z <= Low + Epsilon || position.Z >= High - Epsilon;
        }

        /// <summary>
        /// Upper triangle of a symmetric 3x3. Only ever accumulates outer products n*nT, which are
        /// symmetric, so storing six floats instead of nine is free.
        /// </summary>
        struct Symmetric3
        {
            public float M00, M01, M02, M11, M12, M22;

            public void AddOuterProduct(Vector3 n)
            {
                M00 += n.X * n.X; M01 += n.X * n.Y; M02 += n.X * n.Z;
                                  M11 += n.Y * n.Y; M12 += n.Y * n.Z;
                                                    M22 += n.Z * n.Z;
            }
        }

        /// <summary>
        /// How strongly the solve is pulled toward the mass point. This is what keeps the
        /// RANK-DEFICIENT case sane: on flat ground every normal is parallel, AtA has rank 1, and the
        /// true minimizer is an entire plane of equally good answers. Biasing toward the mass point
        /// pins the unconstrained directions to the surface-nets answer, which is right there.
        ///
        /// Each crossing adds an outer product of trace 1, so AtA's trace is roughly the crossing
        /// count (3-12). At this size the bias is negligible where the system is well conditioned and
        /// decisive where it isn't — which is the whole trick, and why no SVD is needed.
        /// </summary>
        const float QefBias = 0.05f;

        /// <summary>
        /// Minimizes sum over crossings of (n.(v - p))^2, i.e. squared distance to each tangent
        /// plane, biased toward <paramref name="massPoint"/> and clamped into the cell.
        /// </summary>
        static Vector3 SolveQef(Symmetric3 ata, Vector3 atb, Vector3 massPoint, Vector3 cellMin)
        {
            // (AtA + bias*I) v = Atb + bias*massPoint
            float a00 = ata.M00 + QefBias, a11 = ata.M11 + QefBias, a22 = ata.M22 + QefBias;
            float a01 = ata.M01, a02 = ata.M02, a12 = ata.M12;

            Vector3 b = atb + massPoint * QefBias;

            // Cofactors. The adjugate of a symmetric matrix is symmetric, so six are enough.
            float c00 = a11 * a22 - a12 * a12;
            float c01 = a02 * a12 - a01 * a22;
            float c02 = a01 * a12 - a02 * a11;
            float c11 = a00 * a22 - a02 * a02;
            float c12 = a02 * a01 - a00 * a12;
            float c22 = a00 * a11 - a01 * a01;

            float det = a00 * c00 + a01 * c01 + a02 * c02;
            if (MathF.Abs(det) < 1e-12f) return massPoint;   // shouldn't happen with the bias term

            var v = new Vector3(c00 * b.X + c01 * b.Y + c02 * b.Z,
                                c01 * b.X + c11 * b.Y + c12 * b.Z,
                                c02 * b.X + c12 * b.Y + c22 * b.Z) / det;

            // A QEF minimum can sit far outside its own cell — on a near-flat patch the solution is
            // a long thin valley and small errors slide a long way down it. Unclamped, those show up
            // as spikes. Clamping also keeps the vertex inside the cell that owns it, which is what
            // makes the dual topology from pass 2 valid.
            return Vector3.Clamp(v, cellMin, cellMin + Vector3.One);
        }

        /// <summary>Where a cell's single vertex goes. The only difference between the two meshers.</summary>
        enum Placement
        {
            /// <summary>Average of the edge crossings. Rounds off creases.</summary>
            SurfaceNets,
            /// <summary>QEF minimum over the tangent planes at the crossings. Reconstructs creases.</summary>
            DualContouring,
        }

        /// <summary>Surface nets over a filled scratch buffer. Positions are chunk-local.</summary>
        public static MeshData GenerateMeshFromSurfaceNet(Sample[] scratch)
            => Generate(scratch, Placement.SurfaceNets);

        /// <summary>
        /// Dual contouring over a filled scratch buffer. Same topology as surface nets; the vertex
        /// is the point that best fits the tangent planes at the crossings instead of their average,
        /// so a concave crease lands on the corner rather than half a voxel inside it.
        ///
        /// Sharper geometry does NOT mean sharper shading: a cell still has one vertex with one
        /// normal, shared by every quad touching it. A crisp edge needs two normals at the same
        /// position, i.e. splitting vertices by crease angle, which is a separate step.
        /// </summary>
        public static MeshData GenerateMeshDualContouring(Sample[] scratch)
            => Generate(scratch, Placement.DualContouring);

        static MeshData Generate(Sample[] scratch, Placement placement)
        {
            var cellVertex = new int[CellCount];
            Array.Fill(cellVertex, -1);

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();

            // One index bucket per material; concatenated into ranges at the end.
            var byMaterial = new List<int>[MaterialCount];

            Span<float> d = stackalloc float[8];
            Span<int> quad = stackalloc int[4];

            // A cell can only hold surface if its two corner planes contain both a solid and an air
            // sample between them. Recording that per horizontal slab first collapses the 129-row
            // cell loop down to the rows the surface actually passes through — a 128-tall column of
            // heightmap terrain has a surface in a handful of them, so this is most of the mesher's
            // work avoided. Conservative by design: whole-slab flags can say "maybe" for a row where
            // no individual cell crosses, which costs a wasted row and never a missing quad.
            Span<bool> slabHasSolid = stackalloc bool[ScratchWidth];
            Span<bool> slabHasAir = stackalloc bool[ScratchWidth];
            int slabArea = ScratchWidth * ScratchWidth;

            for (int sy = 0; sy < ScratchWidth; sy++)
            {
                bool solid = false, air = false;

                for (int i = sy * slabArea, end = i + slabArea; i < end; i++)
                {
                    if (scratch[i].Distance < 0f) solid = true; else air = true;
                    if (solid && air) break;
                }

                slabHasSolid[sy] = solid;
                slabHasAir[sy] = air;
            }

            int minCy = int.MaxValue, maxCy = int.MinValue;

            for (int cy = CellMin; cy < CellMin + CellsPerAxis; cy++)
            {
                int lower = cy + Apron, upper = cy + 1 + Apron;

                if (!((slabHasSolid[lower] || slabHasSolid[upper]) &&
                      (slabHasAir[lower] || slabHasAir[upper]))) continue;

                if (cy < minCy) minCy = cy;
                if (cy > maxCy) maxCy = cy;
            }

            if (minCy > maxCy) return MeshData.Empty;   // no sign change anywhere in the chunk

            // --- Pass 1: one vertex per cell the surface passes through ----
            // Starts at CellMin, so the -1 layer that border quads reach back into gets vertices.
            for (int cy = minCy; cy <= maxCy; cy++)
            {
                for (int cz = CellMin; cz < CellMin + CellsPerAxis; cz++)
                {
                    for (int cx = CellMin; cx < CellMin + CellsPerAxis; cx++)
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

                        // Normal equations for the QEF, accumulated in place so the (point, normal)
                        // pairs never have to be stored. Unused when placing by average.
                        var ata = new Symmetric3();
                        var atb = Vector3.Zero;

                        foreach (var (a, b) in CellEdges)
                        {
                            if ((d[a] < 0f) == (d[b] < 0f)) continue; // if both signs are the same, surface doesnt pass between the points defining this edge

                            float t = d[a] / (d[a] - d[b]);      // treat density as linear along the edge

                            var oa = CornerOffsets[a];
                            var ob = CornerOffsets[b];
                            int ax = cx + oa.x, ay = cy + oa.y, az = cz + oa.z;
                            int bx = cx + ob.x, by = cy + ob.y, bz = cz + ob.z;

                            var crossing = Vector3.Lerp(new Vector3(ax, ay, az), new Vector3(bx, by, bz), t);
                            var gradient = Vector3.Lerp(GradientAt(scratch, ax, ay, az),
                                                        GradientAt(scratch, bx, by, bz), t);

                            sumPos += crossing;
                            sumNormal += gradient;
                            crossings++;

                            if (placement != Placement.DualContouring) continue;

                            // The QEF weights every plane equally, so these must be UNIT normals —
                            // the raw gradient's magnitude would weight by how steep the field is.
                            if (gradient.LengthSquared() <= 1e-12f) continue;
                            var n = Vector3.Normalize(gradient);

                            ata.AddOuterProduct(n);
                            atb += n * Vector3.Dot(n, crossing);
                        }

                        cellVertex[CellIndex(cx, cy, cz)] = positions.Count;

                        // The one line that separates the two algorithms.
                        var massPoint = sumPos / crossings;
                        positions.Add(placement == Placement.SurfaceNets
                            ? massPoint
                            : SolveQef(ata, atb, massPoint, new Vector3(cx, cy, cz)));

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
            // Iterates the grid points this SECTION owns — local 0..15 on all three axes. Every
            // world grid point is owned by exactly one section, so every quad is emitted exactly
            // once, and the ones on a border knit rather than being dropped.
            // Same band as pass 1. A sign-changing edge at py implies the cell rows py and py-1 hold
            // surface, so anything outside [minCy, maxCy + 1] cannot produce a quad.
            int minPy = Math.Max(0, minCy);
            int maxPy = Math.Min(ChunkConstants.SectionHeight - 1, maxCy + 1);

            for (int py = minPy; py <= maxPy; py++)
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
                                    qx >= CellMin + CellsPerAxis || qy >= CellMin + CellsPerAxis || qz >= CellMin + CellsPerAxis)
                                {
                                    complete = false;
                                    break;
                                }

                                quad[i] = cellVertex[CellIndex(qx, qy, qz)];
                                if (quad[i] < 0) { complete = false; break; }
                            }
                            if (!complete) continue;

                            // The quad exists because THIS edge changes sign, so the solid end of it
                            // names the material. Unambiguous, unlike asking the four corner cells,
                            // which can disagree.
                            BlockType material = dp < 0f
                                ? MaterialAt(scratch, px, py, pz)
                                : MaterialAt(scratch, px + step.x, py + step.y, pz + step.z);

                            var indices = byMaterial[(int)material] ??= new List<int>();

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

            // Concatenate the buckets so one index buffer holds everything, and record where each
            // material's run starts. Materials with no quads produce no submesh.
            var allIndices = new List<int>();
            var submeshes = new List<Submesh>();

            for (int m = 0; m < MaterialCount; m++)
            {
                if (byMaterial[m] is not { Count: > 0 } bucket) continue;

                submeshes.Add(new Submesh((BlockType)m, allIndices.Count, bucket.Count));
                allIndices.AddRange(bucket);
            }

            return new MeshData
            {
                Positions = [.. positions],
                Normals = [.. normals],
                Indices = [.. allIndices],
                Submeshes = [.. submeshes],
            };
        }

        /// <summary>
        /// Splits vertices whose incident faces disagree by more than the crease angle, so a hard
        /// edge carries one normal per side instead of one average shared across it. Dual contouring
        /// puts the crease vertex in the right PLACE; this is what makes it look like an edge.
        ///
        /// Vertices with a single smooth group keep their original gradient normal — that's the true
        /// field normal and better than anything reconstructed from triangles. Only split vertices
        /// fall back to area-weighted face normals, because there the field has no single answer.
        ///
        /// Index count and submesh ranges are unchanged; only index VALUES are remapped, so this
        /// composes with either mesher and with the per-material grouping.
        /// </summary>
        public static MeshData SplitCreases(MeshData mesh, float creaseAngleDegrees = 50f)
        {
            int triangles = mesh.Indices.Length / 3;
            if (triangles == 0) return mesh;

            // Unnormalized, so magnitude is twice the triangle area and summing weights by area.
            var faceNormals = new Vector3[triangles];

            for (int t = 0; t < triangles; t++)
            {
                int i0 = mesh.Indices[t * 3], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];

                var cross = Vector3.Cross(mesh.Positions[i1] - mesh.Positions[i0],
                                          mesh.Positions[i2] - mesh.Positions[i0]);

                // Orient against the field normals rather than assuming a winding convention, so
                // this keeps working if the winding is ever flipped.
                if (Vector3.Dot(cross, mesh.Normals[i0] + mesh.Normals[i1] + mesh.Normals[i2]) < 0f)
                    cross = -cross;

                faceNormals[t] = cross;
            }

            var incident = new List<int>[mesh.Positions.Length];
            for (int t = 0; t < triangles; t++)
            {
                for (int k = 0; k < 3; k++)
                    (incident[mesh.Indices[t * 3 + k]] ??= new List<int>(6)).Add(t);
            }

            float cosThreshold = MathF.Cos(creaseAngleDegrees * MathF.PI / 180f);

            var positions = new List<Vector3>(mesh.Positions.Length);
            var normals = new List<Vector3>(mesh.Positions.Length);
            var indices = new int[mesh.Indices.Length];

            var representative = new List<Vector3>(4);   // unit normal defining each group
            var accumulated = new List<Vector3>(4);      // area-weighted sum within each group
            var outputIndex = new List<int>(4);

            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                if (incident[v] is not { Count: > 0 } faces) continue;   // unreferenced: drop it

                representative.Clear();
                accumulated.Clear();
                outputIndex.Clear();

                // Greedy grouping. A vertex has at most a dozen incident faces, so the quadratic
                // scan is cheaper than building edge adjacency for a proper flood fill.
                Span<int> group = faces.Count <= 32 ? stackalloc int[faces.Count] : new int[faces.Count];

                for (int f = 0; f < faces.Count; f++)
                {
                    var raw = faceNormals[faces[f]];
                    var unit = raw.LengthSquared() > 1e-12f ? Vector3.Normalize(raw) : mesh.Normals[v];

                    int g = -1;
                    for (int k = 0; k < representative.Count; k++)
                    {
                        if (Vector3.Dot(unit, representative[k]) < cosThreshold) continue;
                        g = k;
                        break;
                    }

                    if (g < 0)
                    {
                        g = representative.Count;
                        representative.Add(unit);
                        accumulated.Add(Vector3.Zero);
                        outputIndex.Add(0);
                    }

                    accumulated[g] += raw;
                    group[f] = g;
                }

                bool smooth = representative.Count == 1;

                for (int g = 0; g < representative.Count; g++)
                {
                    outputIndex[g] = positions.Count;
                    positions.Add(mesh.Positions[v]);

                    var n = smooth ? mesh.Normals[v] : accumulated[g];
                    normals.Add(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : mesh.Normals[v]);
                }

                for (int f = 0; f < faces.Count; f++)
                {
                    int t = faces[f];
                    for (int k = 0; k < 3; k++)
                    {
                        if (mesh.Indices[t * 3 + k] == v) indices[t * 3 + k] = outputIndex[group[f]];
                    }
                }
            }

            return new MeshData
            {
                Positions = [.. positions],
                Normals = [.. normals],
                Indices = indices,
                Submeshes = mesh.Submeshes,
            };
        }
    }

}
