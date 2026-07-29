# AI Implementation Plan

**Goal:** Server-authoritative tactical AI for infantry combat on deformable voxel terrain —
navigation, perception, engagement, cover, squad coordination, digging, and a commander tier.

## Implementation status

- **Step 1 foundation is implemented:** packed multi-level navigation cells, collision-derived
  standability and slope checks, deterministic bounded A*, goals, partial paths, and headless
  coverage. Jump edges are validated by simulating the authoritative capsule and fixed-timestep
  jump solver. Steep upward edges also receive a bounded no-jump movement simulation so sharp SDF
  ledges become jump actions while genuinely walkable slopes remain ordinary traversal. Deliberate
  fall and dig edges remain later navigation extensions.
- **Step 2 walking integration is implemented:** one replacement-aware navigation worker,
  request-generation and terrain-edit invalidation, waypoint following, one-second stall replans,
  and the existing `PlayerMovement.Step` as the sole movement authority.
- Run `ai stats` in the developer terminal or dedicated console for the latest one-second window:
  average live agents, mob movement time on the server tick, off-thread path-search time, and path
  request/completion counts.
- **Step 3 perception is implemented:** each NPC performs at most one enemy-only FOV/terrain-LOS
  ray per tick and writes sightings into a five-second confidence-decaying `ContactMemory`.
- Shared ballistics, recoil/spread, and hit-probability math from **Step 4** already exists from the
  projectile weapon work.
- **Step 5 engagement is implemented:** NPCs hold while engaged, acquire with a reaction delay,
  settle aim at a bounded turn rate, compensate projectile drop, choose controlled or suppressive
  AK fire from hit probability, suppress actors on near misses, and use the authoritative ammo,
  cadence, projectile, friendly-fire, damage, and reload paths.
- **Step 6 cover is implemented:** one globally budgeted, event-driven spatial query samples 13
  deterministic nearby navigation cells against up to two believed threats. It distinguishes
  crouch-blocked/stand-clear fighting positions from full concealment, scores travel and escape
  routes in pure `Common` logic, sends the winner through the existing navigation worker, invalidates
  it on terrain edits or material threat movement, and makes an arrived NPC crouch/peek on a bounded
  cadence. Fully blocked positions also test validated lateral cells for corner peeks, which are
  preferred over popping over low cover; cover paths disable jump edges so an agent routes around
  the obstacle instead of vaulting it. Cover-query time and count are included in `ai stats`.
- **Step 7 squad blackboards are implemented:** NPCs are assigned deterministically to team-local
  squads of four. Direct sightings enter shared contact memory after 11 server ticks (~367 ms),
  short cover leases prevent squadmates from selecting the same fighting position, and two
  engagement plus two advance permits rotate every three seconds. This produces bounded focus fire
  and alternating fire-and-movement without adding replication messages or bypassing individual
  LOS checks. The board also carries a shared Conquest objective: squads path to the nearest
  neutral, enemy, or threatened friendly flag, spread into stable positions inside its capture
  radius, and remain assigned there to defend or retake it.
- **The Step 8 grenade slice is implemented:** an NPC can use a recently lost believed contact to
  probe just behind intervening cover, solve a low ballistic arc, reject terrain-blocked or
  friendly-unsafe throws, and reserve the throw on its squad board so grenades arrive singly rather
  than as an eight-NPC volley. Mortar and heavy-machine-gun items remain prerequisites for the
  crew-served portions of Step 8.

**Architecture:** AI produces *intent* and nothing else. The same `Vector3` direction and
`PlayerStateFlags` a client input packet carries goes into `PlayerMovement.Step`, and the same
`PlayerFireData` shape goes into `WeaponSystem.ApplyFire`. Every layer below stays untouched, so
there is never a second movement or shooting path to keep in sync. Pure logic (search, ballistics,
scoring, memory) lives in `Common` with no Stride dependency and is therefore testable headless;
orchestration, threading and world access live in `Server`.

**This is a system, not a feature.** It is built one step at a time, each playable and each ending
with what is *deliberately not* in it. Expect the shape to change between steps — the design past
step 3 is a sketch that runs ahead of the code on purpose, and should be re-read rather than
trusted when its turn comes.

---

## Global constraints

- **Never `git commit`.** Leave every change unstaged for review.
- **60 FPS client, 30 TPS server, in singleplayer on a Beelink SER5, with 32–64 agents alive.** A
  requirement, not an aspiration. The binding case is singleplayer, where `ServerHost` steps from
  the client's `Update()` and the server tick shares the 16.6 ms frame budget instead of owning
  33 ms. A step that cannot hold this on that machine is not done. See Performance budget below.
