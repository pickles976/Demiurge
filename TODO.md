# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

# PVP Mechanics

UI Improvements


It sounds like we have a bunch of tasks running in threads that overwrite the NPCs state and make it do something. What if we just have threaded functions update the blackboard, but a     
  single function decides what to do based on the contents of the blackboard? Even the squad-level and strategic AI can just propagate down to the individual NPCs blackboard.

- Add a ticket system. Start with 200 tickets, bleed if you lose majority of flags. Add ticket UI to the top of the screen, red vs blue ticket count.

Bugs:
- models with weapons equipped sitting back at spawn doing nothing
- shovel.png still not hooked up
- units still standing idle at flag -- if they are defending, we need visual feedback indicating so. Add a command to show NPC state above their head in white text
- defenders at the castle still digging a giant hole
- defenders are digging WAYYY too much
- digging down to cross a trench rather than just jumping in


- [ ] PVP
    - [ ] heavy MG
      Add the DP-27. 550 RPM, similar ballistics as Mosin, but slightly since it is 7.62x54mm but shorter muzzle length., but 50 damage instead of 70. 47 rounds per magazine. Make the player spawn with it by default. Add the reload and gunshot sounds.

      - [ ] fix cat models, just one model, orange and gray textures for team 1 and 2.
      - [ ] fix NPCs holding stuff

      - [ ] add prone
      - [ ] tune MG for firing while prone
      - [ ] add MG pickup crate at hilltop flag
      - [ ] allow NPCs to use it

    - [ ] Commander fortification and crew-weapon objectives
  grenade reservations
  - [ ] Weapon-role assignment, mortar crews, and heavy-MG logistics

    - [ ] mortar
    - [ ] add helmet
    - [ ] add uniforms to cats
    - [ ] flag 3D model

- [ ] sandbags texture and block type

- [ ] add trees
- [ ] tree destruction
      - [ ] low LOD tree
      - [ ] trees have health and take damage and change models to a broken version
      - [ ] trees delete if the terrain beneath them goes away

# Rendering

- [ ] NPCs show no equipped weapon or shovel in third person. Not an AI-overhaul regression — no
      client code was touched — and the machinery exists on both ends: `ItemSystem.SpawnInfantryLoadout`
      creates primary/shovel/grenade as replicated `ServerObject`s, and `ObjectViewFactory` attaches an
      `ItemAttachScript` to each item object to seat it on its holder's bone. Break is somewhere
      between. First suspect is `ItemAttachScript.Update`, which resolves its holder by entity name and
      then returns silently with no log:

      ```csharp
      owner ??= Entity.Scene?.Entities.FirstOrDefault(e => e.Name == $"Player_{Object.Owner.PlayerId}");
      if (owner == null) return;
      ```

      `PlayerViewFactory` does name NPC entities `Player_{id}`, so that ought to match — which points
      instead at whether NPC-owned item objects are replicated to the client at all, or whether the NPC
      model carries the attach bones. Needs a live look; add a log on that early return first.

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

# PVP Demo

- [ ] add screen where you enter a server IP and port
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
