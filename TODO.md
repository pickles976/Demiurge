# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

Use Codex for actual engineering work, Claude for just adding tiny features.

1. Finish up AI with Codex
2. Finish PVP MVP
3. Make the environment richer
4. Refactor commands
5. Multiplayer test

# NPC AI

Claude is too stuoid for AI behavioral stuff. Just use it for simple stuff.
The consolidated status and remaining work live in [AI_TODO.md](AI_TODO.md).

# Gameplay Polish

We want to allow users to place sandbags, but to keep players from
spamming sandbags, they should be resource-constrained.

- [ ] Digging sends dirt to your inventory, 2 dirt - 1 sandbag. Can hold 4 sandbags before you need to dig more. Show sandbag "ammo" when digging with the shovel.


- [ ] add trees

  - [x] tree 3D model
  - [x] move half a meter down
  - [x] spawn 10 random billboard quads 1m around each anchor point

  - [x] apply the alpha texture in /assets/textures
  - [ ] add geometry for volume

  - [ ] Implement the shader 
  https://godotshaders.com/shader/simple-cheap-stylized-tree-shader/
  https://simonschreibt.de/gat/airborn-trees/
  
  - [ ] tune the appearance

  - [ ] stress test
  - [ ] Low-LOD tree

  - [ ] trees have health and take damage and change models to a broken version
  - [ ] trees delete if the terrain beneath them goes away
  - [ ] tree brush in map editor
  - [ ] add trees to map

# Map Editor

- [ ] improve ergonomics of terminal
  - [ ] autocomplete
  - [ ] better grouping
  - [ ] man page type documentation
- [ ] add map editor and structure editor UI block select, object select
- [ ] Draw chunk borders in debug mode

# PVP Demo
- [ ] add screen where you enter a server IP and port
- [ ] "test" to launch into conquest right away
- [ ] test over VPN

- [ ] Host a server and run external multiplayer playtests
  - [ ] Provision a DigitalOcean host
  - [ ] Configure the scrungy.com domain
- [ ] Track and fix issues found by the Demo

## Refactoring and Cleanup
- Finish AI
- Finish PVP features

- Codex refactor certain parts of the code
- Codex event queue implementation

## Stride Upgrade

Try to upgrade Stride version and get particle system working
https://github.com/stride3d/stride-community-toolkit/tree/stride-4.4/examples/code-only/Example12_Particles


# Debugging And Known Issues
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
