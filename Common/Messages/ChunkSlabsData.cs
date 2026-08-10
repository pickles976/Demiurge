using Demiurge.Net;

namespace Demiurge
{
    /// <summary>
    /// A run of horizontal slabs of one chunk, encoded by <see cref="ChunkWire"/>.
    ///
    /// Self-describing on purpose: Riptide's Reliable mode guarantees delivery but not ORDER, so a
    /// client must be able to apply any of these the moment it arrives, in any sequence, without
    /// waiting for a neighbour.
    /// </summary>
    public struct ChunkSlabsData : IMessageSerializable
    {
        public int ChunkX;
        public int ChunkZ;
        public ushort FirstSlabY;
        public ushort SlabCount;
        public byte[] Payload;

        /// <summary>True once every slab of this chunk has been sent, so the client can mesh it.</summary>
        public bool ChunkComplete;

        public void Serialize(Message message)
        {
            message.AddInt(ChunkX);
            message.AddInt(ChunkZ);
            message.AddUShort(FirstSlabY);
            message.AddUShort(SlabCount);
            message.AddBool(ChunkComplete);
            message.AddBytes(Payload);
        }

        public void Deserialize(Message message)
        {
            ChunkX = message.GetInt();
            ChunkZ = message.GetInt();
            FirstSlabY = message.GetUShort();
            SlabCount = message.GetUShort();
            ChunkComplete = message.GetBool();
            Payload = message.GetBytes();
        }
    }
}
