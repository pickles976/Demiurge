# Next Big Thing: navigation search budgets do not hold

Measured 2026-08-02, after the raycast fix took perception and cover off the critical path and the
server reached 30 TPS. Navigation is now the binding constraint, and it is failing in a specific way
worth stating before anyone optimises around it.

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

**Already done from this diagnosis:** `NavSearchOptions.Deterministic()` budgets by expansion count
instead of wall clock. Tests using it are reproducible —
`WideTrenchWithABridgeIsCrossedByRepeatedRequests` went from failing two runs in three to passing
eight of eight. Production still uses the clock; switching it changes NPC behaviour and belongs to the
design decision above, not to a test fix.

**Open, and characterised:** `NpcExcavatesAcrossAnUnwalkableSoilSlopeInsteadOfJumpingAtIt` fails
identically on every run — not flaky, and not fixed by the deterministic budget. The NPC reaches
`X = 7.69` against a goal at `X = 7.5`, so it arrives horizontally, but ends at `Y = 16.25` where the
plateau needs `17.14` — **0.89 m short vertically after 167 terrain edits**. It is digging forward into
the hill rather than cutting a staircase up it. BARITONE.md's execution record claims exactly this
scenario ("a steep soil frontier is excavated without endless recovery jumping", and the follower's
uphill recovery jump restored so NPCs could mount the one-metre treads staircase excavation produces),
so this is a regression against behaviour that was once verified. Fixing it means the staircase
generator or the follower's rise handling, which is the same subsystem as the item above.

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

The weapon-behavior items above (PPSH sprinting, SKS entrenching, more mobile tactics) are symptoms of
one missing system, not three features. Per-unit arbitration branches on weapon identity, so each
behavior has to be written and no two candidate actions can be ranked against each other. Prefer
pricing every available action in one currency and letting weapon behavior fall out of parameters —
the way route choice fell out of movement seconds. The currency itself is the unsolved part; see the
design-method section in `../CLAUDE.md` and the AI-layers note in `ARCHITECTURE.md` before adding
another per-weapon branch.

- [ ] Connected foxhole/trench construction
- [ ] when the enemy is entrenched, the AI should dig towards the enemy's trenches. Needs a
  `PathFollowState.Digging` case in the bound follower first; a digging man gives up his aim
- [ ] Raise the global cover-query budget once measured; at 1/tick a squad is slow to go set
- [ ] Commander fortification and crew-weapon objectives
  grenade reservations
- [ ] Weapon-role assignment, mortar crews, and heavy-MG logistics

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
