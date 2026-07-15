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

4a. PVP Mechanics
- [x] sniper rifle
    - [x] make lookahead range tied to the weapon itself
- [ ] add wearables
    - [x] add armor
    - [ ] add helmet
- [ ] Player health
    - [ ] add health pack pickups
    - [x] pistol pickup
    - [ ] shotgun pickup
        - [ ] multiple pellets per-shot
    - [ ] grenade pickup
        - [ ] throwing arc
        - [ ] server-side splash damage 

- [ ] clean up UI and stuff

5. Generate a map with perlin noise
6. Simple UI for playing with noise
7. Generate meshes for chunks + performance enhancements
8. Create water, ground, and grass shaders
https://www.youtube.com/watch?v=GOfttJQ-FGw&t=19s
- [ ] grass
    - [ ] compute shader
    - [ ] no asset, just direct geometry
    - [ ] simplex noise
        - [ ] height
        - [ ] color
    - [ ] animate wind
    - [ ] squish the grass

8a. Add client proxy for tracking what chunks are active and what objects to replicate (this is gonna be a huge fucking pain >:())

10. Play around with scripting
    - [ ] add a debug terminal
    - [ ] add some basic scripting functionality with basic parser 
11. Add inventory UI
12. Host server and test with buddies
- [ ] digital ocean droplet
- [ ] hook up scrungy.com domain name
13. Create a simple free-for-all demo for testing
    - [ ] load a map from a PNG
    - [ ] random spawns
    - [ ] fixed health kit locations
    
14. Bug Fixes from FFA demo

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