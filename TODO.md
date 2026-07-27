# Chunk Generation

# LLM Work
- [ ] investigate slow chunk loading
- [x] TCP channel for bulk terrain (ChunkTransport / ChunkTcpServer / ChunkTcpClient)
      Riptide keeps gameplay; terrain has its own stream on port 7778. No rate
      constant any more — a blocking write IS the backpressure. Riptide's
      1225-byte datagram limit is gone too, so a frame is a whole chunk column and
      the slab cursor is gone with it.
- [ ] nearest-first chunk order
      QueueWorldFor still walks x-major from the far corner, so the player's chunk
      is 840th of 1681 and 1.74 MB (55% of the world) is sent before their own
      ground can be meshed. Nearest-first needs 22 KB (0.69%) — measured 80x to
      first mesh. Sort the queue by distance from the player. Cheapest remaining
      win by a wide margin.
- [ ] cache encoded chunks
      ChunkWire.Encode is 0.42 ms/chunk and runs per client per send. Terrain is
      immutable until digging lands, so encode once and write the same buffer to
      every stream. Also caps a writer thread at ~2400 chunks/s today.
- [ ] delta-code density along Y (~1.6x, 1977 -> ~1100 bytes/chunk)
      The one the TCP move UNLOCKED. Roughly 1280 of a chunk's bytes are density
      literals, and they are an arithmetic sequence: density is `y - h`, so
      consecutive slabs differ by exactly 50 quantized units. Delta-coding collapses
      them to ~350 bytes.
      Was shelved because Riptide's reliable channel is unordered, so every message
      had to be independently applicable. A stream is ordered — that objection is
      gone. Unlike the heightmap encoding this survives caves: it exploits vertical
      coherence of the field, not the assumption that a heightmap produced it.
      Whole world 3.17 MB -> ~1.9 MB.
- [ ] coalesce frame writes
      Each chunk is two Write calls (header then payload) with NoDelay on, so ~2
      syscalls and potentially 2 packets per 2 KB chunk. Write header+payload from
      one buffer, or batch several chunks per write. Small but nearly free.
- [ ] MAYBE: compress the whole stream (Deflate/Brotli)
      Would find cross-chunk redundancy the per-chunk format cannot — neighbouring
      chunks have similar material planes. Guessing 1.5-2x on top, at real CPU cost.
      CONFLICTS with "cache encoded chunks": a compressor with state across the
      stream cannot reuse a per-chunk cached buffer, so these two are alternatives
      rather than additive. Measure before assuming it wins; do the cache first
      since it is certain and this is not.
      Also note per-player view distance (below) attacks the same total from a
      better angle: it makes size depend on view radius instead of world area.
- [ ] update documentation

- [ ] chunked LOD (quadtree of virtual chunks)
      Downsample an NxN group of chunks into one coarse grid, mesh it with the
      EXISTING mesher, and scale it on the entity transform. That last part is
      already free: mesh positions are section-local (ChunkMeshFactory), uniform
      scale preserves normals, and the triplanar shader derives UVs from world
      position, so texture scale stays right with no shader change.
    - [ ] downsample the SDF by AVERAGING the block of distances, not point
          sampling — point sampling flips signs between levels, which puts holes
          through ridges. Watch the +/-2.54 quantization clamp: averaging a
          clamped field is not the same as evaluating the true distance coarsely,
          and that's the first suspect if a coarse level grows geometry that
          isn't there.
    - [ ] downsample MATERIAL by most-common-non-air vote. An enum has no mean.
    - [ ] seams: SKIRTS. Extend the coarse mesh's edge downward to plug the gap.
          Decided over vertex-constraining and Transvoxel — ugly up close,
          invisible at distance, and it ships.
      LOD is for RENDERING ONLY. It is not a streaming optimization: the client
      receives full-resolution voxels either way and downsamples them itself.
      That keeps it entirely inside the View layer — no wire change, no LOD level
      on ChunkSlabsData, no multi-resolution ChunkMap, and collision cannot
      accidentally sample a coarse level because Sim never sees one.
      Note a coarse chunk is SHORTER: uniform downsampling shrinks Y too, so
      sections-per-chunk varies by level.
      Do NOT use heightfield LOD (geometry clipmaps / CDLOD). It's easier and
      crack-free by construction, and it's worthless the moment cave carving
      lands — same trap as heightmap wire encoding and static water.
      Deliberately not in it: adaptive octree dual contouring (Ju et al. 2002,
      the canonical crack-free DC answer, but a rewrite of the mesher's
      traversal), QEF-error-driven detail selection, morphing between levels,
      and LOD for anything but rendering.

- [ ] raycasting against terrain
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