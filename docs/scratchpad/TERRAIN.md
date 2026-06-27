- try mesh terrain
- try SDF terrain
https://m.youtube.com/watch?v=PLMcCKeJ6f0&list=WL&index=56&pp=iAQBsAgC
https://www.boristhebrave.com/2018/04/15/dual-contouring-tutorial/
https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14

1. Heightmap mesh 

```
for z in 0..CHUNK_SIZE-1 {
    for x in 0..CHUNK_SIZE-1 {
        add_quad(
            height[x][z],
            height[x+1][z],
            height[x][z+1],
            height[x+1][z+1]
        );
    }
}
```

2. Smooth normals

3. texture by slope

4. domain warping

5. ridged noise

6. erosion