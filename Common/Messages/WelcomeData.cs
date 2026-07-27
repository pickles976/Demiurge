using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct WelcomeData : IMessageSerializable
    {
        public ushort ClientId;

        /// <summary>
        /// Identifies this client on the separate terrain stream (see <see cref="ChunkTransport"/>). The
        /// TCP connection arrives with no way of saying who it is, so it presents this instead of a
        /// client id — which anyone could guess.
        /// </summary>
        public Guid ChunkToken;

        public void Serialize(Message message)
        {
            message.AddUShort(ClientId);
            message.AddBytes(ChunkToken.ToByteArray());
        }

        public void Deserialize(Message message)
        {
            ClientId = message.GetUShort();
            ChunkToken = new Guid(message.GetBytes());
        }
    }
}