# Time-costed NPC navigation

## Outcome

NPC navigation uses one bounded A* search whose costs are estimated execution seconds. Walking,
jumping, falling, and excavation compete in the same decision. A wall with an exit routes through
the exit, a trench with a cheaper bridge routes across the bridge, and an actor with no cheaper air
route excavates. Terrain-shape classifiers such as “highest ground within N metres” do not own
movement or cancel a valid route.

This follows Baritone's useful contract rather than its Minecraft-specific representation:

- a movement has an estimated execution cost and an intended result;
- A* compares those costs and may return a useful prefix when its wall-clock budget expires;
- the executor validates the movement against the live world and replans when reality diverges.

The server remains authoritative. Workers return plain plans; only the main thread moves actors or
edits terrain.

## Constraints

1. **No map clone per node or request.** The conquest scenario starts 32 NPCs and the reference
   machine has a 16.6 ms singleplayer frame budget. Speculative `ChunkMap.DeepClone()` is forbidden
   in navigation search.
2. **One bounded search.** A request keeps the existing 25 ms useful-prefix budget, 100 ms failure
   budget, 100,000-node ceiling, and cancellation check every 64 expansions. Digging must not cause
   an unconditional second traversal of the same graph.
3. **Dig probes are local and cached.** A probe may inspect the blocked cardinal edge and nearby SDF
   samples. Its result is cached by start cell, direction, and terrain generation alongside walk and
   jump traversal answers.
4. **No hypothetical global terrain state in the node identity.** A node remains a `NavCell`. Adding
   an arbitrary edit-set dimension makes the search exponential and prevents route sharing.
5. **Excavation is receding-horizon.** Search estimates the complete local action needed to create
   the next passage/tread, but returns the next authoritative shovel target. Each real bite changes
   terrain, invalidates the affected corridor, and replans from truth. Site preference may stabilize
   consecutive bites; it may not suppress A* or choose a direction independently of it.
6. **Stone remains an absolute boundary.** Estimated actions must be executable by
   `SubtractSoil`; they never assume rock can disappear.
7. **Execution recovery remains generic.** A failed takeoff, stale corridor, non-standable live
   actor cell, or repeated movement stall can trigger revalidation/replanning. Recovery may not infer
   strategy from a fixed-radius height scan.

## Cost model

All `g` and `h` values are seconds:

- walk: distance / authoritative walk speed;
- jump: fixed-timestep simulated duration;
- fall: authoritative ballistic estimate or simulation;
- excavation: approach time + estimated shovel time + an explicit conservation penalty;
- heuristic: optimistic remaining distance / maximum movement speed.

`NavCosts.DigOneVoxel` retains the deliberate excavation penalty. It is analogous to Baritone's
additional block-break penalty: the unit is still seconds, but terrain destruction loses ties and
modest detours. The estimate for a staircase/passage counts bounded local clearance work rather than
pretending one bite completes an entire transition.

The search tracks the best excavation frontier while continuing to expand ordinary successors. A
reached goal wins when its actual route cost is cheaper. On a bounded partial result, the useful air
prefix wins unless the excavation macro has a lower estimated total cost. If the ordinary reachable
set is exhausted, an executable excavation frontier wins automatically.

## Plan representation

`NavAction.Dig` remains an execution waypoint because only one real bite may be committed at a time.
Its `NavWaypoint.Position` is the shovel target and its `NavWaypoint.Cell` identifies the blocked
frontier/site. The search-side candidate additionally records:

- the standable node from which the action is reachable;
- the blocked cardinal direction;
- the next shovel target;
- the estimated local macro cost;
- `g + macro + h` for comparison with air routes.

This is intentionally a lazy macro-edge: the estimated result is used for route choice, while its
individual terrain edits are materialized and validated incrementally. It provides the useful part
of a full `(position, hypothetical edits)` search without multiplying node state.

## Implementation stages

### 1. Integrate excavation into the bounded A* pass

- Cache `TryDig` results in `NavTraversalCache` and the per-request traversal memo.
- Extend the dig probe to return a conservative bounded macro-cost estimate.
- Collect dig candidates during the normal neighbor visit whenever `AllowDig` is true.
- Compare the best dig candidate with a reached goal or useful partial by estimated total seconds.
- Remove `NavigationSystem`'s air-only search followed by a second dig-enabled search.

Deliberately not included: hypothetical expansion beyond an uncommitted terrain edit.

### 2. Keep execution authoritative

- Follow the walk prefix to the selected frontier.
- At the dig waypoint, equip the shovel and apply exactly one normal server dig.
- Remember the excavation site briefly, invalidate the changed corridor, and request another
  time-costed path from the new terrain.
- If another actor already cleared the frontier, the new search consumes the now-walkable edge.

Deliberately not included: batch terrain mutation or off-thread world writes.

### 3. Remove strategic escape heuristics

