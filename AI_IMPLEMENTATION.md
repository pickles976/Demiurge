# AI Implementation Plan

**Goal:** Server-authoritative tactical AI for infantry combat on deformable voxel terrain —
navigation, perception, engagement, cover, squad coordination, digging, and a commander tier.

## Implementation status

- **Step 1 foundation is implemented:** packed multi-level navigation cells, collision-derived
  standability and slope checks, deterministic bounded A*, goals, partial paths, and headless
  coverage. Jump edges are validated by simulating the authoritative capsule and fixed-timestep
  jump solver. Steep upward edges also receive a bounded no-jump movement simulation so sharp SDF
  ledges become jump actions while genuinely walkable slopes remain ordinary traversal. Deliberate
  fall edges remain a later navigation extension.
- **Step 2 walking integration is implemented and scaled:** a replacement-aware prioritized worker
  pool (half the logical processors, clamped to 1–8), per-NPC `NavigationAgent`, chunk-corridor
  invalidation, shared squad objective trunks, partial-path prefetch, waypoint recovery, and the
  existing `PlayerMovement.Step` as the sole movement authority.
- Run `ai stats` in the developer terminal or dedicated console for the latest one-second window:
  average live agents, mob movement time on the server tick, off-thread path-search time, and path
  request/completion counts.
- **Step 3 perception is implemented:** each NPC checks at most one enemy per tick against range,
  field of view, and terrain LOS, and writes sightings into a five-second confidence-decaying
  `ContactMemory`. LOS probes the ordered body points in `GunConfig.AimHeights` — centre mass, then
  the head — and remembers which one answered so `CombatBehavior` aims where perception could
  actually see. The second ray is only cast when centre mass is blocked. Probing centre mass alone
  made a target peeking over cover with only its head exposed completely invisible, and the aim point
  it used sat inside the old torso-sphere hit volume, so such a target was also unhittable; see
  `docs/networking/Shooting.md`.
- Shared ballistics, recoil/spread, and hit-probability math from **Step 4** already exists from the
  projectile weapon work.
- **Step 5 engagement is implemented:** NPCs hold while engaged, acquire with a reaction delay,
  settle aim at a bounded turn rate, compensate projectile drop, choose controlled or suppressive
  AK fire from hit probability, suppress actors on near misses, and use the authoritative ammo,
  cadence, projectile, friendly-fire, damage, and reload paths. Engagement is bounded by
  `MaxEngagementRange` (70 m): a contact beyond it is still believed but does not make combat own the
  NPC's movement. Without that gate any contact the squad shared — perception reaches 100 m — froze
  men nowhere near the fight in place, aiming across the map instead of manoeuvring. A base of fire
  additionally fires in a **suppressing** mode aimed at the contact's last known position on a slower
  cadence; ordinary fire requires current visibility, which made suppression impossible against
  precisely the target it exists for.
- **Step 6 cover is implemented:** one globally budgeted, event-driven spatial query samples 13
  deterministic nearby navigation cells against up to two believed threats. It distinguishes
  crouch-blocked/stand-clear fighting positions from full concealment, scores travel and escape
  routes in pure `Common` logic, sends the winner through the existing navigation worker, invalidates
  it on terrain edits or material threat movement, and makes an arrived NPC crouch/peek on a bounded
  cadence. Fully blocked positions also test validated lateral cells for corner peeks, which are
  preferred over popping over low cover; cover paths disable jump edges so an agent routes around
  the obstacle instead of vaulting it. Cover-query time and count are included in `ai stats`.
- **Step 7 squad blackboards are implemented:** direct sightings enter shared contact memory after
  11 server ticks (~367 ms), short cover leases prevent squadmates from selecting the same fighting
  position, and two engagement plus two advance permits rotate every three seconds. This produces
  bounded focus fire without adding replication messages or bypassing individual LOS checks. The
  board also carries a shared Conquest objective: squads path to the nearest neutral, enemy, or
  threatened friendly flag, spread into stable positions inside its capture radius, and remain
  assigned there to defend or retake it. `SquadBlackboard.Centre` is recomputed from live member
  positions each re-formation pass; it used to be the mean of members' *spawn* points and was never
  updated, so every squad-relative calculation including commander travel costing kept measuring
  from base.
- **Step 7b dynamic squads are implemented:** membership re-forms from live proximity once per second
  in pure, deterministic `SquadFormation`. A cohesive squad keeps its members, over-strength squads
  shed their outliers, separated members join the nearest squad with room, and under-strength squads
  merge into neighbours — that last pass is what handles a lone unit standing beside a squad, since
  cohesion measured against a squad's own centre makes a squad of one trivially cohesive with itself.
  Membership was previously fixed at spawn: `MobBrain.SquadIndex` was init-only and the per-team
  counter never decremented, so a squad could never re-form and a replacement NPC became a permanent
  squad of one that the commander then sent to its own objective. Leases are released on the granting
  board when a member changes squad, and empty squads are dropped so the commander stops planning for
  units that no longer exist.
- **Step 7c squad tactics are implemented:** pure `SquadTactics` turns the squad's primary believed
  threat into a per-member role at 2 Hz. It derives a threat axis from the live squad centre, assigns
  sticky left/right envelope sides, and orders one man per side to **bound** while the rest are
  **base of fire**. Two properties are worth stating because they replace machinery that could not
  express them:
  - **Nobody moves until somebody is set.** With no member in position the whole squad goes to ground
    and digs in first.
  - **Leapfrog is emergent, not a state machine.** The rule is "the man farthest from the threat
    bounds next", so once he has moved past his partner the partner is farthest and takes the next
    bound. The previous three-second advance-token lease was a timer and could not express "wait
    until he is set".

  Each completed bound shortens that member's standoff (45 m opening, 12 m floor) and narrows the
  envelope, so the two sides converge rather than walking past. A bound reuses the cover-destination
  lane deliberately — claims, path priority, and arrival detection are the same problem as moving to
  a fighting position — and going set on arrival is what hands the next bound to the partner.
  `MobBrain.AtCover` used to be terminal: an arrived NPC never moved again unless the threat shifted
  five metres, which was the single largest reason nothing ever leapfrogged.
- **The Step 8 grenade slice is implemented:** an NPC can use a recently lost believed contact to
  probe just behind intervening cover, solve a low ballistic arc, reject terrain-blocked or
  friendly-unsafe throws, and reserve the throw on its squad board so grenades arrive singly rather
  than as an eight-NPC volley. Mortar and heavy-machine-gun items remain prerequisites for the
  crew-served portions of Step 8.
- **The Step 9 digging slice is implemented:** normal air-only navigation always runs first, and a
  second dig-allowed search runs only when `NavSearch.NeedsDigEscalation` says the first one
  exhausted every cell ordinary movement can reach without arriving. That predicate is the fix for
  "NPCs stuck in a trench never dig out": the escalation used to be gated on the air-only pass
  returning *zero waypoints*, which happens only for an actor sealed in on every side, so a trench —
  where an NPC can walk the floor but not climb the walls — returned a pacing path forever and
  `AllowDig` was effectively dead. Exhaustion is the right signal rather than distance covered,
  because a bounded prefix of a good long route is exactly what the search budget is supposed to
  return and must not pay for a second search.

  A dig deliberately does **not** compete on `f = g + h`. One frontier bite costs seconds while
  buying at most a metre of heuristic, so no cost comparison would ever choose one; whether digging
  is on the table at all is the escalation gate's decision, and the search only ranks bites against
  each other. Consecutive bites also commit to a **site** rather than a cell (the frontier cell moves
  as the cut advances): a candidate within `NavSearch.DigSiteRadius` of the last bite gets a two-bite
  score bonus, remembered for five seconds by `NavigationAgent`. Without it every bite invalidated
  the path, the replan recomputed its best frontier from scratch, a marginally better cut elsewhere
  won, and the NPC took one or two voxels then wandered off to start a fresh hole.

  Emergency fighting positions are capped at `MaxFoxholeDepth` (1 m) below the surrounding grade,
  measured behind the digger so it samples untouched ground rather than its own hole. That dig was
  unbounded, so an NPC under sustained fire bit the same spot every `TicksPerDig` until it stood in a
  pit it could neither jump out of (jump clears 1.5 m) nor excavate out of. A base of fire now digs
  when *ordered to hold*, not only while rounds are landing, and termination is the cover scorer
  itself: once the cut classifies the spot as a `FightingPosition` the cover query accepts it and
  digging stops, which is "deep enough to peek over, low enough to crouch behind" without a depth
  constant deciding it. Stone is never diggable. **Still deferred:** digging during a bound — the
  bound follower has no `PathFollowState.Digging` case, and executing a bite needs the shovel, pitch
  and yaw that `CombatBehavior` holds for aiming, so a digging man is not simultaneously shooting.
  Commander-designated trench construction also remains Step 10 work.
