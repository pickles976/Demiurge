# Netcode Robustness Plans

Deferred work, written down while the reasoning is fresh. None of this blocks the
current roadmap (PVP mechanics, map gen). Each section says what breaks today,
what to change, and why — so it can be picked up cold, months from now.

What already works and should NOT be reinvented: intent normalization kills speed
hacks (`Common/PlayerMovement.cs:23` — server normalizes inside Step), fire-rate /
reload / ammo / origin gates kill combat cheats (`Server/WeaponSystem.ApplyFire`),
sequence dedup kills input replay (`Server/GameWorld.ApplyInput`). The gaps below
are what's left.

---

## 1. Main loop: bound the catch-up (the spiral of death)

**Today** (`Server/Program.cs`): the fixed-timestep accumulator has no cap. A
transient hitch self-heals (burst of catch-up ticks), but *chronic* overload —
average tick cost > 33.3ms — grows the accumulator forever. The server spends
100% of its time catching up on a past it never reaches, `PumpNetwork()` only
runs between batches so input latency climbs unboundedly, and there is no
recovery even when load drops.

**Change:**
- Clamp accumulator debt (~5 × FixedDt); log every time debt is dropped.
- Cap ticks per outer iteration (~3).
- Pump the network between ticks inside a catch-up batch, not just outside it.

**Why this shape:** dropping debt converts overload from "frozen forever" into
**time dilation** — game time slows uniformly. Because every server system is
tick-denominated (rewind window, NextFireTick, ReloadDoneTick), dilation is
internally consistent: nothing desyncs, everything slows together. That is the
elegant failure mode; protect it.

## 2. Crash isolation in message dispatch

**Today** (`Server/GameServer.OnMessageReceived`): deserialization runs bare. A
truncated or hostile payload throws out of `GetSerializable` and kills the
process. **Change:** try/catch around the dispatch switch; log and kick the
sender. Cheapest robustness win in this file.

## 3. Flood control

**Today:** `PendingMoves` has drain logic (2/tick above depth 3,
`Server/GameWorld.Tick`) but no ceiling — the drain removes at most 60/s, so a
client spamming inputs grows the queue without bound (memory + permanent
position lag). **Change:** hard-cap the queue (~8, drop oldest). Fire/reload are
logically gated but each message still costs CPU — add a per-connection message
rate limit before hosting strangers.

## 4. Client State flags are a future cheat surface

**Today** (`Server/GameWorld.Tick`, the input-apply loop): client `State` flags
are copied verbatim. Currently harmless — Sprinting is a legal input, the
slowing flags only hurt the sender. **Rule to enforce when it matters:** the
moment any flag confers an advantage (crouch shrinking the hitbox, armor
states), that flag must become server-derived or server-validated, never copied.

## 5. Telemetry

You can't handle degradation you can't see. Rolling tick duration (avg + max),
actual TPS, per-player queue depth; warn at ~60% of the 33ms budget. A
once-per-second console line is enough.

## 6. Broadcast once per catch-up batch

**Today:** `BroadcastPositions` runs inside `Tick`, so a 5-tick catch-up burst
sends 5 snapshot volleys back-to-back — bandwidth spike + interpolation pops.
**Change:** broadcast after the accumulator drains (or on the batch's last tick
only). Interpolation already tolerates tick gaps.

## 7. Client render clock: advance at observed rate, stop re-anchoring

**Today** (`Client/Simulation/PlayerRegistry.RenderTick`): the clock free-runs at
a hardcoded 30 ticks/s between packets and hard re-anchors on every newest
arrival. Two failure modes:
- Server dilation (see §1): clock outruns snapshots at 22 real TPS, hits the
  freeze-clamp, jerks backward each packet.
- Jitter passes straight through — an early packet snaps the clock forward; it
  never eases.

**Change:** estimate effective tick rate from smoothed inter-arrival times,
advance the clock at that rate, and correct drift by nudging a few percent
instead of re-anchoring. Highest-leverage client change in this file: fixes both
TPS degradation *and* high-ping jitter. `Client/View/NetObjectScript.cs` has a
second, per-object copy of the same clock — fix both or share one.

## 8. Adaptive interpolation delay

**Today:** fixed 3 ticks (`NetworkConfig.InterpolationDelayTicks`) = 100ms of
jitter headroom. Key insight: the clock is *arrival*-anchored, so high ping per
se costs nothing — jitter is what kills (and high-ping links jitter more).
Beyond the headroom, remotes freeze then snap (`SnapshotBuffer.GetInterpolated`
clamps at the newest snapshot; no extrapolation).

**Change:** per-connection delay ≈ recent p99 inter-arrival jitter + 1 tick,
clamped ~2..8 ticks, adjusted slowly. Add bounded extrapolation (~2 ticks along
last velocity, then hold) so underruns glide instead of freeze-snapping.

**Coupling** (the doc comment on the constant already warns): the server's
rewind gate reads this number. If delay becomes per-client dynamic, the client
must report its current delay (or the server infer it) or lag comp drifts
dishonest. Test knobs: `NetworkConfig.SimulatedLatencySeconds` / `SimulatedJitterSeconds`
— jitter above ~100ms reproduces the freeze-snap today.

## 9. Input redundancy

