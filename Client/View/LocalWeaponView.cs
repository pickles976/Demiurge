using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// View-layer bridge from the first-person weapon presenter to the local controller. The sim
    /// should not know about entities, but the shot origin has to match the barrel the player sees.
    /// </summary>
    public sealed class LocalWeaponView
    {
        public Vector3? MuzzleWorld { get; set; }
        public ItemType? Weapon { get; set; }
        public uint? NetworkId { get; set; }

        public void Clear()
        {
            MuzzleWorld = null;
            Weapon = null;
            NetworkId = null;
        }
    }
}