- **Step 10 strategic flag allocation is implemented:** a one-hertz commander for each team ranks
  threatened friendly flags, contested/active captures, untouched neutral points, enemy points,
  and quiet rear security. Equal-priority assignments minimize squad travel with a stability bias;
  urgent flags create a second reinforcement slot before low-priority objectives are covered.
  One-member/incomplete squads remain valid units, and squad count now varies during a match as
  membership re-forms. The singleplayer battle uses `conquest` with 16 NPCs per team, spread around
  the authored team spawn clusters.
- **Reactive search and movement are implemented:** accepted enemy gunshots within 60 m create
  bounded investigation goals, idle defenders rotate a regular visual scan, repeatedly stalled
  terrain waypoints are temporarily excluded from replanning, ineffective long-range rifle fire
  advances through closer cover or bounded forward waypoints, and dirt pit walls generate rising
  shovel targets. A hostile projectile passing within 2 m creates a two-second believed threat at
  its firing position, interrupting ordinary travel for cover selection or emergency dirt digging.
  Stone remains non-diggable.
- **Failure reporting is implemented:** kills, flag neutralizations/captures, and the reason for a
  60-second navigation-stuck deletion are published through the reliable activity feed. Terrain
  edits racing a background search now fail/retry reconstruction instead of throwing from a worker.
- **Respawn clears the brain:** `MobSystem.OnRespawn` drops cover, contacts, the under-fire window,
  the heard-gunshot goal, flank side and bound progress. Respawn reset the replicated `ServerPlayer`
  but never `MobBrain`, so a mob returned to base still believing it was at cover, under fire, and
  holding a contact — and stood there crouched behind nothing.
- **`ai track` draws the NPC debug overlay** (`docs/COMMANDS.md`). Client-side only; beacons, facing,
  and a clustering layer that links NPCs within 4 m, which is the view for judging bunching.

**Architecture:** AI produces *intent* and nothing else. The same `Vector3` direction and
`PlayerStateFlags` a client input packet carries goes into `PlayerMovement.Step`. Human fire enters
through validated `PlayerFireData`; NPC fire enters through `TryFireAi`, then both converge on the
same authoritative ammo, cadence, spread, projectile, damage, and reload core. Every layer below
stays shared, so there is never a second movement or shooting implementation to keep in sync. Pure
logic (search, ballistics, scoring, memory) lives in `Common` with no Stride dependency and is
therefore testable headless; orchestration, threading and world access live in `Server`.

The current cross-system boundaries are summarized in `docs/ARCHITECTURE.md`; the navigation
lifecycle and diagnostics are in `docs/NAVIGATION.md`.

**This is a system, not a feature.** The detailed step sections below preserve the implementation
sequence and design reasoning. This status section is authoritative when an older step description
still talks about the smaller implementation that existed when the step was written.

---

## Global constraints

- **Never `git commit`.** Leave every change unstaged for review.
- **60 FPS client, 30 TPS server, in singleplayer on a Beelink SER5, with 32–64 agents alive.** A
  requirement, not an aspiration. The binding case is singleplayer, where `ServerHost` steps from
  the client's `Update()` and the server tick shares the 16.6 ms frame budget instead of owning
  33 ms. A step that cannot hold this on that machine is not done. See Performance budget below.
- AI state adds no per-NPC wire stream. Mobs already replicate as `ServerPlayer`s through
  `ServerToClientId.PlayerSpawn` / `PlayerPosition` and `ObjectReplication`. The activity feed is a
  match-event message, not replicated AI decision state.
- `Common` must not reference Stride. Anything in `Common/Navigation`, `Common/Ballistics`,
  `Common/Ai` is `System.Numerics` only.
- `DemiurgeSharp.csproj` globs `**/*.cs` from the repo root. New directories under `Common/`,
  `Server/` are already covered by existing `<Compile Remove>` lines; **no new directory at the
  repo root** without adding one.
- All cadence in server ticks, derived from `NetworkConfig.TickRate` (30). Never hardcode 30.
- Test complex pure logic and narrow server boundaries: search correctness, coordinate/angle math,
  probability, shared-route scheduling, path following, strategic allocation, and watchdog timing.
  Use playtests for emergent arbitration and presentation.
- Run `dotnet test --filter "Category!=Benchmark"` after each step (~1s).

---

## File structure

| File | Responsibility |
|---|---|
| `Common/Navigation/NavCosts.cs` | Seconds-per-action table, derived from `PlayerMovement` / `Digging` |
| `Common/Navigation/NavTraversal.cs` | `Standable` / `TryStep` — the one traversability predicate |
| `Common/Navigation/NavGoal.cs` | `INavGoal` + `GoalPosition`, `GoalNear`, `GoalAwayFrom` |
| `Common/Navigation/NavHeap.cs` | Binary heap open set with decrease-key |
| `Common/Navigation/NavSearch.cs` | The A\* itself; time-budgeted, best-partial fallback |
| `Common/Navigation/NavPath.cs` | Result: waypoint list + whether it reached the goal |
| `Common/Ballistics/BallisticsConfig.cs` | Per-weapon MOA and recoil terms |
| `Common/Ballistics/Spread.cs` | MOA↔radians, `SigmaRadians`, recoil accumulate/decay |
| `Common/Ballistics/HitEstimate.cs` | `Probability` — Rayleigh CDF over σ, range, target size |
| `Common/Ai/ContactMemory.cs` | Believed enemy contacts with confidence fade |
| `Common/Ai/CoverScore.cs` | Scores a candidate position given threats and LOS results |
| `Common/Ai/GunshotHearing.cs` | Team/distance gate and investigation lifetime |
| `Common/Ai/StrategicObjectivePlanner.cs` | Deterministic team-relative flag allocation |
| `Common/ActorIds.cs` | Mob actor-id range; the client's only way to tell an AI from a remote human |
| `Client/Rendering/NpcTrackerScript.cs` | `ai track` debug overlay: beacons, facing, clustering |
| `Server/Ai/NavigationSystem.cs` | Prioritized worker pool, shared routes, caching, metrics |
| `Server/Ai/NavigationAgent.cs` | One NPC's destination, request, path, blocked-cell and progress lifecycle |
| `Server/Ai/NavigationProgressWatch.cs` | Travel-only 60-second stuck detection |
| `Server/Ai/PathFollower.cs` | Path → intent, waypoint advance, replan triggers |
| `Server/Ai/Perception.cs` | FOV + budgeted LOS raycasts, writes `ContactMemory` |
| `Server/Ai/SquadBlackboard.cs` | Roster, live centre, shared contacts, position claims, engage/advance tokens, tactical orders |
| `Server/Ai/SquadFormation.cs` | Pure proximity re-grouping: cohesion, capacity, orphan joins, under-strength merges |
| `Server/Ai/SquadTactics.cs` | Pure doctrine: threat axis, flank sides, base-of-fire vs bound, envelope positions |
| `Server/Ai/CombatBehavior.cs` | Engage / suppress / hold decision and aim; range gate and suppressing fire mode |
| `Server/Ai/GrenadeBehavior.cs` | Safe low-arc throws against recently occluded contacts |
| `Common/Ballistics/ThrowSolver.cs` | Launch angle for a lobbed projectile; terrain clearance along the arc |
| `Common/Ai/AreaTargeting.cs` | Best splash centre given believed contacts, with a friendly exclusion |
| `Server/Ai/MobBrain.cs` | Per-unit arbitration; owns the above for one mob |
| `Server/Ai/CommanderAi.cs` | Low-frequency team flag assignments |
| `Server/MobSystem.cs` | *Modified* — delegates to `MobBrain`, keeps spawn/roam helpers |
| `Server/ActivityFeedSystem.cs` | Reliable kill, flag, and stuck-deletion match events |

