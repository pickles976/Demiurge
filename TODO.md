# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

Use Codex for actual engineering work, Claude for just adding tiny features.

1. Finish up AI
2. Finish PVP MVP
3. Switch to Stride 4.5
4. Make the environment richer
5. Refactor commands
6. Multiplayer test

merge into main.

Bug: NPCs on Team 1 not capturing flag right next to base

- [ ] add trees
      - [ ] tree 3D model
      - [ ] low LOD tree
      - [ ] trees have health and take damage and change models to a broken version
      - [ ] trees delete if the terrain beneath them goes away
      - [ ] tree brush in map editor
      - [ ] add trees to map


## Misc

- [ ] structure editor 
      - [ ] launch with a separate command
      - [ ] build in a flat void
      - [ ] save structure with name
      - [ ] load structure in map editor, show placement preview

Here are some more features. We want to allow users to place sandbags, but to keep players from spamming sandbags, they should be resource-constrained.
- [ ] Digging sends dirt to your inventory, 2 dirt - 1 sandbag. Can hold 4 sandbags before you need to dig more. Show sandbag "ammo" when digging with the shovel.

## Refactoring and Cleanup
- Codex clean up all comments
- Codex refactor certain parts of the code
- Codex event queue implementation

## Stride Upgrade

Try to upgrade Stride version and get particle system working
https://github.com/stride3d/stride-community-toolkit/tree/stride-4.4/examples/code-only/Example12_Particles

# NPC AI Overhaul

Design: [docs/superpowers/specs/2026-08-04-ai-overhaul-design.md](docs/superpowers/specs/2026-08-04-ai-overhaul-design.md)

Rebuilds per-unit arbitration and squad tactics on one currency — net HP/second — so role falls out
of the range matchup instead of `ItemType` branches. Replaces `MobSystem` arbitration and
`SquadTactics` outright. Staged; each stage gets its own plan.

- [ ] Stage 1 — scoring core: currency, three actions, threat pruning, squad allocation.
      Fixes standing at the flagpost, aimless digging, digging inside cover.
      Plan: [docs/superpowers/plans/2026-08-04-ai-stage-1-scoring-core.md](docs/superpowers/plans/2026-08-04-ai-stage-1-scoring-core.md)
- [ ] Stage 2 — squad movement: one shared trunk path per squad out of combat, wedge slots as
      steering offsets, shared-route key rekeyed on (team, squad, objective).
- [ ] Stage 3 — excavation: commit the whole cut, lazy local path validation, excavation leases,
      0.5 brush radius for cuts, minimum-volume planning (1-wide trenches and staircases).
- [ ] Stage 4 — strategy: game-mode-agnostic objective set (CTF/KOTH), force ratio, stalemate
      concentration, combat zones.
- [ ] Stage 5 — enrichment: grenades, elevation term, target-selection scoring, skill dial.

Bugs found during design, each explaining part of the observed behaviour:

- [ ] `SquadTactics` forbids movement unless someone is `IsSet`; a squad that cannot reach cover
      deadlocks standing up, then digs, and is still not set.
- [ ] `CoverQueriesPerTick = 1` throttles the whole server to 0–3 cover searches per *second*.
      Measured at 0–400 µs/tick inside a 3.9 ms tick — starving behaviour, not protecting the budget.
- [ ] Path sharing is structurally dead: the shared-route key needs the destination within 4 m of the
      objective, but `WedgeFormation` offsets members 14 m. `shared routes 0` for entire runs.
- [ ] `NavPath.IsValid` invalidates on whole-chunk revisions, so one man's shovel bite invalidates
      every squadmate's path — a squad fits inside one 16×16 m chunk.
- [ ] `MobBrain`'s single heard-gunshot slot is last-write-wins, so a distant shot erases a
      point-blank one.
- [ ] `ai stats` reports `follow` as a residual containing the collision solver, making path
      following look 55× more expensive than its actual 51 µs/tick.
- [ ] `docs/BARITONE.md` cites `StaircaseDigTargetsStayInsideOneMetreCorridor`, which does not exist.

## AI Final Cleanup

- [ ] NPCs sometimes digging down to cross a big trench rather than just jumping in
- [ ] Get heightmap texture around flags from voxel data. Apply a sobel filter to extract edges. If insufficient edges are found, plan a simple trench design, concentric squares where the edge of each square is a 1-wide, 2-deep trench. one at 10m, one at 17m. Connect these concentric trenches in 4 directions. Strategic AI should plan the design, and NPCs can pick it up and *ONLY* dig out voxels from the plan.

# PVP Demo

- [ ] add 4 more NPCs to each team
- [ ] add screen where you enter a server IP and port
- [ ] test over VPN

- [ ] Host a server and run external multiplayer playtests
  - [ ] Provision a DigitalOcean host
  - [ ] Configure the scrungy.com domain
- [ ] Track and fix issues found by the Demo

# Open World

- [ ] Track active chunks per player on the server
- [ ] Stream chunks as players move
- [ ] Replicate objects according to active player chunks
- [ ] Limit client meshing to view distance
- [ ] Decide the intended maximum view distance
- [ ] Research and implement cave carving
- [ ] Add resource deposits

# Debugging And Known Issues

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
