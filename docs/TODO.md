# Next Big Thing: navigation search budgets do not hold

Measured 2026-08-02, after the raycast fix took perception and cover off the critical path and the
server reached 30 TPS. Navigation is now the binding constraint, and it is failing in a specific way
worth stating before anyone optimises around it.

> **The four paragraphs below are the original diagnosis and are kept in their measured tense. Half
> of it has since been acted on — read the status under "Two ways out" before believing any present
> tense in them.**

**`NavSearch` declares a 25 ms useful-prefix budget and a 100 ms failure budget. Live searches run
153 ms at p50 and 342 ms at p95** — three to six times over, consistently, not occasionally. The
budget is wall-clock and checked every 64 expansions, which cannot hold when eight workers contend
for six cores: a descheduled thread blows straight through it and only notices afterwards.

Three consequences, all of which look like separate problems and are not:

- **Routes come back partial** when they should complete, so NPCs replan every few metres and the
  worker queue stays deep. It improved a lot with the cheaper raycast — full routes went from 0 to 18
  in a good window — but it is still mostly partial traffic.
- **Route sharing is effectively off.** `SquadBlackboard` only shares complete dig-free routes by
  design (see BARITONE.md's execution record, where sharing unproved prefixes made every squad member
  reconnect to the same local minimum). With few complete routes there is almost nothing to share, so
  a correctness fix silently removed the reuse that kept cost down.
- **The navigation tests are flaky** — the same searches, the same budgets, the same contention. While
  that holds, the suite cannot tell a regression from noise.

Two ways out, and they are genuinely different designs rather than a fix and a workaround. Either the
budget mechanism becomes robust to descheduling — count expansions rather than milliseconds, so it is
deterministic and reproducible — or long-range routing gains structure (hierarchy, corridor caching)
so that a search to a distant flag does not need a budget to terminate in the first place. The first
is small and makes the tests deterministic; the second is what actually makes NPCs route across the
map. They are compatible, and the first is a prerequisite for measuring the second honestly.

This is a system, not a patch. It wants the same treatment the time-costed A* got.

**The first way out is DONE, and production takes it.** `NavSearchOptions.Default` now sets
`PrimaryExpansionBudget = 128` and `FailureExpansionBudget = 320`; the `TimeSpan`s are retained,
ignored, and documented as a record of what the budget was originally meant to buy. The counts are
calibrated rather than picked — conquest measures ~0.8 ms of CPU per expansion, at which price the
old 25 ms and 100 ms budgets bought about 32 and 127 expansions, so 64/256 reproduce the same
ceilings without descheduling overshoot. The limits were later raised when 64/256 returned no useful
prefix for one of the 32 initial conquest routes and repeatedly replanned inside Team 1's spawn.
Actors that actually repeat bounded prefixes now escalate individually through 4,096, 8,192 and
16,384 expansions; making 2,048 the default pushed the 32-route conquest benchmark to roughly two
seconds p95. The ordinary limits remain multiples of the 64-expansion check interval on purpose. This
does not make navigation cheaper; it makes the cost predictable and stops route quality depending on
how busy the machine was.

Also since this was written: the heuristic had been quietly deflated 1.5x (it divided distance by
sprint speed while every edge is priced at walk speed), so the search has only just started behaving
like real A*. `NavSearchOptions.HeuristicWeight` exists for weighted A* and is deliberately left at 1
until that lands, rather than bundling two changes into one measurement.

**Still open: the second way out.** Long-range routing has no structure — no hierarchy, no corridor
caching — so a search to a distant flag still terminates because a budget stopped it rather than
because it finished. That is the thing that actually makes NPCs route across the map, and it is
untouched.

**Regression, characterised, as of 2026-08-09:** `NpcExcavatesOutOfADeepWidePit` fails with
`successTick == 0` — the NPC never leaves a 6 m pit in 240 simulated seconds. BARITONE.md's execution
record claims this scenario "reaches the rim in 120 terrain edits instead of 300", so this is a loss
against behaviour that was verified.