`Server/Ai/CrewWeapon.cs` remains a planned seam for mortar/heavy-MG lug, deploy, fire, and pack
states; those items do not exist yet.

---

## Step 1 — Navigation core

Pure `Common`. No server wiring; nothing observable in game yet. This is the step with real tests.

**Files:** create `Common/Navigation/{NavCosts,NavTraversal,NavGoal,NavHeap,NavSearch,NavPath}.cs`,
`Common.Tests/NavigationTests.cs`.

### The node

A node is an integer voxel coordinate `(x, y, z)` meaning *the agent's feet rest on the surface of
cell y in column (x,z)*. This is Baritone's model and it ports because our capsule is
**narrower than a voxel** — `PlayerMovement.Body` is radius 0.4 (0.8 m across) against 1 m cells,
needing two cells of headroom for its 1.8 m height. Multi-level columns (a tunnel under a hill)
fall out for free as two y values at one column.

Pack to a `long` key. X and Z need the full world range, Y needs 7 bits (`ChunkConstants` height
128):

```csharp
public static long Key(int x, int y, int z)
    => ((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | (long)(y & 0xFFF);
```

### Traversability — one predicate, derived from the movement solver

**This is the load-bearing rule of the whole step.** If the planner and `PlayerMovement.Step`
disagree about what is walkable, agents path onto 56° faces and grind. `NavTraversal` must call the
same `TerrainCollision` sampling and the same `PlayerMovement.MaxSlopeCos` the solver uses.

```csharp
public static bool Standable(ChunkMap map, int x, int y, int z, out float surfaceY)
{
    surfaceY = 0f;
    // Feet rest on the top face of cell y.
    var feet = new Vector3(x + 0.5f, y + 1f, z + 0.5f);

    if (!TerrainCollision.TryDeepestContact(map, PlayerMovement.Body, feet, out var contact))
        return false;                                   // nothing under us, or unloaded

    // Ground must be within snap range and not steeper than the solver will let us stand on.
    if (contact.Distance < -PlayerMovement.SkinWidth) return false;         // buried
    if (contact.Distance > PlayerMovement.GroundSnapDistance) return false; // floating
    if (contact.SurfaceNormal.Y < PlayerMovement.MaxSlopeCos) return false; // too steep

    surfaceY = feet.Y - contact.Distance;
    return true;
}
```

`TryStep` then validates an edge between two standable nodes: the height change must be within
`GroundSnapDistance` upward (a step up beyond that needs a jump, which is not in this step) and the
midpoint must also be standable, so an agent cannot cross a 1-voxel-wide chasm by diagonal.

### Movement set — smaller than Baritone's

Because our terrain is a smooth SDF rather than a block lattice, **ascend and descend are not
separate move types.** Height change is continuous and the slope check is the only gate. That
collapses Baritone's traverse/ascend/descend family into one:

- 4 cardinal + 4 diagonal traverses (diagonals also require both adjacent cardinals passable, or
  agents cut corners through rock)
- fall: drop to a lower standable surface in the same column, cost from the gravity integration

Not in this step: jump, parkour, dig.

### Costs, in seconds

Everything commensurate so heterogeneous actions compare, which is the entire reason Baritone can
decide "tunnel or go around" with one search.

```csharp
public static class NavCosts
{
    public const float Inf = 1_000_000f;   // summed, so never float.MaxValue

    public const float WalkOneMetre = 1f / PlayerMovement.WalkSpeed;        // 0.25 s
    public const float DiagonalMetres = 1.41421356f;

    /// <summary>Free-fall time for a drop of h metres under PlayerMovement.Gravity.</summary>
    public static float Fall(float h) => MathF.Sqrt(2f * h / PlayerMovement.Gravity);

    /// <summary>Fastest anything moves. The heuristic divides by this, so it stays admissible.</summary>
    public const float MaxSpeed = PlayerMovement.SprintSpeed;               // 6 m/s
}
```

Heuristic is euclidean distance / `MaxSpeed`.

### The search

Structurally a direct port of `AStarPathFinder.calculate0`, which is worth following closely — it
is a single flat loop with no allocation in the hot path:

- `Dictionary<long, NavNode>` for the node map, `NavHeap` for the open set with decrease-key
- check the clock **every 64 nodes**, not every node (`(expanded & 63) == 0`)
- two budgets: a primary timeout, and a longer failure timeout that only applies while the search
  has not yet found anything worth walking
- track best-so-far at several heuristic weightings simultaneously and return the first that got
  further than a few metres. Baritone uses `{1.5, 2, 2.5, 3, 4, 5, 10}`; start with the same and
  tune later. This is what makes a timed-out search still useful — an agent under fire wants *a*
  path now, not the best path in 300 ms.
- skip successors whose cost is `>= NavCosts.Inf` rather than branching per move type

### Goals are predicates

```csharp
public interface INavGoal
{
    bool IsInGoal(int x, int y, int z);
    float Heuristic(int x, int y, int z);
}
```

`GoalPosition` (exact cell), `GoalNear` (within a radius — for "get to the flag"), `GoalAwayFrom`
(inverted heuristic — retreat, and it costs nothing extra to have). Later steps add
`GoalHasLineOfSight` without touching the search.

### Tests — `Common.Tests/NavigationTests.cs`

`SyntheticTerrain` already builds exactly the worlds this needs:

- `Flat()` — straight-line path, cost equals distance / `WalkSpeed` within tolerance
- `Slope(30)` — path exists and climbs; `Slope(70)` — no path up it
- `Wall(x)` — path routes around, and a goal on the far side of a full-height wall in a bounded
  map returns "no path" rather than exhausting the budget
- `Solid()` — start is not standable, search fails immediately
- `Slab(low, high)` — two standable levels in one column resolve to distinct nodes
- determinism: the same query twice returns the identical path
- budget: a search given 1 ms returns a partial path rather than blocking

**Deliberately not in this step:** dig edges, jump, hierarchy or portal graphs, flow fields, any
danger or exposure term in the cost, all server wiring, all squad and combat behaviour.

---

## Step 2 — Mobs navigate

Wires step 1 into the server. Observable: mobs stop walking into hillsides.

**Files:** create `Server/Ai/{NavigationSystem,PathFollower}.cs`; modify `Server/MobSystem.cs`,
`Server/GameWorld.cs`.

### Threading

The search runs on a worker while `GameWorld.Tick` and `TerrainSystem.Apply` keep running.
`ChunkMap`'s lookup is a `ConcurrentDictionary`, but a voxel write is not atomic with respect to a
concurrent read. That is tolerable — a torn read of a 2-byte `Voxel` yields a wrong edge cost, not
a crash — so we do not copy the map. Instead:

- add an `EditVersion` counter to `ChunkMap`, incremented by every `TerrainEdits` write
- record the version when a search starts and discard stale results
- use one worker and a FIFO with per-mob replacement

Digs are rate-limited to two per second per player, so version churn is low.

That was the initial integration. The current implementation replaces the global discard with
chunk-corridor revision stamps, uses a prioritized bounded pool, cancels superseded active work,
and serializes ownership only for duplicate searches of the same shared squad route. Reconstruction
rechecks standability because edits may race the optimistic terrain read.

### Path following

`PathFollower` converts the current waypoint into the intent `MobSystem` already produces —
`LastIntent`, `State`, `Yaw` — and `PlayerMovement.Step` remains the only thing that moves anybody.
Advance to the next waypoint within an arrival radius; request a replan when the path is exhausted,
its corridor revision moved, or progress stalls. Partial paths prefetch before their final waypoint;
short uphill stalls try a bounded recovery jump before replanning.

`MobSystem.RandomSurfacePoint` stays as the destination chooser; it just becomes the seed for a
`GoalNear` instead of a straight-line target.

### Timing instrumentation lands here, not at the end

