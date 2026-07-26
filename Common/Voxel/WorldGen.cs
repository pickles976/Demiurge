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
        /// The fixed map, inclusive on both corners. Small on purpose: everything is generated up
        /// front and never streamed.
        /// </summary>
        public static readonly ChunkIndex Min = new() { x = -3, z = -3 };
        public static readonly ChunkIndex Max = new() { x = 2, z = 2 };

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
            if (map.Get(new ChunkIndex { x = 0, z = 0 }) is { } wall) TerrainEdits.AddWall(wall);
            if (map.Get(new ChunkIndex { x = 0, z = 1 }) is { } trench) TerrainEdits.CarveTrench(trench);
        }
    }
}
