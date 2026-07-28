using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Demiurge;

public static class RuntimeMapSerializer
{
    private const uint Magic = 0x50414D44; // "DMAP" little endian
    public const int HashBytes = 32;
    private const int MaxNameBytes = 512;
    private const int MaxPlacements = 100_000;

    public static void Save(string path, RuntimeMap map)
    {
        var validation = RuntimeMapValidation.Validate(map);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Runtime map path has no parent directory");
        Directory.CreateDirectory(directory);

        string temporary = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var writer = new HashingBinaryWriter(stream, hash))
            {
                Write(writer, map);
                writer.Flush();
                stream.Flush(flushToDisk: true);
                map.ContentHash = hash.GetHashAndReset();
                stream.Write(map.ContentHash);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static RuntimeMap Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < HashBytes + 64) throw new InvalidDataException("Runtime map is truncated");

        ReadOnlySpan<byte> content = bytes.AsSpan(0, bytes.Length - HashBytes);
        ReadOnlySpan<byte> storedHash = bytes.AsSpan(bytes.Length - HashBytes);
        byte[] actualHash = SHA256.HashData(content);
        if (!storedHash.SequenceEqual(actualHash))
            throw new InvalidDataException("Runtime map content hash does not match");

        using var stream = new MemoryStream(bytes, 0, bytes.Length - HashBytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var map = Read(reader);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Runtime map has trailing content before its hash");
        map.ContentHash = actualHash;

        var validation = RuntimeMapValidation.Validate(map);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return map;
    }

    private static void Write(BinaryWriter writer, RuntimeMap map)
    {
        writer.Write(Magic);
        writer.Write(RuntimeMap.CurrentFormatVersion);
        writer.Write(map.MapId.ToByteArray());
        WriteString(writer, map.Name, MaxNameBytes);
        writer.Write(ChunkConstants.ChunkWidth);
        writer.Write(ChunkConstants.ChunkHeight);
        writer.Write(ChunkConstants.WorldMinY);
        WriteChunkIndex(writer, WorldGen.Min);
        WriteChunkIndex(writer, WorldGen.Max);
        WriteChunkIndex(writer, WorldGen.MeshableMin);
        WriteChunkIndex(writer, WorldGen.MeshableMax);
        writer.Write(map.SourceHash);

        var chunks = map.Terrain.Snapshot();
        writer.Write(chunks.Count);
        writer.Write(map.Placements.Count);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkTransport.MaxPayloadBytes);
        try
        {
            foreach (var chunk in chunks)
            {
                var (slabs, length) = ChunkWire.Encode(chunk, 0, buffer);
                if (slabs != ChunkConstants.ChunkHeight)
                    throw new InvalidOperationException($"Chunk {chunk.index} did not fit persistence buffer");
                writer.Write(chunk.index.x);
                writer.Write(chunk.index.z);
                writer.Write(length);
                writer.Write(buffer, 0, length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        foreach (var placement in map.Placements)
        {
            writer.Write((byte)placement.Kind);
            writer.Write(placement.Position.X);
            writer.Write(placement.Position.Y);
            writer.Write(placement.Position.Z);
            writer.Write(placement.Yaw);
            writer.Write((ushort)placement.Item);
            WriteString(writer, placement.SpawnId ?? string.Empty, 256);
        }
    }

    private static RuntimeMap Read(BinaryReader reader)
    {
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Not a Demiurge runtime map");
        int version = reader.ReadInt32();
        if (version != RuntimeMap.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported runtime map version {version}");

        var mapId = new Guid(ReadExact(reader, 16));
        string name = ReadString(reader, MaxNameBytes);
        int chunkWidth = reader.ReadInt32();
        int chunkHeight = reader.ReadInt32();
        int worldMinY = reader.ReadInt32();
        var min = ReadChunkIndex(reader);
        var max = ReadChunkIndex(reader);
        var meshableMin = ReadChunkIndex(reader);
        var meshableMax = ReadChunkIndex(reader);

        if (chunkWidth != ChunkConstants.ChunkWidth
            || chunkHeight != ChunkConstants.ChunkHeight
            || worldMinY != ChunkConstants.WorldMinY
            || !min.Equals(WorldGen.Min)
            || !max.Equals(WorldGen.Max)
            || !meshableMin.Equals(WorldGen.MeshableMin)
            || !meshableMax.Equals(WorldGen.MeshableMax))
            throw new InvalidDataException("Runtime map bounds or chunk constants are incompatible");

        byte[] sourceHash = ReadExact(reader, HashBytes);
        int chunkCount = reader.ReadInt32();
        int placementCount = reader.ReadInt32();
        int requiredChunks =
            (WorldGen.Max.x - WorldGen.Min.x + 1) *
            (WorldGen.Max.z - WorldGen.Min.z + 1);
        if (chunkCount != requiredChunks) throw new InvalidDataException($"Invalid chunk count {chunkCount}");
        if (placementCount < 0 || placementCount > MaxPlacements)
            throw new InvalidDataException($"Invalid placement count {placementCount}");

        var terrain = new ChunkMap();
        for (int i = 0; i < chunkCount; i++)
        {
            var index = new ChunkIndex { x = reader.ReadInt32(), z = reader.ReadInt32() };
            int length = reader.ReadInt32();
            if (length < 1 || length > ChunkTransport.MaxPayloadBytes)
                throw new InvalidDataException($"Invalid chunk payload length {length}");
            byte[] payload = ReadExact(reader, length);
            var chunk = new TerrainChunk(index);
            ChunkWire.Decode(chunk, 0, ChunkConstants.ChunkHeight, payload);
            if (terrain.Has(index)) throw new InvalidDataException($"Duplicate chunk {index}");
            terrain.Insert(chunk);
        }

        var placements = new RuntimePlacement[placementCount];
        for (int i = 0; i < placementCount; i++)
        {
            var kind = (RuntimePlacementKind)reader.ReadByte();
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float yaw = reader.ReadSingle();
            var item = (ItemType)reader.ReadUInt16();
            string spawnId = ReadString(reader, 256);
            placements[i] = new RuntimePlacement(kind, position, yaw, item, spawnId);
        }

        return new RuntimeMap
        {
            MapId = mapId,
            Name = name,
            Terrain = terrain,
            Placements = placements,
            SourceHash = sourceHash,
        };
    }

    private static void WriteChunkIndex(BinaryWriter writer, ChunkIndex index)
    {
        writer.Write(index.x);
        writer.Write(index.z);
    }

    private static ChunkIndex ReadChunkIndex(BinaryReader reader)
        => new() { x = reader.ReadInt32(), z = reader.ReadInt32() };

    private static void WriteString(BinaryWriter writer, string value, int maxBytes)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maxBytes) throw new InvalidDataException($"String exceeds {maxBytes} UTF-8 bytes");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int maxBytes)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maxBytes) throw new InvalidDataException($"Invalid string length {length}");
        return Encoding.UTF8.GetString(ReadExact(reader, length));
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }

    private sealed class HashingBinaryWriter : BinaryWriter
    {
        private readonly IncrementalHash hash;

        public HashingBinaryWriter(Stream output, IncrementalHash hash)
            : base(new HashingWriteStream(output, hash), Encoding.UTF8, leaveOpen: true)
        {
            this.hash = hash;
        }

        public new void Flush() => base.Flush();
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly IncrementalHash hash;

        public HashingWriteStream(Stream inner, IncrementalHash hash)
        {
            this.inner = inner;
            this.hash = hash;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            hash.AppendData(buffer);
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
    }
}
