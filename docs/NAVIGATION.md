# NPC Navigation

NPC navigation is a server-authoritative, asynchronous consumer of the same voxel collision and
movement rules used by players. Search is pure code in `Common`; scheduling and path ownership live
in `Server/Ai`.

## Navigation model

A `NavCell(x, y, z)` represents a capsule standing on one surface in a voxel column. Multiple `y`
values allow a bridge, cave floor, and terrain above it to coexist at the same X/Z. The packed
`long` key is the search identity.

`NavTraversal` is the only traversability contract:

- standability resolves the SDF surface and validates slope, capsule clearance, and headroom;
- adjacent walk edges validate endpoints and the continuous midpoint;
- suspicious ascents and narrow diagonals run a bounded simulation through
  `PlayerMovement.Step`;
- jump edges simulate the authoritative fixed-timestep jump through landing;
- fall edges price a step off a ledge as the ballistic time of the drop plus the metres walked to
  the landing, which is up to three cells out because air control carries the actor while he falls.
  They are capped at `MaximumFallCells` (8) — not for survivability, there is no fall damage, but
  because a fall is one way and a bounded search cannot prove the bottom of a ten-metre trench has
  an exit;
- dig edges identify one dirt/grass frontier voxel, but never pretend it is already removed;
- stone is never diggable.

`NavSearch` is deterministic bounded A*. It supports `GoalPosition`, `GoalNear`, and `GoalAwayFrom`,
caps expansion work, checks cancellation every 64 expansions, and returns a useful partial
prefix when a full goal cannot be reached within the request budget. Collinear walk runs are
conservatively reduced without smoothing across jump, fall, or dig actions.

A result also reports `NavPath.ExhaustedReachable`: whether the open set emptied on its own rather
than the search stopping on an expansion budget. When the goal was not reached, that distinguishes
**"cannot get there"** from **"did not have time to"**, and it is the signal the dig escalation below
is built on.

Every edge is priced in estimated execution seconds: walking uses authoritative movement speed,
jumps use fixed-timestep simulation, falls use ballistic time plus horizontal travel, and excavation
adds approach and shovel time plus a conservation penalty. This common unit lets the search compare
a bridge, jump, detour, or cut without a terrain-shape classifier owning the decision.

## Server lifecycle

Each `MobBrain` owns one `NavigationAgent`. Decision code sets destinations; the agent owns:

- the installed `PathFollower`;
- the current request generation and cover/objective request kind;
- partial-path prefetch state;
- a temporarily excluded stalled cell for the next three replans;
- destination state;
- the 60-second meaningful-progress watchdog.

`NavigationSystem` owns a background pool of `min(8, max(1, logical processors / 2))` workers. The
main server thread submits plain request values and drains completed paths; it never waits for A*.
Only the main thread installs paths, changes actors/terrain, or sends messages.

Request priority is:

1. no usable path;
2. combat/cover;
3. objective;
4. partial-path prefetch;
5. roaming.

Only the latest request for an NPC is valid. A replacement supersedes queued or active work, and
search observes cancellation at its amortized budget check.

## Long-route reuse

Objective movement uses a shared key composed from team, squad, and flag. One worker owns a given
shared route at a time so multiple workers do not duplicate the same long search.

A nearby squad member can reuse a completed, dig-free trunk by:

1. solves a short connector to the nearest usable point within 24 m of the shared trunk;
2. following the shared trunk;
3. solves a short exit to its own formation slot.

Bounded partial paths are currently actor-local because sharing an unproved prefix made whole squads
reconnect to the same local minimum. Traversal geometry is still cached across searches. Making
long-route squad reuse effective without restoring that failure is tracked in
[`AI_TODO.md`](../AI_TODO.md). Jump, dig, cover, blocked-cell recovery, and final formation placement
remain per NPC.

## Terrain edits and stale results

`ChunkMap.EditVersion` remains a global diagnostic/search-cache generation. Runtime edits also stamp
the affected terrain chunks. A path records every chunk its segments cross plus a one-chunk apron.
An unrelated edit no longer clears the path; only a corridor revision mismatch invalidates it.

Workers read the mutable terrain optimistically. Two checks make that safe:

