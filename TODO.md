# PVP Mechanics

- [ ] brick wall

- [ ] animation clean-up
    - [ ] keep gun pointed forward when not sprinting, don't wobble
        - [ ] shoot without aiming
        - [ ] aim down sights
    - [ ] fix body armor positioning

- [ ] PVP
    - [ ] grenade pickup
        - [ ] throwing arc
        - [ ] server-side splash damage 
        - [ ] deform the terrain
    - [ ] mortars

    - [ ] shovel
    - [ ] add mosin-nagant
    - [ ] add SKS
    - [ ] add ppsh
    - [ ] heavy MG

    - [ ] add wearables
        - [x] add armor
        - [ ] add helmet

    - [ ] add health pack pickups
    - [ ] add black cats

    - [ ] flag 3D model

- [ ] switch to projectile-based weapons

- [ ] clean up UI and stuff

# Inventory And PVP Demo

- [ ] create a simple map
- [ ] Add an inventory UI
- [ ] Create a simple free-for-all demo
  - [ ] Load a map from a PNG
  - [ ] Add random spawn selection
  - [ ] Add fixed health-kit locations
- [ ] Host a server and run external multiplayer playtests
  - [ ] Provision a DigitalOcean host
  - [ ] Configure the scrungy.com domain
- [ ] Track and fix issues found by the FFA demo

# Map Editor And Content

- [x] Load a specific baked map when the server starts
- [x] Save and load source maps
- [x] Edit terrain, blocks, objects, and spawn points in 3D
- [x] Capture, save, load, transform, and place structures
- [ ] Add team-specific spawn points
- [ ] Add a dedicated editor object browser and properties UI
- [ ] Add cancellable background baking with progress
- [ ] Profile long editing sessions and add compaction only if justified

# Structures And Game Modes

- [ ] Create reusable castle structures
- [ ] Create mortar emplacements
- [ ] Add a one-sided assault game mode
- [ ] Add a scripting system

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
