https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

`dotnet run --project Client`

NOTES:
- particle system broken
https://github.com/stride3d/stride/issues/2496


1. ~~Move the player~~
2. ~~Camera controls~~

3. Shooting

- [ ] shooting at targets
    - [x] add a dummy
    - [x] add raycast (bullet tracers are just for visualization)
    - [x] log when dummy is hit

6. Networking
- [ ] separate out client and backend
    - [ ] hardcode IP into game for now
    - [ ] sync players
    - [ ] sync weapon spawning
    - [ ] sync gun visuals and sounds
    - [ ] add a login screen to client

7. Host server and test with buddies
- [ ] digital ocean droplet
- [ ] hook up scrungy.com domain name

8. Generate a map with perlin noise
9. Simple UI for playing with noise

10. PVP Mechanics
- [ ] Player health
    - [ ] spawn crates with health
    - [ ] damage crates when hit by bullet
    - [ ] destroy crates when HP < 0.0
    - [ ] add this logic to players
    - [ ] add health pack pickups
11. Play around with scripting
    - [ ] add a debug terminal
    - [ ] add some basic scripting functionality with basic parser 
12. Add inventory and pickups
    - [ ] AK
    - [ ] sniper rifle
    - [ ] shotgun
13. Create a simple free-for-all demo for testing
    - [ ] load a map from a PNG
    - [ ] random spawns
    - [ ] fixed health kit locations
    
14. Bug Fixes from FFA demo
15. Generate meshes for chunks + performance enhancements
16. Create water, ground, and grass shaders
- [ ] grass
    - [ ] compute shader
    - [ ] no asset, just direct geometry
    - [ ] simplex noise
        - [ ] height
        - [ ] color
    - [ ] animate wind
    - [ ] squish the grass

17. Create structures
18. CTF gamemode

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