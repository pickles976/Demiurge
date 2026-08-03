# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

# PVP Mechanics

AI 
- Remember not to myopically solve a single problem with solutions like if statements, but to use behavior trees and robust systems. Use Baritone pathfinding as an anecdote.

- Sometimes NPCs just stand at the flagpost doing nothing
- NPCs should always be doing something
- NPCs are aimlessly digging big holes while defending the flag

- better tactics
  -  be more mobile. Standing is boring. Flanking and sprinting is interesting. When you outnumber your opponent, flank! When you outrange your opponent, stay back. When your opponent outguns you, close the distance by sprinting.
  - Digging is *defensive*. Movement is *offensive*

- [ ] Connected foxhole/trench construction
- [ ] Commander fortification and crew-weapon objectives
  grenade reservations
- [ ] Weapon-role assignment, mortar crews, and heavy-MG logistics

UI Improvements

Feedback Improvements

- [ ] PVP
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