- reconstruction revalidates each standable waypoint; a terrain edit that removes a node returns a
  failed path instead of throwing on the worker thread;
- `NavPathTerrain.TryStamp` rejects a corridor whose chunk changed after the request began.

The main thread revalidates the stamp before installing and while following the path.

## Following and recovery

`PathFollower` turns waypoints into the same normalized intent and jump flag used by client input.
It forward-joins a newly completed asynchronous path so an actor does not walk backward to the old
request origin.

- Partial paths prefetch their next segment with about 12 m remaining.
- Planned jumps keep the same horizontal intent through takeoff, flight, and landing.
- A walk waypoint that is at least 0.2 m uphill and makes no progress for roughly 0.25 s receives a
  one-tick recovery jump. It tries twice before replanning.
- A waypoint stalled for roughly 0.75 s is remembered as blocked and excluded from bounded replans.
- Objective NPCs stop in stable formation positions inside the capture radius and scan rather than
  continually replacing their route.
- A navigating NPC that makes neither 2 m of horizontal progress nor a terrain edit for 60 seconds
  is removed by the server. The activity feed states that it was deleted for being stuck; defending,
  aiming, and reloading do not run this watchdog.

## Digging

Dig-enabled requests use one bounded search. It continues ordinary expansion while collecting local
excavation frontiers, then compares the best executable dig macro against a reached goal or useful
air prefix. The NPC follows the selected prefix, equips its shovel at the dig waypoint, performs one
authoritative dirt/grass bite, and replans from the changed terrain. Stone is never considered
diggable.

Three things about that gate are worth knowing before touching it.

**Exhaustion is the strongest dig signal.** A trench with steep walls can return a pacing path along
the floor even though ordinary movement cannot reach the goal. `ExhaustedReachable` distinguishes
that closed reachable set from a useful route whose search merely hit its expansion budget.

**A dig is a lazy macro, not a hypothetical node.** The search prices approach, the local clearance
work, and the remaining heuristic against reached goals and useful air prefixes. If ordinary
movement exhausts its reachable set, the best executable excavation frontier wins. The dig cell is
never relaxed onto the open set because it is not standable until the server takes the real bite, so
it has no truthful successors yet.

**Consecutive bites commit to a site, not a cell.** The frontier cell moves as the cut advances, so
commitment is positional: a candidate within `NavSearch.DigSiteRadius` (3 m) of the last bite gets a
two-bite score bonus, remembered for five seconds by `NavigationAgent.RememberDigSite`. Without it
every bite invalidated the path, the replan recomputed its best frontier from scratch, a marginally
better cut elsewhere won, and the NPC took one or two voxels before wandering off to start a fresh
hole somewhere else.

Objective, combat, and cover callers may opt into digging. `PathFollower` returns
`PathFollowState.Digging`, and `MobSystem` executes the bite through the authoritative terrain-edit
path. Emergency fighting positions remain a separate local behavior.

## Diagnostics and tests

Run `ai stats` in the developer terminal or dedicated-server console. The one-second report includes:

- average live agents;
- main-thread movement, perception, and cover microseconds per tick;
- worker count and aggregate off-thread search time;
- requested/completed and full/partial paths;
- queue and search p50/p95;
- nodes and returned metres per path;
- traversal-cache hits and shared-route reuses;
- cancellations and spatial invalidations.

Coverage is split across:

- `Common.Tests/NavigationTests.cs`: cells, goals, deterministic A*, partial paths, slopes, bridges,
  jumps, digging, dig escalation (a trench escalates, a bounded prefix does not), terrain-edit
  reconstruction, and corridor revisions;
- `Server.Tests/NavigationSystemTests.cs`: shared-route connectors, reuse, and invalidation;
- `Server.Tests/PathFollowerTests.cs`: jump continuity, digging, stall recovery, and uphill jumps;
- `Server.Tests/NavigationProgressWatchTests.cs`: travel-only stuck deletion;
- `Server.Tests/ConquestNavigationBenchmarkTests.cs`: the saved 32-NPC scale benchmark.

Current failures, performance gates, and proposed long-route work are tracked only in
[`AI_TODO.md`](../AI_TODO.md).
