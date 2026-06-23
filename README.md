https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

4. ~~Render a cube with a texture~~
5. Generate a map with perlin noise
6. Simple UI for playing with noise
7. Render a player
8. Move the player
9. Shooting

### Camera Controls
https://www.youtube.com/watch?v=ijN3gobR6Zo&t=1s

### Shooting
- [x] add litte white dot
- [x] refactor systems to use MainCamera component
- [x] aimline
- [x] reticle
- [x] add gun
    - [x] press "F" to spawn and despawn gun
    - [x] parent gun to player
- [x] spawn bullets
    - [x] figure out where to put pivot
    - [x] figure out where barrel is relative to model
    - [x] aim line originate at gun barrel end
    - [x] hook up observer
- [x] switch to 0.19.0
- [x] get it compiling
- [x] actually spawn bullet entities
- [x] delete bullets
- [x] gun config
    - [x] rate of fire
    - [x] magazine capacity
    - [x] reloading
- [ ] add UI
    - [ ] ammo count
    - [ ] reloading prompt
- [ ] spread
- [ ] add muzzle flare
- [ ] add sound
- [ ] tune reticle and accuracy
- [ ] add collision to ground, aim gun towards ground

- [ ] clean up player aiming logic and stuff

10. Networking
- [ ] separate out client and backend

- [ ] BIG REFACTOR
Where does UI live? How much should each of these modules know about each other?

11. PVP Mechanics
- [ ] shooting at targets (add a crate)
- [ ] add raycast (bullet tracers are just for visualization)
- [ ] Player health
    - [ ] spawn crates with health
    - [ ] damage crates when hit by bullet
    - [ ] destroy crates when HP < 0.0
    - [ ] add this logic to players
    - [ ] add health pack pickups
12. Play around with scripting
    - [ ] add a debug terminal
    - [ ] add some basic scripting functionality with basic parser 
13. Add inventory and pickups
    - [ ] AK
    - [ ] sniper rifle
    - [ ] shotgun
14. Create a simple free-for-all demo for testing
    - [ ] load a map from a PNG
    - [ ] random spawns
    - [ ] fixed health kit locations
    
15. Bug Fixes from FFA demo
16. Player art
    - [x] create guy with blockbench
    https://bevy.org/examples/animation/animated-mesh/
    - [ ] hook up animation controller
    - [ ] add animations
        - [ ] aiming
        - [ ] walking (unarmed)
        - [ ] walking (armed)
        - [ ] crouching 
        - [ ] sprinting 
17. Generate meshes for chunks + performance enhancements
18. Create water, ground, and grass shaders
19. Create structures
20. CTF gamemode

Debug Stuff
- [ ] debug draw chunk borders

- generate textures with noise
- generate trees with noise
- create a water shader for tiles