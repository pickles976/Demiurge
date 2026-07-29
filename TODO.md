# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

# AI 

- [ ] AI "teams"
- [ ] AI should have different behaviors based on weapon
  - [ ] SMG -- close the gap by moving from cover to cover, once close start suppressing and using grenades. Ideal engagement range is <30m
  - [ ] automatic rifle -- fire and advance. Seek cover before firing, coordinate with nearby units for fire and advance. Ideal engagement range is 100-200m
  - [ ] sniper rifle, 200m+. Seek cover and take shots when you can. Fall back as units get closer to better vantage points. Look for cover and high spots with far LOS.
- [ ] how do AI find "cover"?
  - [ ] seek out parts of a chunk with steep gradients, perform LOS-checks to known enemies in the AI's blackboard to evaluate the quality of cover
  - [ ] concealment-- use cover when engaging in combat, use concealment to move stealthily or perform ambushes.
- [ ] AI not omniscient, need full LOS to see enemies. Can remember where it last saw enemies with memory fade.
- [ ] when no good cover is available, AI can dig their own foxholes. If AI get stuck it should staircase out of a hole or a tunnel. 
- [ ] when the enemy is entrenched, the AI should dig towards the enemy's trenches.
- [ ] units can share knowledge of enemy positions with one another
- [ ] squad tactics, riflemen use "fire and advance", snipers provide suppression and overwatch, assault troops close the distance and go in for the kill

- [ ] commander-level AI, sets theater-scale objectives (take objective, build fortifications, move MG or mortar to location)
- [ ] squad-level AI - how to achieve theater goals locally, delegate mortar lugging job to least useful unit in the group, when to attack, when to defend, who should hold a defensive post and who should work on digging, etc.
- [ ] unit-level AI -- goals like "stand on cap point" or "bring MG to this area". Intermediate behaviors like engaging, flanking, digging into cover, seeking cover, etc.

# PVP Mechanics
- [ ] brick wall texture and block type

- [ ] animation clean-up
    - [ ] keep gun pointed forward when not sprinting, don't wobble
        - [ ] shoot without aiming
        - [ ] aim down sights
    - [ ] fix body armor positioning

- [ ] PVP
    - [ ] shovel
      - [ ] add helmet
    - [ ] add mosin-nagant
    - [ ] add SKS
    - [ ] add ppsh
    - [ ] flag 3D model
    - [ ] add black cats

    - [ ] grenade pickup
        - [ ] throwing arc
        - [ ] server-side splash damage 
        - [ ] deform the terrain
        - [ ] camera shake
        - [ ] particles
    - [ ] crate
    - [ ] mortar
    - [ ] heavy MG
      - [ ] takes time to assemble and disassemble, player has to lug crate around and is vulnerable

- [ ] add trees
- [ ] tree destruction
      - [ ] low LOD tree
      - [ ] trees have health and take damage and change models to a broken version
      - [ ] trees delete if the terrain beneath them goes away

- [ ] health regeneration

- [ ] clean up UI and stuff

# AI Battle

- [ ] create map
  - [ ] add trees back in
  - [ ] reusable structure editor
  - [ ] Add team-specific spawn points
  - [ ] add flag zones
  - [ ] add heavy MG and mortar items
- [ ] set up AI team battle
  - [ ] frontlines. Capture flags to secure spawn points. Goal is to capture all flags on the map. 20s respawn timer for "wave" spawning.
  - [ ] Give player "commander" abilities to direct friendly units

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