Biggest *feel* win on lossy links. **Today:** one lost input packet forces the
server onto `LastIntent` (`Server/GameWorld.Tick`, starved branch) and the
client into a visible reconciliation correction. **Change:** each
`PlayerInputData` carries the last ~3 moves; the server applies any sequences it
hasn't seen. Append-only wire change, per the wire rules in RECIPES.md.

## 10. Clamp lag-comp rewind per client

**Today** (`Server/WeaponSystem.ApplyFire`, the RenderTick gate): any client may
claim a view up to `MaxRewindTicks` = 1s old. That is simultaneously a cheat
surface (always claim max rewind — shoot where enemies *were*) and the
"shot from behind cover" generator for victims.

**Change:** bound each shot's claimed RenderTick to that client's measured
RTT/2 + interp delay + slack (Riptide exposes smoothed RTT per connection), and
pick an absolute cap — ~250ms is the genre norm — as a design decision: beyond
that ping, shooters lead their targets.

## 11. Cap client pendingMoves

**Today** (`Client/Simulation/Player.cs`): unbounded. Legit size is RTT × 30
(~9 moves at 300ms), but a server stall grows it forever and replay cost grows
with it. **Change:** cap ~64; overflow means desynced — clear and resync.

## 12. Deferred and known-accepted: server-paced client simulation

The client's fixed step runs at wall-clock 30Hz regardless of actual server
rate. Under sustained dilation the client outproduces the server: queue pressure
+ silently rejected predicted shots. The real fix is feedback-paced client
ticking (Overwatch-style, driven by server queue depth). That is a project;
with §1 and §3 in place, mispredictions during degradation are livable.
Revisit only if degraded-TPS becomes a supported state rather than an incident.

---

## 13. Unify players into the object system — BEFORE roadmap item 7a

The player/object split (separate spawn messages, registries, view factories,
interpolation paths) was the right call for building prediction in isolation,
but the seams already leak:

- `PlayerStatus` is a workaround-shaped object: player health rides the object
  system because only objects have component replication.
- Shooting a *player* returns their Status **object** from
  `WeaponSystem.Raycast` — the hit pipeline already pretends players are objects.
- `WeaponAttachScript`'s retry loop exists partly because player and object
  spawns travel unrelated, unordered message paths.

**The forcing function is interest management (roadmap 7a):** visibility sets,
enter/leave events, and catch-up-on-enter must be written once over ONE list of
replicated things — or twice, forever, in agreement. Do this merge as the
*prerequisite step of 7a*, not before (nothing nearer on the roadmap needs it,
and the prediction path is working code).

**Shape of the merge:** players become NetObjects with
`Transform | Health | Owner` plus (a) an appended `PlayerState` component
(state flags + LastProcessedSequence, consumed only by the owning client for
reconciliation) and (b) a "streamed" attribute on Transform — every-tick
unreliable, vs. the dirty-flag evented default. `PlayerRegistry` folds into
`ObjectRegistry` with an "is this mine → predict, else interpolate" fork.
`LocalPlayer` / `PlayerMovement` (the prediction core) do not change — only
their transport does. `PlayerStatus` and the raycast weirdness dissolve.

Until then: let new player state ride the `PlayerStatus` object (mildly awkward,
but it's already in the component system, so it merges for free later).

## 14. Chunk-backed spatial queries — follow-on to 7a, not before

**Today:** `Server/WeaponSystem.Raycast` linearly sweeps every Transform-bearing
object plus every player per shot, and `TryPickup` (the O(n²) its own TODO
flags) tests every player against every object, **every tick, unconditionally**.
At current scale this is the right amount of engineering: a ray-sphere test is
nanoseconds, and 100 players at full AK cadence is single-digit ms of CPU per
second. Brute force also has a correctness virtue — no spatial structure to keep
in sync with *rewound* player positions.

**Scaling order:** the pickup scan hits the wall first, not the raycast —
raycasts cost only when someone fires; the pickup scan costs always, and map gen
(crates, health packs, tree blockers) multiplies the object count.

**Change, once 7a's chunks exist** (do NOT build a separate structure for this):
- `TryPickup`: query the player's chunk + 8 neighbors instead of the world.
- `Raycast`: DDA-walk the chunks the ray traverses, testing only entities
  registered there; early-exit at MaxRange or first hit.
- Player rewind vs. the index: either keep players brute-force (capped at 100 —
  cheap forever), or index them by *newest* position and inflate the query
  radius by max-speed × max-rewind (4 m/s × 1s = 4m) so a rewound position can
  never escape the searched chunks. Getting this wrong breaks lag comp silently.

**Interim, only if §5 telemetry ever shows the pickup loop:** run `TryPickup`
every few ticks instead of every tick — 100ms of pickup latency is imperceptible
(evented-state rule: pickups don't need tick-rate fidelity).

---

## Suggested order

1. §2, §3, §1 — cheap, each closes a server-killer.
2. §7 + §8 together — they share the clock work; fixes degradation and jitter both.
3. §9, then §10, then §6, with §5 telemetry woven in throughout.
4. §13 when 7a rises to the top of the roadmap; §14 right after 7a lands,
   reusing its chunks.
5. §12 stays deferred unless degraded TPS becomes normal operation.
