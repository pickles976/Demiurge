namespace Demiurge
{
    /// <summary>
    /// Encodes chunk voxels for the wire. The server owns terrain and sends it; the client never
    /// generates any (see <see cref="WorldGen"/>, which is server-side only).
    ///
    /// The unit is a SLAB — one 16x16 horizontal layer of a chunk, 256 voxels. Chosen because even a
    /// worst-case slab fits inside Riptide's 1225-byte payload, which means no fragmentation-and-
    /// reassembly layer. A whole 16^3 section would be 8 KB and would need one.
    ///
    /// Riptide's Reliable mode guarantees delivery but NOT order, so every message must be
    /// independently applicable — hence each carries its own chunk index and slab range, and none
    /// depends on another having arrived first. That rule is why nothing here deltas against the
    /// slab below, which would otherwise be the single biggest remaining win.
    ///
    /// COMPRESSION. Density and material are stored interleaved but encoded as separate PLANES,
    /// because they have nothing in common statistically and interleaving defeats both schemes below.
    ///
    /// - Most slabs are one repeated voxel: everything above the terrain is air and everything below
    ///   is clamped solid. Those cost 3 bytes.
    /// - A mixed slab's MATERIAL plane has at most four values, so it goes as a palette plus either
    ///   run lengths or packed indices, whichever is smaller for that slab.
    /// - A mixed slab's DENSITY plane is mostly saturated at +/-127, because
    ///   <see cref="Voxel.Scale"/> only resolves +/-2.54 voxels around the surface. Saturated voxels
    ///   are not sent at all: a 256-bit mask marks the ones that are, and the rest are reconstructed
    ///   from the material plane's own sign (air means the distance was positive). The mask is built
    ///   from whether that reconstruction would be EXACT, so a voxel whose material and density
    ///   disagree simply becomes a literal — the encoding is lossless by construction, not by relying
    ///   on the generator's invariant holding.
    ///
    /// Cost works out at roughly `59 * mixedSlabs + 1664` bytes per chunk against the old
    /// `513 * mixedSlabs`, so about 3x on the current map and more as terrain gets rougher — the
    /// constant term dominates, which is what stops mountains costing more than plains. A perfectly
    /// flat chunk is 384 bytes either way; a slab that beats neither scheme falls back to raw, so the
    /// pathological case loses 0.4% rather than 100%.
    /// </summary>
    public static class ChunkWire
    {
        /// <summary>Voxels in one 16x16 horizontal layer.</summary>
        public const int SlabVoxels = ChunkConstants.ChunkSize;

        /// <summary>One bit per voxel: which densities are sent literally.</summary>
        const int MaskBytes = SlabVoxels / 8;

        /// <summary>Past this a palette needs more than 4 index bits and raw wins anyway.</summary>
        const int MaxPaletteEntries = 16;

        /// <summary>Six bits of length in a palette-RLE control byte, so 1..64.</summary>
        const int MaxRunLength = 64;

        // Slab tags.
        const byte SlabUniform = 0;
        const byte SlabMixed = 1;

        // Material plane encodings.
        const byte MatUniform = 0;
        const byte MatPaletteRle = 1;
        const byte MatPaletteBits = 2;
        const byte MatRaw = 3;

        // Density plane encodings.
        const byte DenUniform = 0;
        const byte DenMasked = 1;
        const byte DenRaw = 2;

        /// <summary>Worst case for one slab, so callers can size a buffer that always fits one.</summary>
        public static int MaxSlabBytes => 1 + (1 + SlabVoxels) + (1 + SlabVoxels);

        /// <summary>
        /// Encodes slabs from <paramref name="firstSlabY"/> upward into <paramref name="destination"/>,
        /// stopping before it would overflow. Returns how many slabs were written and how many bytes
        /// they took, so the caller can emit that as one message and continue from where it stopped.
        /// </summary>
        public static (int slabCount, int byteCount) Encode(
            TerrainChunk chunk, int firstSlabY, Span<byte> destination)
        {
            Span<BlockType> palette = stackalloc BlockType[MaxPaletteEntries];

            int written = 0;
            int slabs = 0;

            for (int slabY = firstSlabY; slabY < ChunkConstants.ChunkHeight; slabY++)
            {
                int start = slabY * SlabVoxels;

                var stats = Scan(chunk, start, palette);

                int needed = stats.VoxelUniform
                    ? 3
                    : 1 + MaterialBytes(stats, out _) + DensityBytes(stats, out _);

                if (written + needed > destination.Length) break;    // full: caller sends and resumes

                if (stats.VoxelUniform)
                {
                    var value = chunk[start];
                    destination[written++] = SlabUniform;
                    destination[written++] = (byte)value.Density;
                    destination[written++] = (byte)value.Material;
                }
                else
                {
                    destination[written++] = SlabMixed;
                    written += WriteMaterial(chunk, start, stats, palette, destination[written..]);
                    written += WriteDensity(chunk, start, stats, destination[written..]);
                }

                slabs++;
            }

            return (slabs, written);
        }

        /// <summary>Applies a payload produced by <see cref="Encode"/> into a chunk.</summary>
        public static void Decode(TerrainChunk chunk, int firstSlabY, int slabCount, ReadOnlySpan<byte> source)
        {
            int read = 0;

            for (int slab = 0; slab < slabCount; slab++)
            {
                int start = (firstSlabY + slab) * SlabVoxels;

                if (source[read++] == SlabUniform)
                {
                    var value = new Voxel { Density = (sbyte)source[read++], Material = (BlockType)source[read++] };

                    // Collapse rather than write 256 copies. This is what keeps a decoded chunk small:
                    // the wire already knows which slabs are uniform, so storage can too.
                    chunk.FillSlab(firstSlabY + slab, value);
                    continue;
                }

                // Material first: the density plane's saturated voxels are reconstructed from it.
                read += ReadMaterial(chunk, start, source[read..]);
                read += ReadDensity(chunk, start, source[read..]);
            }
        }

        // ---- Measuring ----

        /// <summary>Everything the encoder needs to know about a slab, gathered in one pass over it.</summary>
        struct SlabStats
        {
            public bool VoxelUniform;
            public bool MaterialUniform;
            public bool DensityUniform;

            /// <summary>Distinct materials, or 0 if there were more than <see cref="MaxPaletteEntries"/>.</summary>
            public int PaletteCount;

            /// <summary>Control bytes a palette-RLE material plane would need.</summary>
            public int RunCount;

            /// <summary>Densities that cannot be reconstructed from the material plane.</summary>
            public int LiteralCount;
        }

        static SlabStats Scan(TerrainChunk chunk, int start, Span<BlockType> palette)
        {
            var stats = new SlabStats { MaterialUniform = true, DensityUniform = true };
            var first = chunk[start];

            bool paletteOverflow = false;
            var runMaterial = first.Material;
            int runLength = 0;

            for (int i = 0; i < SlabVoxels; i++)
            {
                var voxel = chunk[start + i];

                if (voxel.Material != first.Material) stats.MaterialUniform = false;
                if (voxel.Density != first.Density) stats.DensityUniform = false;

                if (!paletteOverflow && IndexOf(palette, stats.PaletteCount, voxel.Material) < 0)
                {
                    if (stats.PaletteCount == MaxPaletteEntries) paletteOverflow = true;
                    else palette[stats.PaletteCount++] = voxel.Material;
                }

                // A run ends at a material change, or when it fills the 6-bit length field.
                if (i > 0 && (voxel.Material != runMaterial || runLength == MaxRunLength))
                {
                    stats.RunCount++;
                    runMaterial = voxel.Material;
                    runLength = 0;
                }
                runLength++;

                if (!Reconstructable(voxel)) stats.LiteralCount++;
            }

            stats.RunCount++;                                          // the run still open at the end
            if (paletteOverflow) stats.PaletteCount = 0;               // 0 means "unavailable"
            stats.VoxelUniform = stats.MaterialUniform && stats.DensityUniform;

            return stats;
        }

        /// <summary>
        /// Whether this voxel's density is exactly what the material plane implies — saturated, with
        /// the sign air-ness gives. These are the voxels the mask leaves out.
        /// </summary>
        static bool Reconstructable(Voxel voxel)
            => voxel.Density == SaturatedDensity(voxel.Material);

        static sbyte SaturatedDensity(BlockType material)
            => material == BlockType.BlockType_Air ? Voxel.Maximum : Voxel.Minimum;

        static int MaterialBytes(in SlabStats stats, out byte encoding)
        {
            if (stats.MaterialUniform) { encoding = MatUniform; return 2; }

            encoding = MatRaw;
            int best = 1 + SlabVoxels;

            // RLE packs the index into the same byte as the length, so it needs 2 index bits or fewer.
            if (stats.PaletteCount is > 0 and <= 4)
            {
                int rle = 2 + stats.PaletteCount + stats.RunCount;
                if (rle < best) { best = rle; encoding = MatPaletteRle; }
            }

            if (stats.PaletteCount > 0)
            {
                int packed = 2 + stats.PaletteCount + SlabVoxels * IndexBits(stats.PaletteCount) / 8;
                if (packed < best) { best = packed; encoding = MatPaletteBits; }
            }

            return best;
        }

        static int DensityBytes(in SlabStats stats, out byte encoding)
        {
            if (stats.DensityUniform) { encoding = DenUniform; return 2; }

            int masked = 1 + MaskBytes + stats.LiteralCount;
            if (masked <= 1 + SlabVoxels) { encoding = DenMasked; return masked; }

            encoding = DenRaw;
            return 1 + SlabVoxels;
        }

        /// <summary>Index width that divides 8 evenly, so packed indices never straddle a byte.</summary>
        static int IndexBits(int paletteCount) => paletteCount <= 2 ? 1 : paletteCount <= 4 ? 2 : 4;

        static int IndexOf(ReadOnlySpan<BlockType> palette, int count, BlockType material)
        {
            for (int i = 0; i < count; i++)
                if (palette[i] == material) return i;

            return -1;
        }

        // ---- Writing ----

        static int WriteMaterial(TerrainChunk chunk, int start, in SlabStats stats,
                                 ReadOnlySpan<BlockType> palette, Span<byte> destination)
        {
            MaterialBytes(stats, out byte encoding);

            int written = 0;
            destination[written++] = encoding;

            switch (encoding)
            {
                case MatUniform:
                    destination[written++] = (byte)chunk[start].Material;
                    return written;

                case MatRaw:
                    for (int i = 0; i < SlabVoxels; i++)
                        destination[written++] = (byte)chunk[start + i].Material;
                    return written;
            }

            destination[written++] = (byte)stats.PaletteCount;
            for (int i = 0; i < stats.PaletteCount; i++)
                destination[written++] = (byte)palette[i];

            if (encoding == MatPaletteRle)
            {
                var runMaterial = chunk[start].Material;
                int runLength = 0;

                for (int i = 0; i < SlabVoxels; i++)
                {
                    var material = chunk[start + i].Material;

                    if (i > 0 && (material != runMaterial || runLength == MaxRunLength))
                    {
                        destination[written++] = RunByte(palette, stats.PaletteCount, runMaterial, runLength);
                        runMaterial = material;
                        runLength = 0;
                    }
                    runLength++;
                }

                destination[written++] = RunByte(palette, stats.PaletteCount, runMaterial, runLength);
                return written;
            }

            int bits = IndexBits(stats.PaletteCount);
            int perByte = 8 / bits;

            for (int i = 0; i < SlabVoxels; i += perByte)
            {
                byte packed = 0;
                for (int j = 0; j < perByte; j++)
                    packed |= (byte)(IndexOf(palette, stats.PaletteCount, chunk[start + i + j].Material) << (j * bits));

                destination[written++] = packed;
            }

            return written;
        }

        /// <summary>Two index bits in the high end, six length bits in the low. Length is stored minus one.</summary>
        static byte RunByte(ReadOnlySpan<BlockType> palette, int count, BlockType material, int length)
            => (byte)((IndexOf(palette, count, material) << 6) | (length - 1));

        static int WriteDensity(TerrainChunk chunk, int start, in SlabStats stats, Span<byte> destination)
        {
            DensityBytes(stats, out byte encoding);

            int written = 0;
            destination[written++] = encoding;

            switch (encoding)
            {
                case DenUniform:
                    destination[written++] = (byte)chunk[start].Density;
                    return written;

                case DenRaw:
                    for (int i = 0; i < SlabVoxels; i++)
                        destination[written++] = (byte)chunk[start + i].Density;
                    return written;
            }

            var mask = destination.Slice(written, MaskBytes);
            mask.Clear();
            written += MaskBytes;

            for (int i = 0; i < SlabVoxels; i++)
            {
                if (Reconstructable(chunk[start + i])) continue;

                mask[i >> 3] |= (byte)(1 << (i & 7));
                destination[written++] = (byte)chunk[start + i].Density;
            }

            return written;
        }

        // ---- Reading ----

        static int ReadMaterial(TerrainChunk chunk, int start, ReadOnlySpan<byte> source)
        {
            int read = 0;
            byte encoding = source[read++];

            // One materialisation for the whole slab rather than a null check per voxel.
            var slab = chunk.Materialize(start / SlabVoxels);

            switch (encoding)
            {
                case MatUniform:
                {
                    var material = (BlockType)source[read++];
                    slab.AsSpan().Fill(new Voxel { Material = material });
                    return read;
                }

                case MatRaw:
                    for (int i = 0; i < SlabVoxels; i++) slab[i].Material = (BlockType)source[read++];
                    return read;
            }

            int paletteCount = source[read++];
            Span<BlockType> palette = stackalloc BlockType[MaxPaletteEntries];
            for (int i = 0; i < paletteCount; i++)
                palette[i] = (BlockType)source[read++];

            if (encoding == MatPaletteRle)
            {
                int written = 0;
                while (written < SlabVoxels)
                {
                    byte run = source[read++];
                    var material = palette[run >> 6];
                    int length = (run & 0b0011_1111) + 1;

                    for (int i = 0; i < length; i++)
                        slab[written + i].Material = material;

                    written += length;
                }

                return read;
            }

            int bits = IndexBits(paletteCount);
            int perByte = 8 / bits;
            int indexMask = (1 << bits) - 1;

            for (int i = 0; i < SlabVoxels; i += perByte)
            {
                byte packed = source[read++];
                for (int j = 0; j < perByte; j++)
                    slab[i + j].Material = palette[(packed >> (j * bits)) & indexMask];
            }

            return read;
        }

        static int ReadDensity(TerrainChunk chunk, int start, ReadOnlySpan<byte> source)
        {
            int read = 0;
            byte encoding = source[read++];
            var slab = chunk.Materialize(start / SlabVoxels);

            switch (encoding)
            {
                case DenUniform:
                {
                    var density = (sbyte)source[read++];
                    for (int i = 0; i < SlabVoxels; i++) slab[i].Density = density;
                    return read;
                }

                case DenRaw:
                    for (int i = 0; i < SlabVoxels; i++) slab[i].Density = (sbyte)source[read++];
                    return read;
            }

            var mask = source.Slice(read, MaskBytes);
            read += MaskBytes;

            for (int i = 0; i < SlabVoxels; i++)
            {
                bool literal = (mask[i >> 3] & (1 << (i & 7))) != 0;

                slab[i].Density = literal
                    ? (sbyte)source[read++]
                    : SaturatedDensity(slab[i].Material);
            }

            return read;
        }
    }
}
