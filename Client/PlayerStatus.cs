using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Shared player state (the "B" in the A+B pattern): the single source of truth for
    /// values the gameplay scripts write and the HUD reads. Resolved through
    /// <c>Game.Services</c> so neither side holds a direct reference to the other.
    ///
    /// Pair it with <see cref="GameEvents.AmmoChanged"/> (the "A"): writers update this
    /// state then broadcast the event so the HUD re-reads, instead of polling every frame.
    /// </summary>
    public interface IPlayerStatus
    {
        public Vector3 position {get; set; }
        
        bool WeaponEquipped { get; set; }
        int CurrentAmmo { get; set; }
        int MagazineCapacity { get; set; }
    }

    public sealed class PlayerStatus : IPlayerStatus
    {
        public Vector3 position {get; set; }
        public bool WeaponEquipped { get; set; }
        public int CurrentAmmo { get; set; }
        public int MagazineCapacity { get; set; }
    }
}