Every performance instruction in this plan says "do not optimize until measurement justifies it",
and that instruction is worthless without measurement. Add per-system tick timing now, while the
numbers are small enough to sanity-check by hand:

- accumulate elapsed microseconds per system per tick — movement, pathfinding, perception, the rest
  as they arrive — plus agent count and paths-requested/completed
- surface it through the existing developer terminal, which already survives session transitions
- a rolling average over ~1 s, not per-tick spam

Then spawn 64 mobs in a singleplayer session and read it. Two things come out of that run:

1. **The acceptance check for every later step** — 64 mobs alive, client holding 60 FPS, server
   holding 30 TPS, **measured on the SER5**. Re-run it at the end of each step; the step is not done
   if it regresses. A passing run on a development box proves nothing on its own — it is a smoke
   test, and the SER5 is the gate.
2. **A verdict on the movement prediction.** The Performance section argues movement will dominate
   and that `TerrainCollision`'s per-voxel `ConcurrentDictionary` lookups are why. This confirms or
   kills that before anything gets built on it.

**Deliberately not in this step:** any reaction to enemies, replanning under fire, multiple mobs
coordinating, AI think-rate LOD, and the `TerrainCollision` block-fetch optimization itself — that
is a `Common` change benefiting players equally, and it should be done on the strength of the
measurement rather than bundled into an AI step.

---

## Step 3 — Perception

Mobs stop being omniscient. Observable: a mob walks past an enemy it never saw.

**Files:** create `Common/Ai/ContactMemory.cs`, `Server/Ai/Perception.cs`; modify `Server/Ai/MobBrain.cs`
(created here as a thin owner).

`ContactMemory` is pure and testable: a small list of believed contacts, each `(actorId, position,
lastSeenTick, confidence)`. Confidence decays with elapsed ticks; entries drop below a floor and
are forgotten. The agent's world model is a **separate data structure from the world** — every
later layer queries this, never the live player list. That is what makes cover scoring cheap:
there are three or four believed threats to test against, not every actor on the map.

`Perception` runs the sensing: an FOV cone test first (cheap dot product against `Yaw`), then
`TerrainRaycast.Cast` from eye height (`Digging.EyeHeight`, already the shared number) to the
target's centre. LOS is the expensive query and must be budgeted: each mob tests a bounded number
of rays per tick, round-robin across candidates, acting on results up to a few hundred ms stale.
Real soldiers have latency too.

**Deliberately not in this step:** sharing contacts between mobs, hearing, reacting to being shot,
any change to what mobs *do* with what they see.

---

## Step 4 — Ballistics and spread

Needed before engagement, because "should I fire" is a probability question. Pure `Common`, tested.

**Files:** create `Common/Ballistics/{BallisticsConfig,Spread,HitEstimate}.cs`,
`Common.Tests/BallisticsTests.cs`; modify `Common/WeaponConfig.cs`.

### MOA describes the shooter, not the weapon

Minute of angle: 1 MOA = 1/60°. **By shooting convention an MOA figure is group *diameter*, not
half-angle** — getting that wrong is a silent factor-of-two error.

```csharp
public const float RadiansPerMoa = MathF.PI / (180f * 60f);   // 2.9089e-4
```

The mechanical figure a weapon shoots from a rest is not the interesting number. What decides hits
is the whole shooter-weapon-condition system: stance, breathing, suppression, recoil. So dispersion
is composed from independent sources, which add as variances:

```csharp
public static float TotalMoa(float weapon, float stance, float condition, float recoil)
    => MathF.Sqrt(weapon * weapon + stance * stance + condition * condition + recoil * recoil);
```

Quadrature rather than multiplication, because it gives the property we want for free: **the
largest term dominates and small ones vanish.** A 4 MOA rifle and a 6 MOA SMG become
indistinguishable the moment either is fired standing, with no special case written for it.

**Weapon** — bench accuracy. Flavour; barely observable once a human holds it.

| Class | MOA |
|---|---|
| Bolt-action / sniper | 2 |
| Rifle | 4 |
| SMG | 6 |
| Pistol | 8 |

**Stance** — aimed, settled, stationary. The term that actually matters.

| Stance | MOA |
|---|---|
| Crouched | 14 |
| Standing | 30 |

Prone does not exist in the game today and is out of scope. If it is ever added it slots in as a
tighter stance term (~5 MOA) with no other change to this model.

**Condition** — additive in quadrature; decaying terms tick down per second.

| Condition | MOA | Decay |
|---|---|---|
| Hip fire (not `Aiming`) | 150 | instant on ADS |
| Walking | 20 | instant |
| Sprinting | 60 | instant |
| Post-sprint breathing | 40 | over ~3 s |
| Suppressed | 50 | over ~2 s after last near-miss |
| Stance change | 30 | over ~0.5 s |

**Recoil** — accumulates per shot, decays toward zero.

| Class | Per shot | Decay/s | Cap |
|---|---|---|---|
| Bolt-action | 50 | 60 | 50 |
| Rifle | 12 | 60 | 80 |
| SMG | 8 | 70 | 60 |
| Pistol | 10 | 55 | 50 |

The "cooldown between shots" falls out of the recoil term rather than needing its own mechanism:
the player sees the cone bloom, and the AI computes how long until it decays under its firing
threshold and waits. If a hard cadence penalty is wanted as well it is an extra term on
`WeaponStats.TicksPerShot`, not a redesign.

Every state this model reads already exists on `PlayerStateFlags` — `Crouching`, `Aiming`, `Moving`,
`Sprinting` — and already rides the wire, so no enum change is needed. Suppression is the one new
piece of per-actor state, and it is server-side only.

### Hit probability — Gaussian, not uniform

Impacts are a circular bivariate normal about the aim point, so radial miss distance is Rayleigh
distributed. That is both more physically honest than spreading shots evenly across a disc and no
more expensive to evaluate.

**The MOA spec has to be pinned to a containment fraction or the conversion is ambiguous.** Take
the quoted MOA as the diameter of the circle containing **95%** of shots:

```csharp
/// <summary>
/// Angular standard deviation for a composed `TotalMoa` (GROUP DIAMETER containing 95%).
/// Solving 1 - exp(-r95^2 / 2σ^2) = 0.95 gives r95 = σ·sqrt(-2·ln 0.05) = σ·2.4477.
/// </summary>
public static float SigmaRadians(float moa)
    => moa * 0.5f * RadiansPerMoa / 2.4477f;

/// <summary>Chance one shot lands within `targetRadius` at `range`. Rayleigh CDF.</summary>
public static float Probability(float sigmaRadians, float range, float targetRadius)
{
    float sigma = sigmaRadians * range;
    if (sigma <= 1e-6f) return 1f;
    float k = targetRadius / sigma;
    return 1f - MathF.Exp(-0.5f * k * k);
}
```

`targetRadius` is `GunConfig.HitRadius` (0.6 m), reduced when the target is partially behind cover.

### What the numbers produce — check these before tuning anything

The tables above were sized against this model, not guessed. A coin-flip shot on an exposed target
needs σ ≈ 0.51 m, which at 200 m is about **43 MOA of total dispersion** — an order of magnitude
past any weapon's bench figure, which is why the stance and condition terms carry all the weight.

Rifle, aimed, exposed target (R = 0.6 m):

| | 100 m | 200 m |
|---|---|---|
| Crouched | 1.00 | 1.00 |
| Standing | 1.00 | 0.75 |
| Standing, sprinting | 0.68 | 0.25 |
| Standing, recoil at cap | 0.50 | — |
| Standing, hip fire | 0.20 | — |

Rifle, aimed, **peeking** target at 200 m (R = 0.2 m — head and shoulders over cover):

| Stance | P(hit) |
|---|---|
| Crouched | 0.49 |
| Standing | 0.14 |

The second table is the design. Against a man standing in the open, stance barely matters — a good
shooter does hit that, and both stances read 1.00. Against someone using cover, stance is the whole
engagement: crouching is worth 3.4× against a peeker and nothing at all against someone exposed, so
the mechanic teaches itself and step 6's cover work is what gives it meaning.

