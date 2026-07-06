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
- [ ] sync animations: send InputButtons up, server derives PlayerStateFlags, broadcast State down
- [ ] interpolate remote players (tick-stamped snapshot buffer, render ~100ms in the past)
- [ ] prediction reconciliation (server acks Sequence, client replays unacked inputs)
- [ ] validate inputs server-side (clamp Intent to unit length, sanity-check flags)

- [ ] object registry (NetworkId + ObjectType, spawn/despawn/state messages)

- [ ] gun as pickup/equip object (Weapon state flag; old shooting code: git show ec3fbd0:Client/Gun.cs)
- [ ] sync shooting (server-authoritative hitscan + lag compensation)
- [ ] sync health and stuff

5. Generate a map with perlin noise
6. Simple UI for playing with noise

7. PVP Mechanics
- [ ] Player health
    - [ ] spawn crates with health
    - [ ] damage crates when hit by bullet
    - [ ] destroy crates when HP < 0.0
    - [ ] add this logic to players
    - [ ] add health pack pickups
8. Play around with scripting
    - [ ] add a debug terminal
    - [ ] add some basic scripting functionality with basic parser 
9. Add inventory and pickups
    - [ ] AK
    - [ ] sniper rifle
    - [ ] shotgun
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