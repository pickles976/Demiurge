# Shooting

Shooting is server-authoritative projectile simulation. There is no gameplay hitscan or maximum
weapon range; every projectile has a 1 km safety-distance cap and normally becomes ineffective
through travel time, drop, spread, terrain collision, or target movement first.

## Player request

`LocalPlayerController` predicts cadence/ammo presentation and sends reliable `PlayerFireData`:

- sequence;
- render tick;
- muzzle origin;
- requested direction;
- selected hotbar slot.

`GameWorld` resolves the connection to its actor and delegates to `WeaponSystem.ApplyFire`.
The server rejects non-finite/future/stale data, a muzzle too far from its authoritative actor,
invalid equipment, reload/cadence violations, or an empty magazine.

## Accepted shot

`WeaponSystem` combines stance, movement, recoil, suppression, and weapon-class MOA, then samples a
deterministic direction from actor ID and shot sequence. It consumes authoritative ammunition,
adds recoil, records a gunshot-hearing event, and creates a projectile using the selected
`BallisticsConfig` profile.

At 30 TPS each projectile:

1. integrates velocity and doubled gravity through `ProjectileMotion`;
2. sweeps the complete tick segment so fast bullets cannot tunnel;
3. chooses the nearest terrain, replicated transform object, or actor hit;
4. applies authoritative damage and friendly fire;
5. suppresses actors passed within 2 m before the first obstruction.

## What a bullet can hit

An actor's hittable volume is a **capsule**, tested by `GunMath.PlayerHitDistance`: the axis runs from
the feet to `PlayerMovement.Body.Height` with radius `GunConfig.HitRadius`, so the swept volume spans
exactly the body the movement solver collides with. A hit registers at closest approach along the
segment, and terrain is applied first as a distance ceiling, so cover stops a shot geometrically.

This replaced a single sphere of radius 0.6 m centred `PlayerCenterHeight` (0.5 m) above the feet,
which spanned roughly the hips: **a standing player's head and shoulders were not hittable by anyone**,
and AI perception aiming at that same low point could not see a target peeking over cover either. In
combination, peeking with only the head exposed was literal invulnerability. The change is horizontally
identical and vertically a strict superset — torso and head shots that silently missed now land, for
NPCs and players alike, and shots into the ground just below someone's feet no longer count.

Crouching deliberately has no case here. It lowers the eye, not the body, so the capsule is unchanged;
cover still protects a crouched player because the terrain ceiling is tested first.

`GunConfig.AimHeights` is the ordered list of body points an AI tries to see and shoot — centre mass,
then the head. Every entry must lie inside the capsule, or an AI would settle on a point it can see
and provably cannot damage; `Common.Tests/GunMathTests.cs` asserts exactly that.

`GunMath.HitDistance` remains the point-sphere test, still used for replicated transform objects,
which have an origin rather than a body.

Enemy NPCs receiving that near-miss event remember the firing position for a two-second defensive
window and seek cover or dig emergency dirt cover. This is a belief stimulus, not continuous access
to the shooter's live position.

Only accepted shots broadcast unreliable `PlayerFiredData` for sound and tracers. Hits and terrain
occlusion remain server decisions. Client tracers, impacts, and line-renderer explosions are
depth-tested presentation and do not participate in collision.

## NPC shots

`CombatBehavior` computes aim from contact memory, turns at 180 degrees/second, compensates
projectile drop, and calls `WeaponSystem.TryFireAi`. That entry bypasses client-packet validation
but retains the same equipment, ammo, cadence, recoil/spread, projectile, damage, and reload path.

It aims at whichever body point perception last had a clear line to, falling back to centre mass for a
remembered contact — where nothing was seen this tick and the obstruction check refuses the shot
anyway. Engagement is bounded at `MaxEngagementRange` (70 m); a contact beyond it stays believed but
does not make combat own the NPC's movement.

A squad's base of fire additionally shoots in a **suppressing** mode: at the contact's *last known*
position, on a slower cadence, without waiting for the target to reappear. Ordinary fire requires
current visibility, which made suppression impossible against precisely the target it exists for — one
with its head down. The receiving half of that loop is item 5 above, so a suppressing NPC now applies
real `Spread.Suppress` pressure and buys its squadmate a bound. See `AI_IMPLEMENTATION.md` step 11.
