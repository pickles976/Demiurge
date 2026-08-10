using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    /// <summary>
    /// "I want to dig out — or fill in — the voxel at this grid point."
    ///
    /// One message for both because they are one action: <see cref="Action"/> only picks which side
    /// of the surface the brush lands on and which way the CSG operator runs. Splitting them would
    /// mean two rate gates, two reach checks and two chances for them to disagree.
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

        /// <summary>Dig or place. Default is <see cref="TerrainAction.Dig"/>, which is what keeps
        /// every existing construction of this struct — every NPC dig — meaning what it did.</summary>
        public TerrainAction Action;

        public void Serialize(Message message)
        {
            message.AddVector3(Target);
            message.AddByte((byte)Hotbar);
            message.AddByte((byte)Action);
        }

        public void Deserialize(Message message)
        {
            Target = message.GetVector3();
            Hotbar = (HotbarSlot)message.GetByte();
            Action = (TerrainAction)message.GetByte();
        }
    }
}
