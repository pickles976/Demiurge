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
- dig edges identify one dirt/grass frontier voxel, but never pretend it is already removed;
- stone is never diggable.

`NavSearch` is deterministic bounded A*. It supports `GoalPosition`, `GoalNear`, and `GoalAwayFrom`,
caps expansion/time work, checks cancellation every 64 expansions, and returns a useful partial
prefix when a full goal cannot be reached within the request budget. Collinear walk runs are
conservatively reduced without smoothing across jump or dig actions.

A result also reports `NavPath.ExhaustedReachable`: whether the open set emptied on its own rather
than the search stopping on a node or time budget. When the goal was not reached, that distinguishes
**"cannot get there"** from **"did not have time to"**, and it is the signal the dig escalation below
is built on.

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

A nearby squad member:

1. solves a short connector to the nearest usable point within 24 m of the shared trunk;
2. follows the shared complete or partial trunk;
3. solves a short exit to its own formation slot.

Partial trunks are useful immediately and can be extended by a later prefetch. Jump, dig, cover,
blocked-cell recovery, and final formation placement remain per NPC.

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

Navigation is air-only first. A second, dig-allowed search runs only when
`NavSearch.NeedsDigEscalation` reports that the first one failed to reach the goal *and* exhausted
every cell ordinary movement could reach. The NPC then equips its shovel, performs the same
authoritative two-bite dirt/grass edit as a player, invalidates the local corridor, and searches the
changed terrain again. Pit escape chooses rising dirt targets.

Three things about that gate are worth knowing before touching it.

**Exhaustion is the signal, not distance covered.** The escalation used to be gated on the air-only
pass returning zero waypoints, which happens only for an actor sealed in on every side. A trench with
steep walls returns a *pacing* path — the NPC can walk the floor and cannot climb out — so the dig
pass never ran and `AllowDig` was effectively dead code. Progress-toward-goal thresholds were tried
and rejected: a bounded prefix of a good long route legitimately closes only a few metres and would
have escalated, doubling search cost on every long route.

**A dig does not compete on `f = g + h`.** One frontier bite costs about 4 s — sixteen metres of
walking — while buying at most a metre of heuristic, so no cost comparison would ever choose one. The
escalation gate alone decides whether digging is on the table; inside a dig-allowed search the score
only ranks bites against each other, cheapest to reach and closest to the goal once cut. This is why
the dig cell is never relaxed onto the open set: it is not standable until the server has taken a real
bite, so it has no successors to expand.

**Consecutive bites commit to a site, not a cell.** The frontier cell moves as the cut advances, so
commitment is positional: a candidate within `NavSearch.DigSiteRadius` (3 m) of the last bite gets a
two-bite score bonus, remembered for five seconds by `NavigationAgent.RememberDigSite`. Without it
every bite invalidated the path, the replan recomputed its best frontier from scratch, a marginally
better cut elsewhere won, and the NPC took one or two voxels before wandering off to start a fresh
hole somewhere else.

Digging is still disabled for combat and cover paths. `MobSystem.RequestPath` lets the caller decide
rather than forcing it off, but no combat caller opts in yet: the cover follower discards the dig
target and has no `PathFollowState.Digging` case, so a dig waypoint would stall the NPC on it forever.
Emergency fighting positions are dug separately by `MobSystem.DigEmergencyCover`, capped at 1 m below
the surrounding grade so an NPC can always jump back out of a hole it made itself.

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
- `Server.Tests/NavigationSystemTests.cs`: shared complete/partial trunks and extension;
- `Server.Tests/PathFollowerTests.cs`: jump continuity, digging, stall recovery, and uphill jumps;
- `Server.Tests/NavigationProgressWatchTests.cs`: travel-only stuck deletion;
- `Server.Tests/ConquestNavigationBenchmarkTests.cs`: the saved 32-NPC scale benchmark.

Current benchmark on the 16-core development host: initial long-route queue p95 fell from 639 ms to
about 232 ms, and all 32 conquest actors received useful paths in about 447 ms.

## Measurement-gated work

Do not add these until conquest telemetry shows shared trunks and spatial invalidation are still
insufficient:

- reverse objective flow fields;
- an 8–16 m coarse portal/chunk hierarchy;
- pooled search-node storage;
- capsule-checked string pulling beyond current collinear walk reduction;
- detailed fall edges.
