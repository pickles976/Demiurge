# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

# PVP Mechanics

Bugs: 

After relocation, pathfinding stops and NPCs stand idle.

NPCs are unable to dig out of their foxholes they often just jump up and down instead of digging sideways. They should start by digging a 1x1 2-deep hole, but then expand it at varying depths so they can see out while standing at certain parts.

AI still struggles somewhat with building up, sometimes they just jump at the wall, although it is better than before.

It looks like only one squad member is digging at a time? Could multiple squad members contribute to digging rather than just 1?. Also if a single squad is stuck for a long period of time, perhaps each squad member could do their own pathfinding to dig out, so they aren't all bunched up in the same spot.

NPCs can navigate over short bridges sometimes, but often struggle with long ones. It appears to be non-deterministic.

Client performance issues. 

- [ ] PVP
    - [x] add ppsh
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
- [ ] pain sound
- [ ] feedback sound when enemy dies


- [ ] add trees
- [ ] tree destruction
      - [ ] low LOD tree
      - [ ] trees have health and take damage and change models to a broken version
      - [ ] trees delete if the terrain beneath them goes away

- [ ] clean up UI and stuff

# AI

- [x] Integer teams, team spawns, enemy-only perception, and friendly fire
- [x] Standard NPC inventory: AK-47, four grenades, and shovel
- [ ] AI should have different behaviors based on weapon
  - [ ] SMG -- close the gap by moving from cover to cover, once close start suppressing and using grenades. Ideal engagement range is <30m
  - [ ] automatic rifle -- fire and advance. Seek cover before firing, coordinate with nearby units for fire and advance. Ideal engagement range is 100-200m
  - [ ] sniper rifle, 200m+. Seek cover and take shots when you can. Fall back as units get closer to better vantage points. Look for cover and high spots with far LOS.
- [x] Bounded cover search with LOS, crouched/standing exposure, corner peeks, escape routes,
  concealment, and squad position claims
- [x] LOS/FOV perception, decaying contact memory, delayed squad sharing, gunshot investigation,
  idle scanning, and incoming-fire response
- [x] Fire-and-advance permits, conservative long-range fire, projectile-drop aim, suppression,
  cover grenades, closing ineffective ranges, and a 70 m engagement gate so distant shared contacts
  stop freezing NPCs in place
- [x] Dirt/grass objective digging, rising pit-escape targets, and emergency cover digging
- [x] Dig escalation on an exhausted air-only search, dig-site commitment across bites, and a foxhole
  depth cap an NPC can always jump out of
- [x] Dynamic proximity squads: membership re-forms from live positions instead of being fixed at spawn
- [x] Fire and movement: base-of-fire vs bound roles, sticky flank sides, envelope positions that close
  each bound, emergent leapfrog, and suppressing fire at a contact's last known position
- [x] Capsule hit volume and multi-body-point AI aim, so a head peeking over cover is both visible and
  hittable
- [x] `ai track` NPC debug overlay (beacons, facing, clustering)
- [ ] Connected foxhole/trench construction
- [ ] when the enemy is entrenched, the AI should dig towards the enemy's trenches. Needs a
  `PathFollowState.Digging` case in the bound follower first; a digging man gives up his aim
- [ ] Raise the global cover-query budget once measured; at 1/tick a squad is slow to go set
- [x] Commander flag ranking, squad allocation, emergency reinforcement, and assignment stability
- [ ] Commander fortification and crew-weapon objectives
- [x] Squad-level objective execution, shared contacts, cover claims, fire/advance permits, and
  grenade reservations
- [ ] Weapon-role assignment, mortar crews, and heavy-MG logistics
- [x] Unit-level capture/defend, combat, cover, grenade, jump, dig, hearing, and stuck recovery

# AI Battle

- [x] Create and load the `conquest` map
  - [ ] add trees back in
  - [ ] reusable structure editor
  - [x] Add team-specific spawn points
  - [x] Add flag zones
  - [ ] add heavy MG and mortar items
- [x] Set up two teams of 16 NPCs plus a team-1 player in singleplayer
  - [x] Capturable/neutralizable flags, controlled spawn areas, and 20-second wave respawns
  - [x] Kill/flag/stuck-NPC activity feed
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
