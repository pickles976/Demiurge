# Chunk Generation

# Me Work
- [x] map different textures
      - [x] create grass texture
      - [x] create dirt texture
      - [x] create stone texture

- [ ] read about Minecraft's terrain generation
- [ ] update noise generation
- [ ] make steep slops exposed stone.

- [ ] add grass back in
- [ ] add tree generation

- [ ] read about cave carving
- [ ] add cave carving

- [ ] add resource deposits

# LLM Work
- [x] add server-authoritative terrain collision for player movement
      Shared SDF character controller in `Common`, collided against the existing
      `ChunkMap` / `Voxel.Distance` field. Server stays Stride/Bepu-free.
    - [x] add engine-free terrain collision queries in `Common/Voxel/`:
          trilinear distance sampling, central-difference normals, capsule
          penetration resolution, and collide-and-slide movement.
    - [x] replace flat `PlayerMovement.Step` with a shared kinematic step that
          takes terrain, position, velocity, grounded state, input, and `dt`.
    - [x] store movement velocity / grounded state on both `ServerPlayer` and
          `LocalPlayer` so prediction replay matches server authority.
    - [x] update reconciliation and `PlayerPositionData` to carry velocity and
          grounded, not just position.
    - [x] support walls, slopes, ceilings, caves, and overhangs; `HighestSurfaceY`
          used only for spawning.
    - [x] gravity and a 1.5 m SPACE jump, sized so the DISCRETE apex is 1.5 m.
    - [x] Common tests: flat ground, wall slide, slope limit, ceiling hit,
          cave/overhang, chunk border, missing chunk, burial, determinism,
          replay, and one case on real generated terrain.
      Deliberately not in it: player-vs-player collision, colliders on pickups,
      step-up over placed blocks, crouch changing capsule height, swept
      collision beyond sub-stepping. Lag compensation is untouched — hit rewind
      still only needs position.

- [ ] switch to first-person view (keep third person controller as dead code)
      - [ ] first-person
      - [x] tilde for freecam
      - [ ] F5 for third-person camera
- [ ] add digging
      - [ ] left click with no weapon
      - [ ] dig instantly
      - [ ] dig just a little bit
      - [ ] highlight the surface you are going to dig (ghost block?)
- [ ] add placing blocks
      - [ ] place an entire block

- [ ] track loaded chunks per player
- [ ] load chunks as player moves around
- [ ] do we need LOD?

- [ ] limit the map to 1km x 1km to start. 
- [ ] generate beyond 1km but create an invisible barrier at 1km x 1km