Two other outcomes worth preserving if these numbers get retuned: an AK's first standing shot at
100 m is 0.996 while its shot at recoil cap is 0.50, which is the first-shot-accurate /
spray-inaccurate curve for free; and a crouched sniper at 200 m — well inside the projectile safety
distance — is 0.998 on an exposed target but 0.51 on a peeker, making long-range duels about
exposure discipline rather than about the rifle.

Tests: MOA→radian conversion against the table above; `SigmaRadians` reproduces r95 at the 95%
point; `Probability` is monotonically decreasing in range, approaches 1 as range → 0, and equals
`1 - exp(-0.5)` ≈ 0.393 when σ exactly equals the target radius; recoil decays back to `BaseMoa`
and never below it.

At the time of this step, projectile simulation and drop solving were deliberately deferred. They
are now implemented: `WeaponSystem` sweeps server-side projectiles under gravity, and NPC aim
compensates drop. Weapons have no gameplay `MaxRange`; `ProjectileMotion.SafetyDistance` is a 1 km
runaway-projectile bound.

---

## Step 5 — Engagement

Mobs shoot at what they believe they can see. Observable: a firefight.

**Files:** create `Server/Ai/CombatBehavior.cs`; modify `Server/Ai/MobBrain.cs`,
`Server/WeaponSystem.cs` (extract a mob-callable entry point).

`WeaponSystem.ApplyFire` currently takes `PlayerFireData` from a client message. Mobs need the same
path — extract the validation-free interior so a mob can fire with a server-computed origin and
direction while players keep the gated entry. **Mobs must go through the same ammo, cadence and
reload gates**, or their weapon state stops matching what `ObjectReplication` shows.

The decision is a mode selection, not one threshold:

| Mode | Condition | Behaviour |
|---|---|---|
| **Aimed fire** | P(hit) above the role's threshold, LOS confirmed this tick | Fire single shots, let recoil decay between |
| **Suppressive fire** | P(hit) low but the enemy's *position* is known and reachable | Fire bursts at the cover, accepting misses |
| **Hold** | No current LOS, projectile trajectory obstructed, reloading, or ammo low and no immediate threat | Reposition or reload |

Suppression is the point of automatic fire in this game, so a low P(hit) means *suppress*, not
*hold*. Thresholds differ per role and that is what makes the SMG and the sniper behave differently
with the same code.

Suppression has to *do* something, and step 4 already gives it a mechanism: rounds landing near an
actor add the 50 MOA suppressed term to their dispersion, decaying over ~2 s. So "suppress while a
squadmate flanks" works because the arithmetic says it does, not because a behaviour was scripted to
pretend. Applies to players and mobs identically — it is a term in the shared spread model, not an
AI feature.

**AI has no latency and will be inhumanly accurate.** Players' shots go through prediction,
`NetworkConfig.MaxRewindTicks` compensation and jitter; a server-side AI aiming at exact positions
has none of that. Deliberate error is a balance requirement, not flavour: a reaction delay before
first shot, aim that settles toward the target over time rather than snapping, and a sampled offset
inside the spread cone.

**Deliberately not in this step:** moving while engaging, cover, coordination, reacting to
suppression directed at them.

---

## Step 6 — Cover

**Files:** create `Common/Ai/CoverScore.cs`; modify `Server/Ai/MobBrain.cs`.

Sample-and-score, the EQS/TPS pattern — not a search. Generate candidates around the agent and
between the agent and its believed threats, then score each. `CoverScore` stays pure by taking LOS
results as input rather than casting rays itself; `MobBrain` does the casting under the same budget
as step 3.

Cover is a *(position, direction, threat)* triple, and the useful distinction comes from testing
two heights:

- blocked from threats when crouched, clear when standing → **fighting position** (the good one)
- blocked at both heights → **concealment**, use for movement not for shooting
- clear at both → not cover

Score also on path cost to reach (step 1 gives this for free), whether the agent can shoot back,
and escape routes. Direct LOS raycasts are sufficient — bullet trajectories are flat enough at
these ranges that the arc case does not arise, and indirect fire ignores cover entirely, which is
what trenches are for.

**Deliberately not in this step:** digging cover, claiming positions so two mobs do not pick the
same one (that needs step 7), overhead cover against indirect fire.

---

## Step 7 — Squad blackboard

**Files:** create `Server/Ai/SquadBlackboard.cs`; modify `Server/Ai/MobBrain.cs`, `Server/GameWorld.cs`.

Coordination is a data structure, not a messaging system. Agent-to-agent messages produce ordering
bugs and combinatorial chatter; a shared board does not. One blackboard per squad holding:

- **contacts** — the union of members' `ContactMemory`, merged with a delay so sharing is not
  instantaneous
- **claims** — position reservations, so two mobs do not take the same cover
- **tokens** — a bounded number of "may engage" and "may advance" permits

Fire-and-movement then emerges rather than being scripted: one unit holds the suppress token while
another claims a forward position, and they swap. This is also where the classic Halo/F.E.A.R.
trick lives — capping how many agents engage simultaneously is a few lines and buys the largest
single improvement in how a fight reads.

Teams need to exist by this step. Mobs are `ServerPlayer`s with `IsMob`; add a team id there and to
`RuntimePlacementKind.Mob` placements so the editor can place both sides. That is an append to the
placement data — follow `RECIPES.md`, and remember the enum order **is** the protocol.

**Deliberately not in this step:** commander tier, objectives, anything theater-scale.

---

## Step 8 — Crew-served and indirect weapons

Grenades, mortars and heavy machine guns. The player grenade and its first AI use now exist.
Mortars and heavy machine guns remain gated on their PVP-track weapon implementations.

Without this step the plan describes a system that deadlocks. Steps 5–7 are all direct fire gated
on LOS, and under those rules alone cover is strictly dominant: a unit in a good fighting position
cannot be killed, suppression has no follow-through, and step 9's trenches become a win button.
Grenades and mortars are the counter-pressure that makes the whole loop dynamic — historically they
are *why* trenches needed traverses and dispersal.

### What the three have in common

Not "aiming a gun" but **committing a resource**. A rifle shot is reversible in 100 ms; emplacing an
MG costs seconds of vulnerability and then anchors the unit to that spot. Three shared pieces:

**1. Arc solving** (`ThrowSolver`). Given launch point, target point, projectile speed and
`PlayerMovement.Gravity`, solve the launch angle. There are two solutions — flat and lofted — and
the choice is made by sampling the arc against terrain for clearance. **This is where trajectory
testing genuinely earns its cost**, unlike direct fire where a ray is sufficient. Shared by grenade
and mortar.

**2. Area targeting** (`AreaTargeting`). You aim at a *position*, not an actor, so this reuses step
6's generate/test/score machinery far more than step 5's aim solve. Score candidate impact points by
believed contacts inside the blast radius, minus a hard exclusion for any friendly within a safety
radius, minus the thrower's own position. Output is a point.

