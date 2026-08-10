using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    /// <summary>
    /// A terrain change that has already happened on the server, broadcast so every client can make
    /// the same one.
    ///
    /// The COMMAND travels, not the resulting voxels. A chunk column costs about 5 KB on the wire;
    /// this is 26 bytes, and both ends already share the CSG that turns it into a field change
    /// (<see cref="TerrainEdits"/>), so re-sending terrain for an edit would be paying two hundred
    /// times over for an answer the client can compute exactly.
    ///
    /// This is not the client generating terrain — the thing the streaming design exists to
    /// prevent. It is replicating an edit the server has already decided on, which is what keeps
    /// terrain authoritative once it stops being a pure function of the seed.
    /// </summary>
    public struct TerrainEditData : IMessageSerializable
    {
        public Vector3 Centre;
        public Vector3 HalfExtent;
        public EditMode Mode;
        public BlockType Fill;

        /// <summary>Which primitive. APPENDED — never inserted.</summary>
        public EditShape Shape;

        /// <summary>How much of the edit to apply. 1 is the old full-strength CSG bite.</summary>
        public float Strength;

        public void Serialize(Message message)
        {
            message.AddVector3(Centre);
            message.AddVector3(HalfExtent);
            message.AddByte((byte)Mode);
            message.AddByte((byte)Fill);   // BlockType is a byte enum; match its real width
            message.AddByte((byte)Shape);
            message.AddFloat(Strength);
        }

        public void Deserialize(Message message)
        {
            Centre = message.GetVector3();
            HalfExtent = message.GetVector3();
            Mode = (EditMode)message.GetByte();
            Fill = (BlockType)message.GetByte();
            Shape = (EditShape)message.GetByte();
            Strength = message.GetFloat();
        }
    }
}
