# AI Roadmap

This is the authoritative list of unfinished NPC AI and navigation work as of 2026-08-12.
General project work remains in [`TODO.md`](TODO.md).

The current foundation is substantial: NPCs use the authoritative player movement, weapon,
grenade, mortar, and terrain-edit systems; dynamically form six-person squads; share delayed
contacts; allocate base-of-fire and bounding roles with `CombatValue`; follow wedge lanes; select
conquest objectives by marginal ticket value; evade and throw grenades; and can acquire and operate
mortars. `CombatOutcome`/`MobAction` also give the per-tick actor output one writer.

## 1. Restore the AI acceptance baseline

- [ ] Fix `MobNavigationIntegrationTests.NpcExcavatesOutOfADeepWidePit`. On 2026-08-12 the NPC made
  52 terrain edits but did not escape the six-metre pit within 240 simulated seconds.
- [ ] Update `EachConquestTeamCapturesBothCentralFlagsWithoutStuckRelocation` for the five-flag
  conquest map, then rerun both teams. The stale `flags.Length == 4` assertion currently prevents
  the scenario from simulating.
- [ ] Bring `ConquestNavigationBenchmarkTests.ThirtyTwoNpcInitialObjectiveRoutes` reliably below
  its 500 ms queue-p95 ceiling. A 2026-08-12 Debug run measured 523.9 ms p95, 652 ms wall time,
  and zero shared-route reuses. Repeat measurements before attributing a small overrun because this
  benchmark is sensitive to CPU contention.
- [ ] Add the missing end-to-end AI behavior statistics: stationary fraction, terrain edits per NPC
  per minute, bounds attempted/completed, engagement range, shared-route reuse, and path requests per
  squad per minute.
- [ ] Perform and record a visual conquest review after the items above pass. Check that bounds
  complete, short-range weapons close, defenders do not dig inside existing cover, and no squad
  converges on one excavation local minimum.

## 2. Finish the scored decision model

- [ ] Replace `MobSystem`'s ordered `ActorIntent` ternary with a comparison of combat actions in net
  HP/second. At minimum, compare holding/fire, repositioning, and entrenching; keep immediate safety
  actions such as blast evasion as explicit preconditions rather than pretending they are ordinary
  tactical choices.
- [ ] Construct and consume `ActorIntent.HoldAndFire`. It is currently declared but never created.
- [ ] Remove the remaining fixed entrenchment threshold after entrenching and repositioning are
  priced directly against holding.
- [ ] Wire `ThreatRanking` into perception-ray allocation and combat target selection. The class and
  tests exist, but runtime perception is still round-robin and combat selects the nearest contact.
- [ ] Replace `MobBrain`'s single `HeardActorId`/position/tick slot with the existing `HeardShots`
  memory. The tested salience container exists but has no runtime caller, so a later distant shot can
  still replace a nearby one.
- [ ] Record the observed enemy weapon and aim/engagement evidence in contact memory so scoring uses
  information the NPC actually perceived rather than always assuming the unidentified threat
  profile.

## 3. Make squad routing scale

- [ ] Deliver the documented "one long trunk per out-of-combat squad" outcome. Wedge steering and
  the `(team, squad, objective)` key are live, but newly produced bounded prefixes are actor-local;
  ordinary conquest starts can therefore perform zero shared-route reuses.
- [ ] Add structure to long routes—an objective flow field, coarse portal/chunk hierarchy, corridor
  cache, or a measured combination—so routes finish because the destination is reachable, not
  because a per-actor expansion budget stopped the search.
- [ ] Either integrate `NavFlowFieldService` into the live navigation lifecycle with terrain
  staleness handling or remove the unused prototype after another approach is selected.
- [ ] Preserve local connectors, formation exits, jumps, digging, and blocked-edge recovery when
  sharing a trunk. A shared prefix must not make every squad member reconnect to the same local
  minimum.
- [ ] Replace coarse whole-corridor invalidation during active excavation with validation of the
  next executable segment. Edits outside the actor's immediate route should not restart it.