Two intents worth separating, because they want different scores: **kill** (maximise contacts in
blast) and **flush** (land it where the target must leave cover into someone else's field of fire).
Flushing is what makes grenades work with the squad layer rather than beside it.

**3. Commitment** (`CrewWeapon`). A lug → deploy → fire → pack state machine with an explicit
vulnerability window. Per `TODO.md` the lugger is slow and exposed. The decision is not "should I
fire" but "is this position worth being stuck in for the next thirty seconds," which is a different
calculus and belongs to the squad tier, not the unit.

### Where they differ

| | Grenade | Mortar | Heavy MG |
|---|---|---|---|
| Fire | Indirect, short | Indirect, long | Direct, emplaced |
| Needs LOS | No | **No — needs a spotter** | Yes |
| Carried by | Everyone | Assigned lugger | Assigned lugger |
| Commitment | Seconds | Emplaced | Emplaced |
| Deforms terrain | Yes | Yes | No |

**The mortar is the first thing that genuinely requires step 7's blackboard.** Its firer has no LOS
by design — the target comes from another unit's `ContactMemory`, shared across the squad. Every
layer before this works with per-agent knowledge and treats sharing as a nicety; here it is
load-bearing, and a mortar with no spotter simply cannot fire.

**Role assignment** is the other squad dependency. `TODO.md` calls for delegating the lugging job to
the least useful unit in the group; that is a claim on the blackboard, and the lugger then wants
routing behind cover rather than leading an advance.

**The MG's real role is suppression economics.** In step 5's mode selection it should sit in
suppressive fire almost permanently — sustained, cheap, and effective against targets it has no
chance of hitting. That is what makes squad-level fire-and-movement affordable.

### Terrain deformation is a new case

Grenades and mortars are **unplanned digs**. They hit step 2's edit-version machinery from a
direction nothing else does: the AI *receives* a terrain change it did not cause and did not
predict. Nav paths already replan off the version counter, but **cover scores need invalidating
too** — a fighting position can stop being one mid-fight, and an agent that keeps trusting a cached
score will sit in a crater being shot.

**Deliberately not in this step:** commander-level siting of emplacements, AI constructing or
fortifying positions, counter-battery fire, and any indirect weapon the player cannot also use.

---

## Step 9 — Digging

The step the terrain exists for.

**Files:** modify `Common/Navigation/{NavCosts,NavTraversal,NavSearch}.cs`, `Server/Ai/MobBrain.cs`.

A dig is an **edge in the path graph with a cost**, not a behaviour. Add a move type that enters a
non-standable cell at the cost of clearing it. Two consequences worth stating plainly:

- "dig toward the enemy trench" is A\* deciding a tunnel is cheaper than crossing open ground
- **"staircase out of a hole" needs no code at all** — it is A\* finding an upward diggable step,
  because the graph already knows one is traversable at a price

Cost, from constants that already exist: `Digging.TicksPerDig` is `TickRate / 2` and
`Digging.ClicksPerVoxel` is 2, so one voxel costs **1.0 s** — about four metres of walking at
`WalkSpeed`. That ratio makes digging cheap enough that an unbiased search will tunnel constantly.
Add a multiplier knob (Baritone carries `blockBreakAdditionalPenalty` for exactly this reason); it
is the dial between "AI goes around" and "AI goes through", and it wants to be tunable at runtime.

Two-pass search: air-only first, digging-allowed only if that fails or exceeds a cost ceiling.
Allowing solid volume as traversable explodes the search space, and most paths do not need it.

Execution goes through `TerrainSystem.Apply` so the edit broadcasts to every client exactly as a
player's dig does.

**Deliberately not in this step:** trench templates, commander-designated construction,
connectivity maintenance under collapse.

---

## Step 10 — Commander and the AI battle

**Files:** create `Server/Ai/CommanderAi.cs`; modify map format for team spawns and flag zones.

The implemented commander sets theater objectives and the squad tier decides how. It evaluates
flag ownership, partial capture state, friendly/enemy presence, travel, and assignment stability
once per second. Primary slots distribute squads across useful objectives; emergency reinforcement
slots can pull a second squad to a threatened friendly flag or active capture.

Trench designation remains later work: individual agents digging cover converge on disconnected
foxholes, because **connectivity is the one property that does not emerge from local decisions**.
A commander stamping a template oriented against the threat axis is the simplest thing that
supplies it.

Worth testing the cheap alternative first: dug space is free to traverse afterwards, so a squad
repeatedly pathing the same axis with digging allowed and exposure priced into the cost may extend
and reuse its own cut — trenches as worn paths rather than as construction. One squad and one
machine gun will show whether that converges or produces mush.

Then the AI Battle map from `TODO.md`: team spawn points, flag zones, wave respawn, and player
commander abilities.

---

## Step 11 — Fire and movement

**Files:** create `Server/Ai/{SquadFormation,SquadTactics}.cs`,
`Server.Tests/{SquadFormationTests,SquadTacticsTests}.cs`; modify `Server/Ai/SquadBlackboard.cs`,
`Server/Ai/MobBrain.cs`, `Server/Ai/CombatBehavior.cs`, `Server/MobSystem.cs`.

The step that turns N individuals into a squad. Everything before it was *arbitration* — two
engagement tokens, two advance tokens, position claims — while each NPC independently decided to
close on the threat. That is why a squad read as a crowd converging on one point.

### A squad is a group, not an index

Membership re-forms from live proximity once per second. `SquadFormation` is pure and deterministic:
members are considered in actor-id order and squad indices are the lowest free ones, so the same
battlefield always produces the same grouping regardless of dictionary iteration order.

Four rules, in order: a cohesive squad keeps its members; an over-strength squad sheds the members
farthest from its centre; separated members join the nearest squad with room; under-strength squads
merge into neighbours. **The merge pass is the non-obvious one.** Cohesion is measured against a
squad's own centre, so a squad of one is trivially cohesive with itself and would keep its index
forever — which is exactly the "adjacent unit with no squad" case. Merging is ordered (smallest into
largest, ties by lowest index) so it cannot oscillate.

Cohesion (30 m) is deliberately more generous than the join radius (20 m). Squads legitimately spread
out to envelope, and re-forming a squad mid-bound would throw away the very plan that spread it.

### Roles, at 2 Hz

`SquadTactics` maps the squad's primary believed threat — the shared contact nearest its live centre,
one per squad, because a squad splitting its plan across two enemies does neither — onto a role per
member. It is pure, and terrain is deliberately absent: this produces intent, and navigation and
cover selection resolve it against the actual field.

- A **threat axis** runs from the live squad centre to the threat. `SquadTactics.LateralAxis` is the
  single definition of its right-hand perpendicular, so the geometry and the Left/Right labels cannot
  drift apart.
- **Sides are sticky.** A member already committed to going left keeps going left; a replan
  mid-manoeuvre must not walk a flanker back across the axis through the beaten zone.
- One man per side **bounds**; everyone else is **base of fire**.

Two properties replace machinery that could not express them:

**Nobody moves until somebody is set.** With no member in position, every role is base of fire: the
squad goes to ground and digs in before anyone walks into fire. A lone set man on a side will not
abandon overwatch to bound unless the other side has fire down.

**Leapfrog is emergent, not a state machine.** The rule is only "the man farthest from the threat
bounds next". Once he has moved past his partner, the partner is farthest and takes the next bound,
and they alternate for as long as the fight lasts. There is no hand-off record and no timer. The
previous advance token was a three-second lease with a cooldown, which can express "your turn is
over" but never "wait until he is set" — the one condition that matters.

Each completed bound increments that member's `BoundIndex`, which shortens its standoff (45 m
opening, 12 m floor) and narrows the envelope, so the two sides converge on the threat instead of
walking past it. `BoundIndex` resets when contact is broken, so the next fight opens at full standoff
rather than resuming a closing sequence against an enemy who is no longer there.

### Executing a role

A bound reuses the cover-destination lane on purpose: claims, path priority, and arrival detection are
the same problem as moving to a fighting position, and `CoverKind.Advance` already means "a temporary
forward navigation point". Going set on arrival is what hands the next bound to the partner, and
dropping the Advance destination lets the ordinary cover query find real cover where the man now
stands.

Two gates had to be opened for any of this to move at all:

- **`AtCover` was terminal.** An arrived NPC never moved again unless the threat shifted five metres.
  This was the single largest reason nothing leapfrogged.
- **A base of fire could not query cover.** The query was gated on `mayAdvance`, which is false for a
  man with no wish to advance — so a man ordered to hold and shoot from open ground stood in it, never
  went set, and therefore nobody in the squad was ever cleared to bound.

A base of fire also digs when *ordered to hold*, not only while rounds are landing, and suppresses:
`CombatBehavior` gained a mode that fires at the contact's last known position on a slower cadence.
Ordinary fire requires current visibility, which made suppression impossible against precisely the
target it exists for — one with its head down. The receiving half already existed, so this closes the
loop and a base of fire now actually pins its target.

Engagement is bounded at 70 m. Any shared contact used to make combat own an NPC's movement, so men
nowhere near the fight stood still aiming across the map.

### Deliberately not in this step

- **Digging during a bound.** The bound follower has no `PathFollowState.Digging` case, and executing
  a bite needs the shovel, pitch and yaw `CombatBehavior` holds for aiming — a digging man is not
  simultaneously shooting, which is a doctrine decision rather than plumbing.
