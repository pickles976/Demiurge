using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct WelcomeData : IMessageSerializable
    {
        public ushort ClientId;

        public void Serialize(Message message)
        {
            message.AddUShort(ClientId);
        }

        public void Deserialize(Message message)
        {
            ClientId = message.GetUShort();
        }
    }
}