https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code

dotnet clean
dotnet restore --no-cache
dotnet build --no-incremental

`dotnet run --project Client`

1. ~~Move the player~~
2. ~~Camera controls~~
3. Shooting

GET SOUND WORKING
- [ ] force use alsa instead of pipewire

- [ ] add muzzle flare

- [ ] clean up player aiming logic and stuff
    - [ ] gun just shoots forward unless we are aiming
    - [ ] draw a reticle when aiming

- [ ] shooting at targets
    - [ ] add a crate
    - [ ] add raycast (bullet tracers are just for visualization)
    - [ ] add collision to ground, aim gun towards ground
    - [ ] destroy crate and emit particles when hit

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
15. Player art
    - [x] create guy with blockbench
    - [x] hook up animation controller
    - [ ] add animations
        - [ ] idle
        - [ ] aiming
        - [x] walking (unarmed)
        - [ ] walking (armed)
        - [ ] crouching 
        - [x] sprinting 
16. Generate meshes for chunks + performance enhancements
17. Create water, ground, and grass shaders
18. Create structures
19. CTF gamemode

Debug Stuff
- [ ] debug draw chunk borders

- generate textures with noise
- generate trees with noise
- create a water shader for tiles