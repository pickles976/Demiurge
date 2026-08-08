namespace Demiurge
{
    /// <summary>
    /// How the trigger behaves while the fire button is held.
    ///
    /// This is a CLIENT-INPUT rule, not a server gate, and that is deliberate. The server only
    /// ever sees one fire request per shot, so it cannot tell a held trigger from a fast finger;
    /// the honest gate it CAN apply is cadence, and <see cref="WeaponStats.TicksPerShot"/> already
    /// does. Enforcing "one press per shot" server-side would mean inferring the trigger edge from
    /// the separate input stream, which would silently reject an honest player whose click landed
    /// between two 30 Hz input samples — the one failure mode this codebase refuses to ship.
    /// </summary>
    public enum FireMode : byte
    {
        /// <summary>Holding the button keeps firing at the weapon's cadence.</summary>
        Automatic,
        /// <summary>One press, one shot; the cadence still caps how fast presses can pay out.</summary>
        SemiAutomatic,
    }

    /// <summary>Static per-weapon numbers. Never on the wire: the client
    /// predicts with them and the server enforces them, both keyed by
    /// ItemState.Type. Cadence is in server ticks so both ends count the
    /// same clock. It is fractional because 20 rounds/second is 1.5 ticks
    /// at 30 TPS; both cooldown implementations retain the half-tick phase.</summary>
    public readonly record struct WeaponStats(
        int MagazineCapacity,
        float TicksPerShot,
        int ReloadTicks,
        ushort Damage,
        WeaponBallisticsProfile BallisticsProfile,
        FireMode FireMode = FireMode.Automatic);

    /// <summary>The weapon trait table: only guns have rows, null means "not a
    /// gun". ItemSystem.SpawnPickup derives the WeaponState mask bit from a row
    /// existing here — the table IS the trait declaration.</summary>
    public static class WeaponConfig
    {
        public static WeaponStats? Get(ItemType type) => type switch
        {
            ItemType.Ak47 => new WeaponStats(MagazineCapacity: 30, TicksPerShot: 3, ReloadTicks: 45, Damage: 30, BallisticsProfile: WeaponBallisticsProfile.Carbine),
            // Same damage and ballistics as the AK; the differences are the ten-round magazine and
            // the trigger. TicksPerShot 3 at 30 Hz is the requested 10 rounds/second ceiling — the
            // cap a fast finger runs into, not the cadence a held button pays out at.
            //
            // ReloadTicks 54 is 1.8 s, three tenths longer than the others: stripper-clipping ten
            // rounds into a fixed magazine takes longer than swapping one, and the reload sample is
            // that length. Ticks rather than seconds so client prediction and server enforcement
            // count the same clock.
            ItemType.Sks => new WeaponStats(MagazineCapacity: 10, TicksPerShot: 3, ReloadTicks: 54, Damage: 30, BallisticsProfile: WeaponBallisticsProfile.Carbine, FireMode: FireMode.SemiAutomatic),
            ItemType.Ppsh => new WeaponStats(
                MagazineCapacity: 35,
                TicksPerShot: NetworkConfig.TickRate / 20f,
                ReloadTicks: 3 * NetworkConfig.TickRate / 2,
                Damage: 18,
                BallisticsProfile: WeaponBallisticsProfile.Pistol),
            ItemType.AWP => new WeaponStats(MagazineCapacity: 5, TicksPerShot: 60, ReloadTicks: 45, Damage: 75, BallisticsProfile: WeaponBallisticsProfile.SniperRifle),
            // A manually cycled bolt is the whole character of this weapon, and cadence is where it
            // lives: the 1.5 s between shots is the time the bolt takes, not a rate of fire somebody
            // picked. That makes TicksPerShot load-bearing rather than cosmetic — the client's bolt
            // animation and its sound are paced to fit inside it (WeaponFx.BoltCycle), so shortening
            // this leaves the rifle firing through its own cycle.
            //
            // Semi-automatic for the same reason the SKS is: one press per shot. Holding the button
            // on a bolt gun would pay out at the cadence, which is exactly what a bolt prevents.
            // Ballistics are the AWP's, shared through the profile rather than copied.
            ItemType.Mosin => new WeaponStats(
                MagazineCapacity: 5,
                TicksPerShot: 3 * NetworkConfig.TickRate / 2f,
                // 5.067 s: five rounds off a stripper clip with the bolt held open, plus the time
                // it takes to close it again afterwards. Ticks rather than seconds so client
                // prediction and server enforcement count the same clock — the seconds are the
                // tuned number and the rounding is explicit because the default is banker's, which
                // silently picks the lower tick whenever a tuned value lands on a midpoint.
                //
                // It is also the length of the reload ANIMATION, since MovingPart holds the bolt
                // open for exactly as long as the reload runs — so changing this pads or trims the
                // dwell in the middle, after the pull and before the close, and moves neither end.
                ReloadTicks: (int)MathF.Round(5.067f * NetworkConfig.TickRate, MidpointRounding.AwayFromZero),
                Damage: 70,
                BallisticsProfile: WeaponBallisticsProfile.SniperRifle,
                FireMode: FireMode.SemiAutomatic),
            // The pan gun. Its numbers are all consequences of one fact — it is a nine-kilo squad
            // automatic firing the Mosin's cartridge — rather than a slot on a rate/damage curve:
            //
            // - 550 rounds a minute is the real cyclic rate, written as the rate rather than as the
            //   tick count it comes to (3.27) so it can be read against the gun it describes. The
            //   fraction matters and is deliberately kept: both cooldown paths carry the sub-tick
            //   phase, so this pays out at 550 rpm rather than rounding to 600 or 450.
            // - 47 rounds is the pan magazine, which is most of what the weapon is for. Swapping one
            //   takes 4 s, which is both slower than any box magazine in the game and just inside
            //   the 4.3 s reload sample, so the picture ends before the noise does.
            // - 50 damage against the Mosin's 70: same bullet, but this is a suppression weapon, and
            //   a two-hit kill that arrives nine times a second would beat every rifle in the game
            //   at every range.
            // - The move scale is the other half of that balance and the reason the gun is carried
            //   rather than free: 0.7 of every movement speed, so taking the volume of fire costs
            //   the ability to reposition with it. That number is in ItemConfig, with every other
            //   item's weight — it is a fact about hauling the thing, not about how it shoots.
            ItemType.Dp27 => new WeaponStats(
                MagazineCapacity: 47,
                TicksPerShot: 60f * NetworkConfig.TickRate / 550f,
                ReloadTicks: (int)MathF.Round(4f * NetworkConfig.TickRate, MidpointRounding.AwayFromZero),
                Damage: 50,
                BallisticsProfile: WeaponBallisticsProfile.MachineGun),
            ItemType.Glock => new WeaponStats(MagazineCapacity: 15, TicksPerShot: 7, ReloadTicks: 20, Damage: 5, BallisticsProfile: WeaponBallisticsProfile.Pistol),
            // A grenade stack uses ammo as its remaining count. Each throw automatically cycles
            // the next grenade for 1.5 seconds; R is never needed for this item.
            //
            // Two, not four. It is the number carried AND the number restored on respawn, since a
            // stack refills to its capacity like any other magazine — so this one value is the whole
            // supply an actor sees between deaths.
            // One bomb down the tube, five seconds to load the next, and no damage of its own —
            // everything the round does happens in the blast, exactly as a grenade does.
            ItemType.Mortar => new WeaponStats(
                MagazineCapacity: 1,
                TicksPerShot: 1f,
                ReloadTicks: (int)(MortarConfig.ReloadSeconds * NetworkConfig.TickRate),
                Damage: 0,
                BallisticsProfile: WeaponBallisticsProfile.Mortar,
                FireMode: FireMode.SemiAutomatic),
            ItemType.Grenade => new WeaponStats(MagazineCapacity: 2, TicksPerShot: 1, ReloadTicks: GrenadeConfig.ReloadTicks, Damage: 0, BallisticsProfile: WeaponBallisticsProfile.Throwable),
            _ => null,
        };

        /// <summary>
        /// What holding this item does to movement speed. One is the answer for anything without a
        /// weapon row, which is what lets both ends ask the question about whatever is in the hand
        /// without first checking that it is a gun.
        /// </summary>

        /// <summary>For call sites that already gated on the WeaponState bit.
        /// Throws instead of handing back phantom stats — a miswired call site
        /// should be loud, not fire an invisible AK.</summary>
        public static WeaponStats Require(ItemType type) =>
            Get(type) ?? throw new InvalidOperationException($"{type} has no weapon stats");
    }
}