The earlier entry here blamed `NpcExcavatesAcrossAnUnwalkableSoilSlopeInsteadOfJumpingAtIt` and
described it in detail (0.89 m short vertically after 167 edits, digging forward instead of cutting a
staircase). **That test passes now.** The failure moved from the slope to the pit, so do not use that
characterisation to guide the fix — measure the pit.

**Not a navigation failure at all:** `EachConquestTeamCapturesBothCentralFlagsWithoutStuckRelocation`
fails for both teams in about 150 ms, before any simulation runs, on `Assert.Equal(4, flags.Length)`
— the conquest map has five flag placements now. The real-map acceptance scenario BARITONE.md records
has therefore not actually run since the map gained its fifth flag. Fixing the assertion is the
cheapest way to find out whether that result still holds.

# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

# PVP Mechanics

Server/Client split
- replace Riptide for singleplayer mode

AI
- better tactics
  -  be ore mobile
- PPSH units dont do shit rn
  - PPSH units sprint
- SKS units dig in and engage

  These three were symptoms of weapon-identity branching, and that branching is gone (see the AI
  section below). They are now TUNING questions against a scored model rather than missing features:
  if the PPSH man still does not close, the thing to look at is `WeaponEffectiveness`'s range curve
  and the `aggression` scalar, not a rule about SMGs.

- [ ] PVP
    - [ ] add mosin-nagant
    - [ ] add black cats

    - [ ] heavy MG
      - [ ] takes time to assemble and disassemble, player has to lug crate around and is vulnerable
      - [ ] put one at the hilltop flag
      - [ ] allow NPCs to use it

    - [ ] flag 3D model
    - [ ] crate
    - [ ] mortar
    - [ ] add helmet

- [ ] brick wall texture and block type

- [ ] add trees
- [ ] tree destruction
      - [ ] low LOD tree
      - [ ] trees have health and take damage and change models to a broken version
      - [ ] trees delete if the terrain beneath them goes away

- [ ] clean up UI and stuff

# AI

**The currency is no longer the unsolved part.** It is net health points per second —
`Common/Ai/CombatValue.cs`. Weapon identity is gone from the AI, fire discipline is a rate choice,
squad role allocation is a joint score, and perception is budgeted against a sound upper bound. What
is built and what is not is written up under "The combat currency" in `ARCHITECTURE.md`; read that
before adding anything here.

- [x] Find the combat currency (`CombatValue`, net HP/s) and price weapon reach in it
      (`WeaponEffectiveness`, rate-as-a-choice, `SightingMoa` replacing the flat AI aim constant)
- [x] Delete `MaxEngagementRangeFor`, `PrefersToHoldFire`, `ShouldAdvance` and the hand-set bursts
- [x] Joint squad allocation with the suppression externality (`SquadTactics` scores hold vs assault)
- [x] Raise the global cover-query budget — now 8/tick, was 1
- [x] `PathFollowState.Digging` exists in the follower
- [ ] **Convert `MobSystem`'s per-unit arbitration to the same score.** The last stage-1 item, and
  now wiring rather than design: the ordered `ActorIntent` ternary still selects behaviour by
  priority, and `ActorIntent.HoldAndFire` is declared and never constructed. See ARCHITECTURE.md's
  "What is still an ordered chain"
- [ ] Connected foxhole/trench construction (only single-position `FoxholePlan.NextBite` exists)
- [ ] when the enemy is entrenched, the AI should dig towards the enemy's trenches. The
  `PathFollowState.Digging` prerequisite is met; a digging man gives up his aim
- [ ] Excavation commit model: dig leases, lazy local validation, 0.5 brush radius for cuts
- [ ] Commander fortification and crew-weapon objectives
  grenade reservations
- [ ] Weapon-role assignment, mortar crews, and heavy-MG logistics. `Server/Ai` contains no
  reference to mortars or MGs at all, so NPCs cannot work either one