- **Foxhole connectivity.** Cuts still converge on disconnected positions; see the trench note in
  Step 10. Nothing here supplies connectivity.
- **Roles on the wire.** They are server-only, so `ai track` cannot label them. Judging the doctrine
  means watching behaviour and the clustering layer, not reading state off the screen.
- **Cover query budget.** `CoverQueriesPerTick` is still 1 across all NPCs, so with 32 agents a man
  waits on the order of a second to go set and the opening of a fight is slower than it should look.
  Raising it is a measured decision; `ai stats` reports cover cost per tick.

---

## Performance budget — 32–64 agents at 60 FPS / 30 TPS on a Beelink SER5

### Do the arithmetic first, because it decides the architecture

The target is fixed: **60 FPS client, 30 TPS server, singleplayer, on a SER5.** Everything below is
derived from it rather than chosen.

60 FPS gives the client 16.6 ms a frame. The server ticks at 30 Hz, so in singleplayer — where
`ServerHost` is stepped from the client's `Update()` and cannot have its own thread — a server tick
lands inside every second frame and must fit *alongside* rendering, not instead of it. That is the
binding case. A dedicated server owning the whole machine is the easy one and never the one that
breaks.

Rendering, terrain streaming and meshing already live in that frame. On the SER5 budget the AI at
**~2 ms per tick**:

**2 ms ÷ 64 agents ≈ 30 µs per agent per tick.**

### What the reference machine changes

A SER5 is a Ryzen 5 5500U/5560U class part: 6 cores / 12 threads, Radeon Vega 6–7 iGPU,
dual-channel DDR4 shared between CPU and GPU, 15–25 W sustained. That shape, not its raw speed,
is what should steer decisions here.

- **The CPU has cores available for bounded computation workers.** Move expensive pure AI
  computation off the main thread, but do not assume a low frame rate is GPU-bound: the measured
  1920x1080 regression was an SDL device-reset loop. The navigation pool is capped at eight; a
  thread per agent is still prohibited.
- **Only computation moves.** Anything creating a Riptide `Message` or mutating world state stays on
  the main thread and returns results through a queue — the same discipline `TerrainState` uses. The
  Riptide message pool is unsynchronised and this is not negotiable.
- **Scattered memory access costs twice**, because it competes with the iGPU for the same DDR4.
  This raises the value of the `TerrainCollision` block fetch below, and of keeping agent state in
  flat arrays, above what either would be worth on a discrete-GPU machine.
- Sustained mobile clocks mean single-thread throughput is well under a desktop's. Treat any budget
  measured on a development box as optimistic until it has been re-read on the SER5.

For scale (order-of-magnitude, unmeasured, but the ratios hold): one 200 m voxel raycast is roughly
5–20 µs — a third to half of one agent's entire budget. One A\* over a few thousand nodes is
0.5–2 ms — the whole AI budget for the whole tick.

So the governing rule is not "write fast code", it is:

> **Nothing runs every tick for every agent.** Every expensive operation is scheduled, amortized,
> shared across a squad, or event-driven.

Accept that and 64 agents is comfortable. Ignore it and 8 is hard.

### Movement is probably the dominant cost, and it is not AI

`PlayerMovement.Step` runs per agent per tick unconditionally. Counting the voxel fetches:

- `TerrainCollision.TrySample` = `TrySampleCell` (8 corners) + `TryGradient` (6 × `TrySampleRaw`,
  each another 8 corners) = **56 `TryGetVoxel` calls**
- `TryDeepestContact` = `CapsuleBody.SampleCount` (3) × that = **168 calls**
- A typical walking tick runs 3–6 `TryDeepestContact` between `Move`, `Resolve` and `ProbeGround`
  → **roughly 500–1000 voxel fetches per agent per tick**

At 64 agents that is 32,000–64,000 fetches per tick, ~1–2 M/s. And **every one of them does a
`ConcurrentDictionary` lookup**: `ChunkMap.TryGetVoxel` calls `ChunkAt` then `Get` on every single
voxel (`Common/Voxel/Chunks.cs:127`).

**The fix is local, safe and bit-identical.** All seven sample points inside one `TrySample` lie
within ±0.5 of the same position (`GradientStep = 0.5f`), so every corner they need fits in a
**3×3×3 = 27 voxel block**. Fetch that block once, resolving the chunk once, then compute all seven
trilinear evaluations from local memory:

- 56 scattered fetches → 27 contiguous ones
- 56 dictionary lookups → 1 (a 3×3×3 block sits inside one 16×16 chunk unless it straddles a border)
- extend the same chunk resolution across `TryDeepestContact`'s three capsule samples, since they
  share an XZ column

Expect roughly an order of magnitude off the dominant cost, with no behavioural change at all —
and it speeds up players and client prediction too, not just AI. **Measure before and after; this
is the one optimization worth doing on evidence rather than on faith, because the evidence is
already in the call counts above.**

The other movement lever is structural and specific to mobs: **a mob walking a validated path does
not need the collision solver at all.** The nav graph in step 1 already proved every node standable
before the path was issued. So on-path movement over unchanged terrain can advance along waypoints
and take Y from the surface, and only agents in combat, falling, off-path, or on terrain whose edit
version moved need the full solver. Safe because mobs are replicated positions, not client-predicted
— it only has to look right, not match a prediction.

### Pathfinding

- Already off-thread and queued. Add a **cap on node expansions** per request so one hopeless query
  cannot monopolise the worker.
- **Share paths across a squad.** Eight men going to one objective is one search plus formation
  offsets, not eight. A free 8×, and it reads better than eight independent paths.
- **Flow fields for many-to-one.** Thirty agents converging on a flag is one Dijkstra pass over the
  map, shared by all of them — cost scales with the map, not the agent count. The single biggest
  structural win at this agent count (GAIP 1 ch. 23).
- Throttle replans per agent per second.
- Flat A\* over a 1 km map is ~10⁶ surface nodes. Time-budgeted segmentation (step 1) is the first
  answer and is the same code a hierarchy would sit on top of. **Do not build portals or hierarchy
  until measurement says the budget is the problem.**

### Navigation cleanup and scale roadmap

Playtesting with two teams of sixteen exposed a stop/start failure that could not be solved by merely
raising the partial-path distance. Before this cleanup, the implementation performed one flat A*
per NPC on one worker, returned bounded prefixes, and invalidated every path whenever any terrain
edit incremented the map-wide `EditVersion`. A shovel bite at one flag could therefore stop and
replan actors at every other flag.

Implement the following in order. Each layer is independently measurable and remains useful if a
later layer is deferred:

1. **Spatial terrain revisions and path corridors.**
   - Keep the existing global version as a cheap diagnostic generation.
   - Also increment a revision for every terrain chunk touched by an edit.
   - A completed path records the chunks crossed by its waypoints plus a one-chunk safety apron.
   - Following and in-flight validation reject a path only when a recorded chunk revision changes.
   - Terrain edits outside the route no longer interrupt movement.

2. **One navigation lifecycle owner per NPC.**
   - Move destination, pending request generation, partial-path prefetch, blocked-cell avoidance,
     path following, and stuck detection behind a `NavigationAgent`.
   - `MobSystem` chooses a destination and consumes a movement/action result; it must not duplicate
     request bookkeeping.
   - Superseding a request changes the agent generation so stale results cannot be installed.

3. **Interruptible, prioritized worker scheduling.**
   - Requests with no usable path outrank speculative prefetches; cover paths outrank idle roaming.
   - A search checks cancellation at the same amortized 64-expansion boundary as its time budget.
   - Record queue latency, search latency, expansions, returned path metres, partial/complete
     counts, spatial invalidations, stale/cancelled work, and cache hits.
   - Prefer a stable expansion slice over using wall-clock time as the only bound.

4. **Share the long route, not the final formation placement.**
   - A squad owns one strategic corridor from its current centre toward its assigned flag.
   - Members locally connect to that corridor and locally leave it for their formation offsets.
   - Jump, fall, digging, cover, and blocked-cell recovery remain per-NPC actions.
   - With four-person squads, the conquest scenario reduces thirty-two long objective searches to
     at most eight shared trunks.

