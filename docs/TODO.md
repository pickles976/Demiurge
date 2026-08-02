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
