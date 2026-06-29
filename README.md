https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

`dotnet run --project Client`

1. ~~Move the player~~
2. ~~Camera controls~~
3. Shooting
- [x] add litte white dot
- [x] only play walk cycle when moving
- [x] player looks at mouse by default
    - [x] look at mouse while standing still
    - [x] look in direction of movement while walking
    - [x] look at mouse while aiming
- [x] aimline
- [x] add gun
    - [x] press "F" to spawn gun
    - [x] parent gun to player
    - [x] fix gun rotation1
    - [x] despawn gun
- [x] aim line originate at gun barrel end
- [x] spawn bullets
    - [x] use particle system
- [x] gun config
    - [x] rate of fire
    - [x] magazine capacity
    - [x] reloading
- [ ] add UI
    - [ ] ammo count
    - [ ] reloading prompt
    - [ ] only start reload when user presses R
- [ ] reticle
- [ ] add muzzle flare
- [ ] add sound

- [ ] clean up player aiming logic and stuff

- [ ] shooting at targets (add a crate)
- [ ] add raycast (bullet tracers are just for visualization)
- [ ] add collision to ground, aim gun towards ground
- [ ] exploding boxes

6. Networking
- [ ] separate out client and backend
- [ ] BIG REFACTOR
Where does UI live? How much should each of these modules know about each other?

7. Generate a map with perlin noise
8. Simple UI for playing with noise

9. PVP Mechanics
- [ ] Player health
    - [ ] spawn crates with health
    - [ ] damage crates when hit by bullet
    - [ ] destroy crates when HP < 0.0
    - [ ] add this logic to players
    - [ ] add health pack pickups
10. Play around with scripting
    - [ ] add a debug terminal
    - [ ] add some basic scripting functionality with basic parser 
11. Add inventory and pickups
    - [ ] AK
    - [ ] sniper rifle
    - [ ] shotgun
12. Create a simple free-for-all demo for testing
    - [ ] load a map from a PNG
    - [ ] random spawns
    - [ ] fixed health kit locations
    
13. Bug Fixes from FFA demo
14. Player art
    - [x] create guy with blockbench
    - [x] hook up animation controller
    - [ ] add animations
        - [ ] idle
        - [ ] aiming
        - [x] walking (unarmed)
        - [ ] walking (armed)
        - [ ] crouching 
        - [x] sprinting 
13. Generate meshes for chunks + performance enhancements
14. Create water, ground, and grass shaders
15. Create structures
16. CTF gamemode

Debug Stuff
- [ ] debug draw chunk borders

- generate textures with noise
- generate trees with noise
- create a water shader for tiles