5. **Many-to-one objective fields and hierarchy.**
   - If measurements still show long-route pressure, cache reverse integration fields per active
     flag and terrain-region revision so multiple squads reuse the same work.
   - Above that, introduce an 8–16 m coarse chunk/portal graph. Coarse A* supplies a full-map
     corridor; detailed voxel A* only solves the next 15–30 m execution window.
   - Do not make detailed jump/dig edges part of the coarse graph.

6. **Cache the expensive traversal contract.**
   - Within one spatial terrain revision, cache standable cells, surface heights, walk edges, and
     authoritative jump probes.
   - Reuse search dictionaries/heaps or move node state to pooled value storage after profiling.
   - Add a second path worker only after spatial invalidation and shared routes prevent both workers
     from duplicating immediately-stale work.

7. **Path presentation.**
   - String-pull consecutive walk nodes against the authoritative capsule to remove unnecessary
     one-metre steering changes.
   - Never smooth across jump, fall, or dig actions.
   - Smoothing improves motion readability; it is not a substitute for fixing queue starvation or
     invalidation churn.

Acceptance for the conquest scenario:

- unrelated terrain edits do not clear an NPC's route;
- no stationary wait occurs between healthy partial segments under the normal worker queue;
- `ai stats` reports queue/search percentiles, average returned metres, invalidation and cache data;
- shared strategic searches scale with squads/objectives rather than actor count;
- a deliberately non-progressing navigating NPC is deleted after sixty seconds with an activity
  feed reason, while combatants and flag defenders are never classified as stuck.

Implementation status (2026-07-29):

- Implemented: chunk-scoped edit revisions and stamped path corridors; per-NPC
  `NavigationAgent`; priority scheduling; active/stale request cancellation at 64 expansions;
  traversal caching across searches; an adaptive bounded worker pool (up to eight workers);
  immediately reusable and progressively extended squad objective trunks with parallel per-member
  connectors; validated spawn/destination snapping; distance-based partial-path prefetch;
  conservative collinear walk smoothing; queue/search p50 and p95 plus the other counters above in
  `ai stats`; and the sixty-second stuck deletion watchdog.
- Measurement-gated: reverse objective flow fields, the coarse portal hierarchy, pooled search
  storage, and capsule-checked string pulling beyond collinear runs. Build these only if conquest
  telemetry still shows long-route queue pressure after shared trunks and spatial invalidation are
  active.

The saved 32-NPC `conquest` benchmark is the scale gate. On the 16-core development host, the first
optimization pass reduced initial long-route queue p95 from 639 ms to approximately 232 ms, with all
32 actors receiving useful paths in approximately 447 ms total. The benchmark remains tagged
`Category=Benchmark` so ordinary unit runs stay deterministic.

### Line of sight

Naive is 64 agents × 64 targets × 30 Hz ≈ 123,000 raycasts/s, which is hopeless. Four cuts that
compound:

- **Broad-phase by `ChunkIndex`.** Bucket actors spatially and only consider nearby chunks —
  O(n²) becomes O(n·k).
- **2.5D rejection before any DDA.** Test the ray against per-chunk maximum column height; if it
  clears every chunk it crosses, it is unobstructed with zero voxel marching. Large win on open
  terrain, and the heightmap already exists.
- **Global ray budget, round-robin.** A fixed number of rays per tick shared by every agent. Each
  agent-target pair refreshes on the order of once a second, which reads as reaction latency rather
  than as a bug.
- **Symmetry and sharing.** LOS(A,B) = LOS(B,A), and squadmates metres apart can reuse one result
  through the blackboard.

### Everything else

- **Think-rate LOD by relevance**, not distance alone: engaged and near a player 10 Hz, engaged and
  far 4 Hz, moving with no contact 1–2 Hz, idle 0.2 Hz. Most agents sit in the cheap buckets.
- **Cover scoring is event-driven** — run it when an agent decides to reposition, with 8–16
  candidates, drawing on the same ray budget. Never continuous.
- **No allocation in the tick loop.** No LINQ, agent state in flat arrays rather than object graphs.
  `GameWorld.ActorSnapshot()` currently does `Select().OrderBy().ToArray()` per call — fine for two
  mobs, not for sixty-four.

### The escape hatch

If 64 fully simulated agents will not fit, the standard answer is to stop simulating the ones nobody
can observe: resolve engagements with no nearby player **statistically** rather than tick by tick,
and promote them to full simulation when a player approaches. Better to have this written down now
than to discover at step 10 that the frontline cannot be as wide as the design assumes.

## References

### Read at the step, not up front

| Step | Reading |
|---|---|
| 1 Navigation core | GAIP 3 ch. 21 (3D nav over sparse voxel octrees — the closest published match to our node model), GAIP 2 ch. 32 (voxel navmesh coverage), GAIP 1 ch. 17 (A\* architecture optimizations), Millington 4.1 |
| 1 (search speed, if needed) | GAIP 3 ch. 22 (goal bounding), ch. 23 (faster Dijkstra on uniform grids), GAIP 2 ch. 15 (subgoal graphs) |
| 2 Mobs navigate | **Millington 4.7 "Dynamic Pathfinding" and "Interruptible Pathfinding"** (p. 274–277) — this is exactly the terrain-edit-invalidation problem; GAIP 3 ch. 20 and Millington 4.5 for path smoothing, because raw lattice A\* output looks robotic; GAIP 2 ch. 21 (dynamic obstacles) |
| 4 Ballistics | No reference needed — the maths is closed-form and specified above. |
| 5 Engagement | Behavioral Mathematics (Mark) for response-curve shape; Wisdom 1 for F.E.A.R.-era engagement patterns |
| 6 Cover | **GAIP 3 ch. 26 "Guide to Effective Auto-Generated Spatial Queries"** and GAIP 1 ch. 26 "Tactical Position Selection" — both are the generate/test/score pattern this step implements; GAIP 1 ch. 27, GAIP 3 ch. 24, Millington 6.1 |
| 7 Squad | Wisdom 1 (van der Sterren, "Squad Tactics: Team AI and Emergent Maneuvers"); Wisdom 2 (van der Sterren, "Squad Tactics: Planned Maneuvers") for fire-and-movement; GAIP 2 ch. 20 (hierarchical group navigation) |
| 8 Crew-served / indirect | Wisdom 1 (van der Sterren, terrain reasoning) for firing-position selection; GAIP 1 ch. 26 again, since area targeting is the same generate/test/score pattern as cover |
| 9 Digging | No direct prior art. Nearest analogues are Factorio's destructible path costs and Dwarf Fortress / RimWorld region connectivity. |
| 10 Commander | GAIP 2 ch. 29–30 (influence maps — how to represent a frontline); Millington ch. 6 |
| Scale, only if measured | GAIP 1 ch. 20 (precomputed pathfinding for large worlds), ch. 23 (flow field tiles), Millington 4.6 (hierarchical pathfinding) |

### Shelf notes

- The file named **"AI Game Programming Wisdom 4"** in `books/` is actually **Wisdom 1** (2002,
  ISBN 9781584500773). The metadata is wrong, the content is not.
- **Wisdom 2** is a scanned copy with no text layer and is 124 MB, over the direct-read limit —
  it cannot be searched, only paged through as rendered images
  (`pdftoppm -f N -l M -r 100 -jpeg <file> out`). Printed page ≈ PDF page − 34. A text-layer copy
  would be a real speedup for step 7 and Sebastian is looking for one.
- **`GameAIPro3.pdf`** was converted from the `.azw3` with calibre; the original is unreadable by
  the tooling here. Use the PDF.

### Source

- Baritone (`github.com/cabaletta/baritone`) — `pathing/calc/AStarPathFinder.java` for the loop
  shape, `pathing/movement/MovementHelper.java` for keeping every traversability predicate in one
  file, `api/pathing/movement/ActionCosts.java` for costing in a real time unit.
- Recast/Detour (`github.com/recastnavigation/recastnavigation`) — if step 1's graph ever needs to
  become a navmesh, this is the reference for tiled incremental rebuild. Not needed as written.
