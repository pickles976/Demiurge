# Chunk Generation

- [x] copy line 56 tilemap.cs in Demiurge. We have noise heights, but we also need to be able to take that index in the array and return block coordinates:
`convert_chunk_coords_and_block_index_to_global_block_coordinates`
- [x] spawn cubes
- [x] texture ubes with test texture
- [x] spawn cubes at height based off of perlin noise

- [x] add a debug fly camera

1. Generate Noise Texture
2. Fill Chunk with voxel data containing density information
3. Use chunk to spawn cubes, create a function that maps density to block type and change material color based off of that

- [x] instead of just a surface, make each chunk a 3D 16x16x128 of density + block type
      (still cube entities — meshing and real storage come after)
    - [x] generate the cubes
    - [x] assign block type
    - [x] test for 3x3 chunks, tweak noise settings until it looks good

- [ ] remove cubes and use dual contouring method to generate geometry
      (needs: gradient by central differences on the density field, the cross-chunk accessor,
      and a padded scratch buffer — see DATA_MODEL.md)
- [ ] clean up appearance
- [ ] move to server
- [ ] have claude configure a build that launches server and client simultaneously for local "singleplayer"
- [ ] connect triplanar mapping and texturing and stuff
- [ ] have claude add grass back in