- Remove surrounding-grade caches, fixed escape directions, local escape ownership, and the
  pre-follower `TryEscapeRamp` branch.
- Retain exact-cell headroom recovery only for a live capsule that cannot currently produce a valid
  navigation start; this is execution repair, not route choice.
- Let ordinary A* choose bridges, jumps, detours, and excavation.

### 4. Regression coverage

Required behavior tests:

- every initial conquest NPC leaves its authored spawn base;
- a wide trench with an offset bridge is crossed without excavation when the bridge is cheaper;
- an actor in a deep soil pit excavates to the rim;
- a steep soil frontier is excavated without endless recovery jumping;
- a low soil entrance is cleared with the shovel;
- stone never produces a dig route;
- bounded long routes still return useful air prefixes rather than prematurely excavating.

### 5. Performance gates

Run the existing 32-NPC conquest benchmark and report its wall time, queue p50/p95, expanded nodes,
and traversal-cache hits. Acceptance requires:

- all 32 actors receive useful paths;
- queue p95 remains below the existing 500 ms test ceiling;
- ordinary walk-only searches do not perform a second A* pass;
- a cached repeated search does not repeat dig geometry probes;
- no search or speculative terrain work moves onto the main thread;
- the ordinary fast test suites and solution build pass.

If conquest telemetry violates the queue ceiling, optimize the local dig probe/cache before changing
search budgets or adding hierarchy. A feature that finds better paths by starving the server tick is
not complete.

## Completion criteria

The work is complete when the escape-height heuristic is gone, the real conquest spawn regression
passes, bridge and pit integration tests pass through the same time-costed request path, and the
conquest navigation benchmark remains inside its existing performance ceiling.

## Execution record

Implemented and verified on 2026-08-01:

- replaced the air-only/dig retry sequence with one bounded, time-costed search;
- cached lazy excavation probes and retained one-bite authoritative terrain execution;
- removed the surrounding-height escape controller that preempted ordinary path following;
- separated authoritative actor-start resolution from 3D-nearest authored-goal resolution;
- passed all 42 focused navigation, excavation, and foxhole unit tests;
- passed conquest spawn, offset bridge, deep pit, steep slope, low tunnel, and foxhole integration
  scenarios;
- routed all 32 conquest NPCs in 306 ms wall time with queue p50/p95 of 144.1/161.5 ms, 19 shared
  route reuses, 71,328 traversal-cache hits, and 2,576 expanded nodes. The measured p95 is well below
  the 500 ms ceiling.

Follow-up live execution corrections:

- restored the follower's uphill recovery jump for the full range the movement solver can attempt;
  a temporary 0.65 m rise cap prevented NPCs from mounting the roughly one-metre treads produced by
  staircase excavation and made them push into steep sampled slopes;
- replacement paths now retain their closest unreached walk anchor. This prevents an asynchronously
  replanning NPC from skipping a bridge entrance and steering diagonally toward the next turn;
- the six-metre live pit scenario now reaches the rim in 120 terrain edits instead of 300, while the
  offset bridge still crosses with zero edits and the shallow-depression controls remain walk-only.

Final conquest and excavation corrections:

- bounded prefixes are actor-local. Only complete, dig-free objective routes are shared between
  squad members; sharing an unproved prefix made every member reconnect to the same trench-wall
  local minimum. Traversal geometry remains shared between all searches;
- partial answers do not commit a deep descent until the search has proved its continuation. An
  actor already at the bottom may still choose a time-costed staircase, while an actor on the rim
  keeps searching for a bridge;
- an explicit goal-rise gate lets an unwalkably steep uphill objective choose staircase excavation
  when lateral air prefixes make no useful progress. This uses start/goal geometry, not a scan for
  the highest nearby terrain;
- generic clearance recovery now samples only the 2x2 SDF footprint of each one-metre route cell.
  Staircase shoulder recovery removes the capsule's actual collision contact instead of sweeping a
  4x4 apron. `StaircaseDigTargetsStayInsideOneMetreCorridor` pins the one-metre lateral footprint;
- planned bridge jumps align to a 0.15 m takeoff anchor and execute atomically. A worker result that
  completes after takeoff is discarded rather than replacing the landing corridor in mid-air;
- the real-map acceptance integration runs one complete 16-NPC team at a time, disables stuck
  relocation, and requires both central flag objects to be fully captured. Team 1 captured both in
  6,845 ticks with 0 terrain edits; team 2 captured both in 4,399 ticks with 3 edits. Neither team
  emitted a stuck event, and the test rejects more than 64 terrain edits;
- after making partial routes actor-local, the 32-NPC benchmark completed in 469 ms wall time with
  queue p50/p95 of 177.8/382.2 ms, 192,941 traversal-cache hits, and 6,528 expanded nodes. The p95
  remains below the 500 ms ceiling.
