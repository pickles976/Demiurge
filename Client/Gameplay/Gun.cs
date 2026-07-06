// Gun logic is parked until weapons return (as networked pickup/equip objects —
// see the README networking TODOs). When rewiring: "has a weapon" becomes a
// PlayerStateFlags bit so remote players can see it, and shooting goes through a
// FireAction message with the server doing the authoritative raycast.
// The old client-side shooting implementation (two-stage raycast, tracers,
// positional audio) is in git history: git show ec3fbd0:Client/Gun.cs

// using Stride.Core;
// using Stride.Engine;

// namespace Demiurge
// {

//     // Live core of the gun: seeds and publishes HUD state.
//     public class GunScript : SyncScript
//     {
//         public int magazineCapacity = 30;
//         public float currentAmmo = 30;

//         // Shared state the HUD reads (resolved in Start). The gun never references the HUD.
//         private IPlayerStatus _status = null!;

//         public override void Start()
//         {
//             base.Start();
//             // Resolve shared state and seed it from this gun's config.
//             _status = Services.GetSafeServiceAs<IPlayerStatus>();
//             _status.WeaponEquipped = true;
//             _status.MagazineCapacity = magazineCapacity;

//             PublishAmmo();
//             GameEvents.WeaponEquipped.Broadcast();
//         }

//         public override void Update() { }

//         /// <summary>
//         /// Writes the current ammo into shared state (B) and broadcasts the change
//         /// signal (A) so the HUD re-reads. Call after any change to currentAmmo.
//         /// </summary>
//         private void PublishAmmo()
//         {
//             _status.CurrentAmmo = (int)currentAmmo;
//             GameEvents.AmmoChanged.Broadcast();
//         }

//     }

// }
