https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

`dotnet run --launch-profile singleplayer`

NOTES:
- particle system broken
https://github.com/stride3d/stride/issues/2496

`dotnet run` -- client
`dotnet run --project Server/DemiurgeServer.csproj` -- server

`dotnet build DemiurgeSharp.slnx`

Developer terminal: backtick/tilde. Free camera: `F3`. See `docs/COMMANDS.md`.

6. Map Editor System
    - [ ] load specific maps on server start
    - [ ] map editing
        - [ ] saving and loading
        - [ ] editing in 3D
            - [ ] map editor
            - [ ] structure editor
            - [ ] load and save structures
            - [ ] create walls and structures
            - [ ] spawn structures in the world
            - [ ] set spawn points for teams
7. Add inventory UI
8. Create a simple free-for-all demo for testing
    - [ ] load a map from a PNG
    - [ ] random spawns
    - [ ] fixed health kit locations
9. Host server and test with buddies
- [ ] digital ocean droplet
- [ ] hook up scrungy.com domain name
10. Bug Fixes from FFA demo

11. Create structures
    - [ ] spawn castles
    - [ ] spawn mortars
12. one-sided assault gamemode

13. Open-World Systems

Add client proxy for tracking what chunks are active and what objects to replicate (this is gonna be a huge fucking pain >:())
- [ ] track loaded chunks per player (server-side)
- [ ] load chunks as player moves around
- [ ] view-distance meshing: don't mesh sections past a radius.

- [ ] read about cave carving
- [ ] add cave carving

- [ ] add resource deposits

- [ ] how far should we be able to see?

Debug Stuff
- [ ] debug draw chunk borders

Areola vid
https://www.youtube.com/watch?v=Y0Ko0kvwfgA

https://nicogo1705.github.io/AssetStore/asset?id=com.nicogo.grass
nicogo1705.github.io/AssetStore/asset?id=com.nicogo.marching-cube-compute-shader

SDSL overview
https://hackmd.io/@vN9HDo5XQAGVCM_epmoJBA/S1LxeorWT

https://m.youtube.com/watch?v=PLMcCKeJ6f0&list=WL&index=56&pp=iAQBsAgC
https://www.boristhebrave.com/2018/04/15/dual-contouring-tutorial/
https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14

Tree shader, grass shader, etc
https://www.youtube.com/watch?v=GOfttJQ-FGw&t=19s


Big Systems
- [x] multiplayer
- [x] terrain
- [x] inventory
- [ ] scripting system
