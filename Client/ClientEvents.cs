using Stride.Engine.Events;

namespace Demiurge
{
    public static class GameEvents
    {

        public static readonly EventKey WeaponEquipped = new("Game", "WeaponEquipped");

        // Signal only (the "A" in the A+B pattern): "ammo changed, re-read IPlayerStatus".
        // The value itself lives in the service (B), so the event carries no payload —
        // it's purely a notification that something changed.
        public static readonly EventKey AmmoChanged = new("Game", "AmmoChanged");
    }
}