- Nothing here adds a wire message. Mobs already replicate as `ServerPlayer`s through
  `ServerToClientId.PlayerSpawn` / `PlayerPosition` and `ObjectReplication`. If a step appears to
  need a new `ServerToClientId`, stop — it probably means logic drifted to the client.
- `Common` must not reference Stride. Anything in `Common/Navigation`, `Common/Ballistics`,
  `Common/Ai` is `System.Numerics` only.
- `DemiurgeSharp.csproj` globs `**/*.cs` from the repo root. New directories under `Common/`,
  `Server/` are already covered by existing `<Compile Remove>` lines; **no new directory at the
  repo root** without adding one.
- All cadence in server ticks, derived from `NetworkConfig.TickRate` (30). Never hardcode 30.
- Test only complex pure logic — search correctness, coordinate/angle math, probability. Do not
  write tests for behaviour arbitration, state transitions, or anything verifiable by watching a
  playtest.
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
| `Server/Ai/NavigationSystem.cs` | Path request queue, worker thread, edit-version invalidation |
| `Server/Ai/PathFollower.cs` | Path → intent, waypoint advance, replan triggers |
| `Server/Ai/Perception.cs` | FOV + budgeted LOS raycasts, writes `ContactMemory` |
| `Server/Ai/SquadBlackboard.cs` | Shared contacts, position claims, engage/advance tokens |
| `Server/Ai/CombatBehavior.cs` | Engage / suppress / hold decision and aim |
| `Server/Ai/GrenadeBehavior.cs` | Safe low-arc throws against recently occluded contacts |
| `Common/Ballistics/ThrowSolver.cs` | Launch angle for a lobbed projectile; terrain clearance along the arc |
| `Common/Ai/AreaTargeting.cs` | Best splash centre given believed contacts, with a friendly exclusion |
| `Server/Ai/CrewWeapon.cs` | Lug → deploy → fire → pack state machine for emplaced weapons |
| `Server/Ai/MobBrain.cs` | Per-unit arbitration; owns the above for one mob |
| `Server/Ai/CommanderAi.cs` | Theater objectives, frontline, trench designation (step 10) |
| `Server/MobSystem.cs` | *Modified* — delegates to `MobBrain`, keeps spawn/roam helpers |

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
- `NavigationSystem` records the version when a search starts; if it differs on completion, the
  path is discarded and re-requested
- one worker thread, one request at a time, FIFO queue with per-mob replacement (a mob's newer
  request supersedes its older one rather than queueing behind it)

Digs are rate-limited to two per second per player, so version churn is low.

### Path following

`PathFollower` converts the current waypoint into the intent `MobSystem` already produces —
`LastIntent`, `State`, `Yaw` — and `PlayerMovement.Step` remains the only thing that moves anybody.
Advance to the next waypoint within an arrival radius; request a replan when the path is exhausted,
the edit version moved, or progress stalls for a second.

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
spray-inaccurate curve for free; and a crouched sniper at 200 m — the AWP's actual `MaxRange` — is
0.998 on an exposed target but 0.51 on a peeker, making long-range duels about exposure discipline
rather than about the rifle.

Tests: MOA→radian conversion against the table above; `SigmaRadians` reproduces r95 at the 95%
point; `Probability` is monotonically decreasing in range, approaches 1 as range → 0, and equals
`1 - exp(-0.5)` ≈ 0.393 when σ exactly equals the target radius; recoil decays back to `BaseMoa`
and never below it.

**Deliberately not in this step:** projectile simulation, lead and drop solving, changing
`WeaponSystem` from hitscan. This step only produces the numbers. When projectiles land, the lead
and drop solver joins this directory and is shared by the client reticle, the server, and the AI —
computed once, like every other position in this codebase.

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
| **Hold** | No LOS, out of `WeaponStats.MaxRange`, reloading, or ammo low and no immediate threat | Reposition or reload |

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

The commander sets theater objectives and the squad tier decides how. Trench designation lives
here: individual agents digging cover converge on disconnected foxholes, because **connectivity is
the one property that does not emerge from local decisions**. A commander stamping a template
oriented against the threat axis is the simplest thing that supplies it.

Worth testing the cheap alternative first: dug space is free to traverse afterwards, so a squad
repeatedly pathing the same axis with digging allowed and exposure priced into the cost may extend
and reuse its own cut — trenches as worn paths rather than as construction. One squad and one
machine gun will show whether that converges or produces mush.

Then the AI Battle map from `TODO.md`: team spawn points, flag zones, wave respawn, and player
commander abilities.

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

- **The frame is GPU-bound, the CPU has cores to spare.** So the preferred fix for an expensive AI
  system is to move it to a worker, not to micro-optimize it on the main thread. One pathfinding
  worker is comfortable and a second is affordable; a thread per agent is not.
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
