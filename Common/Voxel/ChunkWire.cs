namespace Demiurge
{
    /// <summary>
    /// Encodes chunk voxels for the wire. The server owns terrain and sends it; the client never
    /// generates any (see <see cref="WorldGen"/>, which is server-side only).
    ///
    /// The unit is a SLAB — one 16x16 horizontal layer of a chunk, 256 voxels. Chosen because a
    /// raw slab is 512 bytes and so always fits inside Riptide's 1225-byte payload, which means no
    /// fragmentation-and-reassembly layer. A whole 16^3 section would be 8 KB and would need one.
    ///
    /// Compression is a single trick: 250 of 288 sections in the current map are one repeated voxel,
    /// because everything above the terrain is air and everything below is clamped solid. Flagging
    /// uniform slabs takes the fixed map from 2304 KB to ~305 KB. RLE inside the mixed slabs would
    /// help further and isn't worth it yet.
    ///
    /// Riptide's Reliable mode guarantees delivery but NOT order, so every message must be
    /// independently applicable — hence each carries its own chunk index and slab range, and none
    /// depends on another having arrived first.
    /// </summary>
    public static class ChunkWire
    {
        /// <summary>Voxels in one 16x16 horizontal layer.</summary>
        public const int SlabVoxels = ChunkConstants.ChunkSize;

        const byte Uniform = 0;
        const byte Raw = 1;

        /// <summary>
        /// Encodes slabs from <paramref name="firstSlabY"/> upward into <paramref name="destination"/>,
        /// stopping before it would overflow. Returns how many slabs were written and how many bytes
        /// they took, so the caller can emit that as one message and continue from where it stopped.
        /// </summary>
        public static (int slabCount, int byteCount) Encode(
            TerrainChunk chunk, int firstSlabY, Span<byte> destination)
        {
            int written = 0;
            int slabs = 0;

            for (int slabY = firstSlabY; slabY < ChunkConstants.ChunkHeight; slabY++)
            {
                int start = slabY * SlabVoxels;
                bool uniform = IsUniform(chunk.voxels, start, out Voxel value);

                int needed = uniform ? 3 : 1 + SlabVoxels * 2;
                if (written + needed > destination.Length) break;    // full: caller sends and resumes

                if (uniform)
                {
                    destination[written++] = Uniform;
                    destination[written++] = (byte)value.Density;
                    destination[written++] = (byte)value.Material;
                }
                else
                {
                    destination[written++] = Raw;
                    for (int i = 0; i < SlabVoxels; i++)
                    {
                        destination[written++] = (byte)chunk.voxels[start + i].Density;
                        destination[written++] = (byte)chunk.voxels[start + i].Material;
                    }
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
                byte kind = source[read++];

                if (kind == Uniform)
                {
                    var value = new Voxel { Density = (sbyte)source[read++], Material = (BlockType)source[read++] };
                    for (int i = 0; i < SlabVoxels; i++) chunk.voxels[start + i] = value;
                }
                else
                {
                    for (int i = 0; i < SlabVoxels; i++)
                    {
                        chunk.voxels[start + i].Density = (sbyte)source[read++];
                        chunk.voxels[start + i].Material = (BlockType)source[read++];
                    }
                }
            }
        }

        /// <summary>Worst case for one slab, so callers can size a buffer that always fits one.</summary>
        public static int MaxSlabBytes => 1 + SlabVoxels * 2;

        static bool IsUniform(Voxel[] voxels, int start, out Voxel value)
        {
            value = voxels[start];

            for (int i = 1; i < SlabVoxels; i++)
            {
                if (voxels[start + i].Density != value.Density || voxels[start + i].Material != value.Material)
                    return false;
            }

            return true;
        }
    }
}
