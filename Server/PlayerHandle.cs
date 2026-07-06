
using System.Numerics;
using Riptide;

namespace Demiurge.GameServer
{
    internal class PlayerHandle
    {
        
        internal static readonly Dictionary<ushort, PlayerHandle> List = new Dictionary<ushort, PlayerHandle>();

        private readonly ushort id;
        private Vector3 position = Vector3.Zero;

        internal PlayerHandle(ushort clientId)
        {
            // Spawn every other player
            id = clientId;
            foreach (PlayerHandle otherPlayer in List.Values)
            {
                Program.Server.Send(otherPlayer.CreateSpawnMessage(), id);
            }

            // Spawn this player
            List.Add(clientId, this);
            Program.Server.SendToAll(CreateSpawnMessage());
        }

        internal static void SendPositions()
        {
            foreach (PlayerHandle player in List.Values)
            {
                player.SendPosition();
            }
        }

        private Message CreateSpawnMessage()
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerSpawn);
            message.AddUShort(id);
            message.AddVector3(position);
            return message;
        }

        internal void SendPosition()
        {
            Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerPosition);
            message.AddUShort(id);
            message.AddVector3(position);
            Program.Server.SendToAll(message, id);
        }

        [MessageHandler((ushort)ClientToServerId.PlayerPosition)]
        private static void HandlePosition(ushort fromClientId, Message message)
        {
            if (List.TryGetValue(fromClientId, out PlayerHandle player))
                player.position = message.GetVector3();
        }
    }
}

