# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

1. Finish up AI
2. Finish PVP MVP
3. Switch to Stride 4.5
4. Make the environment richer
5. Multiplayer test

# PVP Mechanics

- [ ] replace 2 of the MG crates with mortars

- [ ] json config for all items

- [ ] add prone
- [ ] tune MG for firing while prone


AI IMPROVEMENTS

Take a look at ./docs/TODO.md and ./docs/BARITONE.md

Overall the changes we made have caused the pace of the game to increase. NPC battles are much more exciting.

It sounds like we have a bunch of tasks running in threads that overwrite the NPCs state and make it do something. What if we just have threaded functions update the blackboard, but a     
  single function decides what to do based on the contents of the blackboard? Even the squad-level and strategic AI can just propagate down to the individual NPCs blackboard.

- NPCs will always have infinite ammo, take that into account
- units still standing idle at flag -- if they are defending, we need visual feedback indicating so. Add a command to show NPC state above their head in white text.
- Assault units are useless at long range, and extremely deadly at close range. They need to take this into account. The PPSH and grenades can absolutely massacre defenders.
- Strategic AI usually just fights over 1-2 flags, seems like a self-reinforcing loop of "needs mass", never opportunistically sends a squad out to go capture a totally defenseless flag
- If I shoot at enemies going to capture a flag from way outside of their engagement range, they will drop everything and run all the way to attack me. Even if I pose no real threat to them due to the distance.
- NPCs still moving in a line, not in formation

Bugs:

- NPCs sometimes digging down to cross a big trench rather than just jumping in

If Digging still sucks
- Stop all digging during combat. Let's get combat working first and then we can figure out how to appropriately add digging

New Features
- Get heightmap texture around flags from voxel data. Apply a sobel filter to extract edges. If insufficient edges are found, plan a simple trench design, concentric squares where the edge of each square is a 1-wide, 2-deep trench. one at 10m, one at 17m. Connect these concentric trenches in 4 directions. Strategic AI should plan the design, and NPCs can pick it up and *ONLY* dig out voxels from the plan.

- Try to add digging back in to combat

https://github.com/id-Software/Quake-III-Arena
- [ ] Quake 3-style event queue. All events, input, network, time, needs to go to a queue. Enable replays.

- [ ] allow NPCs to use it
- [ ] grenades are too bouncy
- [ ] dead soldiers drop their weapon and you can pick it up
- [ ] limit ammo for players


- [ ] PVP

      - [ ] flag 3D model     
      - [ ] digging sends dirt to your inventory, 2 dirt - 1 sandbaga
      - [ ] dig dirt to place sandbags

    - [ ] Commander set crew-weapon objectives
      - [ ] Weapon-role assignment, mortar crews, and heavy-MG logistics

      - [ ] remove glock, AWP, and AK
      - [ ] fix audio cutting off
      - [ ] long range report sounds
      - [ ] mortar whistle sound

      - [ ] add 4 more NPCs to each team

## Richer Environment 

- [ ] add wood texture
- [ ] add wood block type

- [ ] structure editor

- [ ] DP-27 Pixel Art

- [ ] add grass
- [ ] add trees
- [ ] tree destruction
      - [ ] low LOD tree
      - [ ] trees have health and take damage and change models to a broken version
      - [ ] trees delete if the terrain beneath them goes away

Try to upgrade Stride version and get particle system working
https://github.com/stride3d/stride-community-toolkit/tree/stride-4.4/examples/code-only/Example12_Particles

# NPC AI Overhaul

- Make AI skill variable 
- Fuzzy logic for decision making
http://www.datapax.com.au/mirror/20585341-The-Quake-III-Arena-Bot.pdf

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

# MERGE INTO MAIN

# PVP Demo

- [ ] add screen where you enter a server IP and port
- [ ] extend AI battle demo with human players
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
