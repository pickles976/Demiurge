using Stride.Core;
using Stride.Core.Mathematics;
using System.Runtime.InteropServices;

namespace Demiurge
{
    /// <summary>
    /// One grassable cell. The renderer expands this into several individual blades with
    /// deterministic jitter, scale, rotation, and distance density.
    /// </summary>
    [DataContract]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GrassSeed
    {
        public Vector3 Position;
        public uint Variation;

        public GrassSeed(Vector3 position, uint variation)
        {
            Position = position;
            Variation = variation;
        }
    }

    public static class GrassScatter
    {
        public static uint HashCell(int x, int z, int seed = 0)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093 ^ z * 19349663 ^ seed * 83492791 ^ 0x47524153);
                h ^= h >> 16;
                h *= 0x45d9f3bu;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
