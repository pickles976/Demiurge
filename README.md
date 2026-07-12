https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

NOTES:
- particle system broken
https://github.com/stride3d/stride/issues/2496

`dotnet run` -- client
`dotnet run --project Server/DemiurgeServer.csproj` -- server


`dotnet build DemiurgeSharp.slnx`

1. ~~Move the player~~
2. ~~Camera controls~~
3. ~~Shooting~~

4. Networking (details in docs/NETWORKING.md + docs/NETWORKING_TODO.md)
- [x] separate out client and backend (netcode / sim / view layers)
- [x] networking diagram
- [x] server-authoritative movement (inputs up, positions down, shared PlayerMovement.Step)
- [x] sync rotation
- [x] sync animations: send InputButtons up, server derives outcome flags (Shooting/Reloading/Jumping) in shared Common code, broadcast State down — see NETWORKING_TODO.md §3 (inputs vs outcomes)
- [x] interpolate remote players (tick-stamped snapshot buffer, render ~100ms in the past)
- [x] delete on disconnect

- [x] client-side prediction with true authoritative server
- [x] validate inputs server-side: clamp Intent to unit length (the real speed hack), 

- [x] object registry (NetworkId + ObjectType, spawn/despawn/state messages)
- [x] gun as pickup/equip object (Weapon state flag; old shooting code: git show ec3fbd0:Client/Gun.cs)
    - [x] create a little spinning AK-47 pickup
    - [x] add client-side pickup logic
- [ ] consolidate player and object logic, add health to players

- [ ] sync shooting (server-authoritative hitscan + lag compensation)
    - [ ] rewind

- [ ] simulate latency and jitter
- [ ] client proxy for ensuring state sync works

Go through and trace the object syncing logic and document how it works

A. Interpolate remote players
B. Predict Local players
C. Rollback for PvP actions

5. PVP Mechanics
- [ ] Player health
    - [ ] spawn crates with health
    - [ ] add health pack pickups
    - [ ] pistol pickup
    - [ ] grenade pickup
    - [ ] sniper rifle
    - [ ] shotgun

6. Generate a map with perlin noise
7. Simple UI for playing with noise
7a. Add client proxy for tracking what chunks are active and what objects to replicate (this is gonna be a huge fucking pain >:())

8. Play around with scripting
    - [ ] add a debug terminal
    - [ ] add some basic scripting functionality with basic parser 
9. Add inventory UI
10. Host server and test with buddies
- [ ] digital ocean droplet
- [ ] hook up scrungy.com domain name
11. Create a simple free-for-all demo for testing
    - [ ] load a map from a PNG
    - [ ] random spawns
    - [ ] fixed health kit locations
    
12. Bug Fixes from FFA demo
13. Generate meshes for chunks + performance enhancements
14. Create water, ground, and grass shaders
https://www.youtube.com/watch?v=GOfttJQ-FGw&t=19s
- [ ] grass
    - [ ] compute shader
    - [ ] no asset, just direct geometry
    - [ ] simplex noise
        - [ ] height
        - [ ] color
    - [ ] animate wind
    - [ ] squish the grass

15. Create structures
16. CTF gamemode

Debug Stuff
- [ ] debug draw chunk borders

- generate textures with noise
- generate trees with noise
- create a water shader for tiles

Areola vid
https://www.youtube.com/watch?v=Y0Ko0kvwfgA

https://nicogo1705.github.io/AssetStore/asset?id=com.nicogo.grass
nicogo1705.github.io/AssetStore/asset?id=com.nicogo.marching-cube-compute-shader


SDSL overview
https://hackmd.io/@vN9HDo5XQAGVCM_epmoJBA/S1LxeorWT