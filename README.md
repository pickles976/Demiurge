https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

1. ~~Move the player~~
2. Camera controls
    - [x] fullscreen
    - [x] math utils
    - [ ] player state
    - [ ] replicate camera functionality
        - [ ] rotation
        - [ ] lookahead aiming
    - [ ] replicate player functionality
        - [ ] move relative to camera
        - [ ] only play walk cycle when moving
    - [ ] player looks at mouse by default
3. Shooting
- [ ] add litte white dot
- [ ] aimline
- [ ] reticle
- [ ] add gun
    - [ ] press "F" to spawn and despawn gun
    - [ ] parent gun to player
- [ ] spawn bullets
    - [ ] aim line originate at gun barrel end
- [ ] actually spawn bullet entities
- [ ] delete bullets
- [ ] gun config
    - [ ] rate of fire
    - [ ] magazine capacity
    - [ ] reloading
- [ ] add UI
    - [ ] ammo count
    - [ ] reloading prompt
- [ ] spread
- [ ] add muzzle flare
- [ ] add sound
- [ ] tune reticle and accuracy
- [ ] add collision to ground, aim gun towards ground

- [ ] clean up player aiming logic and stuff

6. Networking
- [ ] separate out client and backend
- [ ] BIG REFACTOR
Where does UI live? How much should each of these modules know about each other?

7. Generate a map with perlin noise
8. Simple UI for playing with noise

9. PVP Mechanics
- [ ] shooting at targets (add a crate)
- [ ] add raycast (bullet tracers are just for visualization)
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
    https://bevy.org/examples/animation/animated-mesh/
    - [ ] hook up animation controller
    - [ ] add animations
        - [ ] aiming
        - [ ] walking (unarmed)
        - [ ] walking (armed)
        - [ ] crouching 
        - [ ] sprinting 
13. Generate meshes for chunks + performance enhancements
14. Create water, ground, and grass shaders
15. Create structures
16. CTF gamemode

Debug Stuff
- [ ] debug draw chunk borders

- generate textures with noise
- generate trees with noise
- create a water shader for tiles