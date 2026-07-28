namespace Demiurge
{
    /// <summary>
    /// The whole map, as a pure function of the shared noise seed.
    ///
    /// Both ends call this and get bit-identical voxels, which is why a fixed map needs nothing on
    /// the wire — no chunk serialiser, no streaming, no per-player tracking. The server is still the
    /// authority on everything that MOVES; terrain just isn't something it has to tell anyone about.
    ///
    /// The moment terrain stops being a pure function — the first player dig — this stays the base
    /// and edits ride the wire as ordered operations on top of it, so the model becomes
    /// generate(seed) + replay(edit log).
    /// </summary>
    public static class WorldGen
    {
        /// <summary>
        /// The fixed map, inclusive on both corners: 63x63 chunks, so 1008 voxels across — the 1 km
        /// target. 61x61 of those are meshable; the outer ring is apron.
        ///
        /// Sized by EROSION, not by taste. Erosion's wavelength is what makes a plain a plain and a
        /// range a range, and at ~220 voxels the world has to be several hundred across or the whole map
        /// sits inside one erosion value and comes out uniformly flat or uniformly mountainous — correct
        /// code that looks like the feature did not work.
        ///
        /// Everything is still generated up front and streamed whole. Measured at this size: 3.3 s to
        /// generate, 7.5 MB on the wire, 29,768 sections of which ~4,293 hold geometry. LOD takes the
        /// drawn box count to ~4,000, about 7x fewer.
        ///
        /// Memory used to bound growth here — a chunk cost a flat 64 KB whatever it held, so this size
        /// was ~248 MB per ChunkMap and singleplayer holds two. Lazy slabs took that to ~21 MB.
        /// </summary>
        public static readonly ChunkIndex Min = new() { x = -31, z = -31 };
        public static readonly ChunkIndex Max = new() { x = 31, z = 31 };

        /// <summary>
        /// Chunks that can actually be MESHED. Meshing reads an apron into the neighbours, so the
        /// outer ring of the generated area exists only to let the ring inside it build.
        /// </summary>
        public static ChunkIndex MeshableMin => new() { x = Min.x + 1, z = Min.z + 1 };
        public static ChunkIndex MeshableMax => new() { x = Max.x - 1, z = Max.z - 1 };

        /// <summary>
        /// Fills a map with the whole world. Idempotent: already-present chunks are left alone.
        /// </summary>
        public static void Generate(ChunkMap map)
        {
            for (int x = Min.x; x <= Max.x; x++)
            {
                for (int z = Min.z; z <= Max.z; z++)
                {
                    var index = new ChunkIndex { x = x, z = z };
                    if (map.Has(index)) continue;

                    map.Insert(ChunkGenerator.GenerateChunk(index));
                }
            }

            ApplyFixedEdits(map);
        }

        /// <summary>
        /// Debug scenery, applied identically on both ends.
        ///
        /// These MUST be part of generation rather than something the client does to its own copy.
        /// Terrain is only a pure function of the seed if every writer runs on both sides — miss that
        /// and the server thinks the ground is somewhere the client doesn't draw it, which surfaces
        /// as a player walking on invisible terrain rather than as an obvious error.
        /// </summary>
        static void ApplyFixedEdits(ChunkMap map)
        {
            // if (map.Get(new ChunkIndex { x = 0, z = 0 }) is { } wall) TerrainEdits.AddWall(wall);
            // if (map.Get(new ChunkIndex { x = 0, z = 1 }) is { } trench) TerrainEdits.CarveTrench(trench);
        }
    }
}
