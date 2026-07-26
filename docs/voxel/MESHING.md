# Meshing the density field

How voxels become triangles. Storage is [DATA_MODEL.md](DATA_MODEL.md), which deliberately
knows nothing about a renderer; this is the other half of that split.

**How to read this.** The first section is a *concept* — surface nets and dual contouring are
one algorithm — and it applies to the first line of mesher code you write. "Known limitations"
is recorded hazard, not a checklist: every item in it is a problem you get *after* a working
single-LOD mesher, and none of it should shape the first version. `TODO.md` scopes the next step.

## Surface nets and dual contouring are one algorithm

Both are *dual* methods: the vertex lives in the cell's interior, dual to the grid, rather than
on grid edges the way marching cubes places it. The skeleton is identical:

1. Walk cells (a cell = the cube spanned by 8 adjacent voxel samples), find edges whose two
   corners disagree in sign.
2. Interpolate each sign-changing edge's corner densities to zero to get a crossing point.
3. Emit **one vertex per surface-containing cell**.
4. For each sign-changing edge, join the vertices of the 4 cells sharing it into a quad.

The only difference is step 3's placement rule:

| | vertex placement | needs |
|---|---|---|
| Surface nets | average of the cell's crossing points | crossings only |
| Dual contouring | QEF minimizer over the tangent planes at the crossings | crossings + normals + a 3×3 solve |

Both are implemented: `GenerateMeshFromSurfaceNet` and `GenerateMeshDualContouring` are two
entry points onto one `Generate(scratch, Placement)` skeleton, differing in a single line.
Everything expensive — cell
iteration, quad winding, the padded scratch buffer, the cross-chunk accessor, building the
vertex/index buffer — is shared, and it is where the bugs are.

This is why the literature and bonsairobo himself write "surface nets (dual contouring)"
interchangeably. They are the same family; treat the names as describing a placement rule.

**Normals are needed either way.** Not for placement — surface nets never looks at them — but for
shading, and later for triplanar blend weights. Derive them from the density gradient by
differences on the corner samples. Because `Density` is approximately a signed distance, its
gradient is already approximately unit length and points along the surface normal.

What DC buys over surface nets is *sharp creases*: averaging the crossings rounds off a corner
that falls between samples, while intersecting tangent planes reconstructs it exactly. Smooth
noise terrain has almost nothing to sharpen. Cliffs and edited geometry do — measured on a wall
meeting flat ground, the crease vertex went from `(6.50, 12.75)` to `(6.86, 12.62)` against a true
corner at `(7.0, 12.5)`.

**DC sharpens geometry, not shading.** A cell still has one vertex carrying one normal, shared by
every quad that touches it, and the field's gradient genuinely rotates over about a voxel at a
concave corner. A crisp edge needs *two* normals at the same position — splitting vertices by
crease angle at buffer-build time. Neither dual method gives you that.

Two failure modes DC adds, both handled in `SolveQef`: the solution can sit far outside its own
cell on a near-flat patch (clamped), and on flat ground every normal is parallel so `AtA` has rank
1 and the minimizer is a whole plane (biased toward the mass point, which avoids needing an SVD).

## Known limitations

These come from the author of the surface nets article's own follow-up ([r/VoxelGameDev
thread](https://www.reddit.com/r/VoxelGameDev/comments/pklhuo/i_wish_i_found_surface_nets_sooner/)) —
i.e. what he learned *after* shipping it. Worth weighting accordingly: knowing all of this, he
still chose surface nets and stayed with it.

### Chunk LOD is the genuinely hard part

Not the meshing. The meshing works. Stitching differently-detailed neighbours without cracks is
the open problem, and his verdicts on the four options:

| approach | verdict |
|---|---|
| Mesh decimation | Doesn't work alone unless you merge meshes across chunks, which is its own nightmare. Better as post-processing on top of another technique. |
| Skirts | Probably fine, but it's "cheating," and won't cover cracks 100% of the time. |
| Stitching | Annoyingly complex and probably expensive. Akin to Transvoxel — another complexity nightmare. |
| **CLOD** (his preference) | Looks very nice. Costs memory and more frequent GPU mesh updates. |

Continuous LOD works like this: every *transition* mesh vertex also stores a **parent vertex**
from the parent chunk's mesh, and the vertex shader blends between the two by camera distance.
That's a per-vertex cost in the *mesh*, not in voxel storage — which is worth noting because it
means the LOD decision doesn't reach back and change the `Voxel` layout.

Reference implementation: <https://dexyfex.com/2016/07/14/voxels-and-seamless-lod-transitions/>

### Dual methods can emit non-manifold meshes

Near certain pseudo-singularities in the SDF, surface nets/DC produce non-manifold geometry. It
looks funky and it breaks normal-based lighting. Mitigation is simply an adequate sample rate for
the field — so this is a "recognize it when you see it" hazard, not something to engineer against
up front.

### Downsampling an SDF does not preserve topology

Thin or small features can pop out of existence entirely when the field is downsampled. Not a
surface nets problem per se — but it compounds with the LOD choice above, because any LOD scheme
that downsamples the *field* (rather than decimating the *mesh*) inherits it. How much it matters
depends on what geometry you're modelling; smooth terrain cares far less than detailed structures.

## References

- [Surface nets deep dive](https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14) — bonsairobo. The implementation guide: crossing interpolation, gradient normals, triplanar mapping + texture splatting. 16³ chunks, one voxel layer of overlap, quads on 3 of 6 faces.
- [Smooth voxel terrain part 2](https://0fps.net/2012/07/12/smooth-voxel-terrain-part-2/) — Mikola Lysenko. The writeup that popularized surface nets, with a reference implementation.
- [cheind/sdftoolbox](https://github.com/cheind/sdftoolbox) — vectorized surface nets / dual contouring in Python. Small enough to read end to end, useful for checking behaviour against.
- [Dual contouring tutorial](https://www.boristhebrave.com/2018/04/15/dual-contouring-tutorial/) — Boris the Brave. The QEF side, if and when placement gets upgraded.
