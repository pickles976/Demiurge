# Chunk Generation

# LLM Work
- [ ] tune LOD level distances (currently 112 / 224 world units, TerrainLod.SplitWithin)
      by looking. LodSection.MaxLevel is 2 on purpose: past that the +/-2.54
      quantization band is so much smaller than a cell that edge crossings always
      interpolate to the midpoint and the surface goes blocky. Raising it needs a
      finer stored field, not a bigger stride.
- [ ] switch to first-person view (keep third person controller as dead code)
      - [ ] first-person
      - [x] tilde for freecam
      - [ ] F5 for third-person camera
- [ ] raycasting against terrain
- [ ] add digging
      - [ ] left click with no weapon
      - [ ] dig instantly
      - [ ] dig just a little bit
      - [ ] highlight the surface you are going to dig (ghost block?)
- [ ] add placing blocks
      - [ ] place an entire block

- [ ] track loaded chunks per player (server-side)
- [ ] load chunks as player moves around
- [ ] view-distance meshing: don't mesh sections past a radius.
- [ ] how far should we be able to see?

- [ ] tune terrain generation and stuff

- [ ] limit the map to 1km x 1km to start. 
- [ ] generate beyond 1km but create an invisible barrier at 1km x 1km

- [ ] add grass back in
- [ ] add tree generation

- [ ] read about cave carving
- [ ] add cave carving

- [ ] add resource deposits