- [ ] Grenade doctrine as considerations feeding one score rather than alternative rules
  (`GrenadeBehavior` exists and contains no scoring)
- [ ] Strategy layer: force ratio, stalemate concentration, combat zones

# AI Battle

- [x] Create and load the `conquest` map
  - [ ] add trees back in
  - [ ] reusable structure editor
  - [ ] add heavy MG and mortar items

# PVP Demo

- [ ] extend AI battle demo with human players
- [ ] Host a server and run external multiplayer playtests
  - [ ] Provision a DigitalOcean host
  - [ ] Configure the scrungy.com domain
- [ ] Track and fix issues found by the Demo

# Map Editor And Content

- [x] Load a specific baked map when the server starts
- [x] Save and load source maps
- [x] Edit terrain, blocks, objects, and spawn points in 3D
- [x] Capture, save, load, transform, and place structures
- [ ] Add a dedicated editor object browser and properties UI
- [ ] Add cancellable background baking with progress
- [ ] Profile long editing sessions and add compaction only if justified

# Open World

- [ ] Track active chunks per player on the server
- [ ] Stream chunks as players move
- [ ] Replicate objects according to active player chunks
- [ ] Limit client meshing to view distance
- [ ] Decide the intended maximum view distance
- [ ] Research and implement cave carving
- [ ] Add resource deposits

# Debugging And Known Issues

- [ ] **Identify the persistent one-tick reconciliation corrections.** Needs a game run; cannot be
  settled from tests. Combat logs show a steady stream of `[Reconcile] correction of 0.1997` at
  roughly two per second, and the value is not arbitrary: `PlayerMovement.SprintSpeed` is 6 m/s and
  6/30 is 0.2, so each correction is exactly ONE server tick of sprinting. Earlier logs showed a
  spread of 0.13 to 1.24 — multiples of one tick at walk and sprint speed — which was the server
  missing ticks while it ran at 15–23 TPS. Now that it holds 30 TPS the spread has collapsed to a
  uniform single tick, so the cause has changed and the remaining one is unidentified.

  The leading suspect is ours: the in-process transport drops 2% of unreliable traffic on purpose
  (`TransportHostility.UnreliableDropRate`), `PlayerInput` rides that channel, and a dropped input
  makes `GameWorld.Tick` re-step with the last intent — which is precisely a one-tick divergence. At
  roughly 60 inputs per second a 2% drop is about 1.2 per second, against the ~2 per second observed.
  That is close enough to be the explanation and not close enough to assume it.

  **The experiment:** set `UnreliableDropRate` to 0, play a combat session, and compare the correction
  rate. If it goes to zero the fuzz is working as designed and the finding is that dropped input
  produces a visible correction — a real multiplayer behaviour worth smoothing rather than a bug we
  introduced. If corrections persist, something else diverges by exactly one tick and the drop rate
  was a red herring.

- [ ] Draw chunk borders in debug mode
- [ ] Revisit the particle system after Stride issue 2496 is resolved:
  https://github.com/stride3d/stride/issues/2496

# Research References

- Grass system: https://nicogo1705.github.io/AssetStore/asset?id=com.nicogo.grass
- Marching-cubes compute shader:
  https://nicogo1705.github.io/AssetStore/asset?id=com.nicogo.marching-cube-compute-shader
- SDSL overview: https://hackmd.io/@vN9HDo5XQAGVCM_epmoJBA/S1LxeorWT
- Dual contouring:
  https://www.boristhebrave.com/2018/04/15/dual-contouring-tutorial/
- Surface nets and texturing:
  https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14
- Tree and grass shader reference:
  https://www.youtube.com/watch?v=GOfttJQ-FGw&t=19s
- Additional video references:
  https://www.youtube.com/watch?v=Y0Ko0kvwfgA
  https://m.youtube.com/watch?v=PLMcCKeJ6f0&list=WL&index=56&pp=iAQBsAgC
