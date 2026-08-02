using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    /// <summary>
    /// "I want to dig out the voxel at this grid point."
    ///
    /// Carries the TARGET rather than the aim ray. The server re-checks that the point is within
    /// reach, but does not re-cast the ray: doing so would need the client's exact camera, which is
    /// not replicated, and would reject digs over any prediction disagreement about where the
    /// player was looking. The authority that matters is over the terrain — what the edit does, and
    /// whether it is close enough to be plausible — not over the aiming.
    /// </summary>
    public struct PlayerDigData : IMessageSerializable
    {
        public Vector3 Target;
        public HotbarSlot Hotbar;

        public void Serialize(Message message)
        {
            message.AddVector3(Target);
            message.AddByte((byte)Hotbar);
        }

        public void Deserialize(Message message)
        {
            Target = message.GetVector3();
            Hotbar = (HotbarSlot)message.GetByte();
        }
    }
}