- [ ] Make `ai stats` label the residual movement bucket honestly. Keep collision solve and actual
  path-follower time separate rather than presenting the whole residual as `follow`.

## 4. Complete excavation and fortification

- [ ] Plan a complete local cut as the minimum voxel set needed for traversability, then execute the
  committed cut until completion or a defined external invalidation. Do not choose a new excavation
  direction after every shovel bite.
- [ ] Add excavation-volume leases to `SquadBlackboard` so squadmates reuse one staircase or trench
  instead of cutting overlapping routes or removing each other's parapets.
- [ ] Use a dedicated 0.5 m excavation brush for planned one-metre corridors while retaining the
  player's existing hand-dig brush where appropriate.
- [ ] Verify that planned one-wide corridors admit the authoritative capsule and that removed voxel
  count approaches one shared staircase rather than one staircase per squad member.
- [ ] Fix the case where an NPC digs downward to cross a large trench when jumping in or routing to
  an existing exit is cheaper.
- [ ] Add connected fortification plans. Around flags, derive terrain edges from the voxel
  heightmap; where the terrain supplies no useful design, use connected concentric one-wide,
  two-deep trenches at approximately 10 m and 17 m as the fallback.
- [ ] Give fortification plans one strategic owner. NPCs assigned to a plan may remove only planned
  voxels; ordinary combat entrenchment remains a small local fighting position.
- [ ] Support offensive excavation toward an entrenched enemy when its scored route beats exposed
  movement.

## 5. Generalize strategy and crew weapons

- [ ] Replace `CommanderAi`'s direct `FlagSystem` dependency with a game-mode objective provider.
  Support static objectives first and leave room for carried and delivery objectives used by CTF;
  KOTH should use the same interface.
- [ ] Add explicit force-at-objective and travel-time strength to strategic allocation. Preserve
  the existing marginal ticket value and diminishing returns rather than reintroducing a priority
  ladder.
- [ ] Publish short-lived combat zones from shots, explosions, and deaths. Use them to distinguish
  ordinary formation travel from combat movement.
- [ ] Add commander-owned fortification and crew-weapon objectives without redirecting an entire
  squad away from its primary objective.
- [ ] Let NPCs relocate mortars: evaluate the value of the current fire sector against the cost and
  vulnerability of packing, carrying, emplacing, and reorienting the tube. Existing AI can acquire
  and operate a useful mortar but does not deliberately reposition one.
- [ ] Add heavy-MG equipment, emplacement/packing, role assignment, ammunition/logistics, and AI
  operation. No heavy-MG behavior exists yet.
## 6. Combat enrichment

- [ ] Fold grenade doctrine into the same action score instead of relying only on the current
  recently-lost-contact rule. Consider cover, clustering, friendly risk, suppression, and an
  imminent assault as additive evidence.
- [ ] Add elevation to position and engagement scoring using its effect on exposure, hit
  probability, and route cost rather than a flat "high ground" bonus.
- [ ] Use the ranked threat model for target selection so danger, weapon reach, exposure, and whom
  the enemy appears to be engaging can outweigh raw distance.
- [ ] Extend `ai track` to show the chosen action, alternatives and scores, range matchup, tactical
  role, committed bearing, objective value, and commander/resource assignment.

## Completion gate

The overhaul is complete when:

- all focused Common and Server AI tests pass;
- the deep-pit and updated real-map conquest integrations pass without stuck relocation;
- the 32-route benchmark is reliably below its 500 ms p95 gate on the reference machine;
- the server maintains 30 TPS at the full 32-NPC conquest load;
- focused-window client measurements remain above 60 FPS—window de-prioritization must be excluded
  from performance conclusions;
- telemetry and visual review show completed bounds, useful weapon-range behavior, bounded digging,
  and no repeated squad/path/objective churn.

## Current references

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) describes the live subsystem boundaries.
- [`docs/NAVIGATION.md`](docs/NAVIGATION.md) describes the live path lifecycle.
