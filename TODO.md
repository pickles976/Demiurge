# Chunk Generation

https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14

# Me Work
- [ ] map different textures
      - [x] create grass texture
      - [ ] create dirt texture
      - [ ] create stone texture

- [ ] read about Minecraft's terrain generation
- [ ] update noise generation
- [ ] make steep slops exposed stone.

- [ ] add grass back in
- [ ] add tree generation

- [ ] read about cave carving
- [ ] add cave carving

- [ ] add resource deposits

# LLM Work
- [x] move chunk generation to server
- [x] have claude configure a build that launches server and client simultaneously for local "singleplayer"
- [x] spawn the items on the surface (can we say "SpawnOnGround(x,z)"?);

- [ ] time-budget the mesh queue instead of counting sections
      `ClientTerrain.RebuildDirty` currently meshes a fixed `MaxRebuildsPerCall = 4` per frame.
      A count doesn't bound frame time: section cost varies ~3x (0.12 ms empty, 0.34 ms with
      surface, plus crease splitting), and it doesn't adapt across hardware. Replace with a
      ~4 ms budget, meshing until it's spent and leaving the rest for the next frame.
    - [ ] `dirty` becomes a Queue + HashSet pair rather than a bare HashSet. Two reasons: the
          set has no defined order, and `dirty.ToArray()` currently allocates the whole set
          every frame to process 4 of it.
    - [ ] always mesh at least one section per frame even if the budget is already spent, or a
          run of slow frames starves meshing indefinitely.
    - [ ] cap attempts at the queue length on entry, so sections that return false from
          `TryBuild` (neighbour chunk not arrived) get retried next frame rather than spun on
          until the budget expires.
    - [ ] accept that the budget overshoots by one section: elapsed time is checked before
          starting an item, so the worst case is the budget plus the most expensive section.
      Deliberately not in this step: ordering the queue nearest-camera-first. That's the real
      win for streaming — the player sees terrain fill in around them rather than in arbitrary
      order — but it needs the camera position in the View layer, so do it separately.
      `TerrainState.Drain()` stays unbounded; decoding a slab is a ~512-byte memcpy and a full
      chunk is microseconds, so it isn't worth a budget until the map is much bigger.

- [ ] let the cat walk around on the surface

- [ ] switch to first-person view (keep third person controller as dead code)
      - [ ] first-person
      - [x] tilde for freecam
      - [ ] F5 for third-person camera
- [ ] add digging
- [ ] add placing blocks

- [ ] track loaded chunks per player
- [ ] load chunks as player moves around