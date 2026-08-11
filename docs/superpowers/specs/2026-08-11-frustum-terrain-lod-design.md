# Frustum-Aware Terrain LOD — Design

**Date:** 2026-08-11
**Status:** Implemented

## Problem

Aiming down a rifle's sights showed the coarse LOD representation of the terrain, which is wrong
for anything thin. A `structures/` wall is **one voxel thick** (`demiurge:stone_bricks`, see
`structures/wood_shack.json`); LOD 1 samples every second voxel, so such a wall is not sampled at
all. LOD 1 began at 112 m, and 112 m is well inside the range you shoot at through an optic.

The diagnosis that mattered, though, is that the old rule was not really a distance rule:

```
SplitWithin = { 0f, 112f, 224f }
```

A level-L box has cells of `2^L` world units. Both boundaries are exactly `56 × cellSize`. Feed the
hip field of view (74°, 1080-tall back buffer) into the standard projected-size formula and `56 m per
metre of cell` is what a constant **12.8 px of geometric error** works out to. The table was already
a screen-space error budget — with the field of view baked into its constants.

`FirstPersonCameraScript` then changes the field of view underneath it: 74° hip → 56° ADS → divided
by the optic's `AimMagnification` (on the tangent, correctly). At 4× that is roughly five times the
pixels per degree, so the real error is five times the budget the constants encode, and nothing
recomputed. Hence: the wall vanishes through a scope and not through iron sights.

## Approach

**The currency is pixels of geometric error.** A node splits when its own cells subtend more than
`PixelErrorBudget` pixels. Everything the old code decided separately falls out of that one number
rather than being asked for:

- zooming narrows the lens, which raises the error, which refines — no `if (aiming)` anywhere;
- ground the view does not cover subtends nothing, so it coarsens — culling is not a second rule;
- the old distance table is what the formula evaluates to at the hip field of view, so hip-fire
  selection is unchanged in kind and cheaper in practice.

This is the method in CLAUDE.md's *complete systems, not special cases* section, and — as that
section says to expect — the design work was finding the unit, not choosing the algorithm that
compares in it. A* was the cheap half of navigation; the priority queue is the cheap half here.

### Rejected alternatives

- **Threshold walk with no ceiling.** Roughly a 20-line change to the existing recursion. Rejected
  because its bound would have been my arithmetic, and `ClientTerrain`'s own comments record this
  pipeline's bottleneck being mis-identified three times by reasoning about it.
- **Aim-cone spotlight** — refine a narrow cone around the aim ray. Cheapest, but it is a branch
  bolted to the side of the search, and it leaves a detail seam at the cone edge when panning.
- **Threshold walk plus a binary search on the threshold** to fit a budget. 3–4 re-walks, and the
  walk is now on the turn path where that multiplier lands in the frame.

## Design

### Selection: `Common/Voxel/TerrainView.cs`, `Common/Voxel/TerrainLod.cs`

`TerrainView` carries an eye, a basis, `tanHalfFovY`, aspect and viewport height, and exposes exactly
two questions: `PixelError(featureSize, distance)` and `Intersects(min, max)`.

`TerrainLod` is now an instance class (it owns a reusable priority queue, because selection runs on
view change rather than once). Refinement seeds the coarse roots, then splits worst-error-first until
a section ceiling is reached.

**Two forced splits bypass the ceiling**, because denying them reintroduces the bugs they exist to
prevent:

- a node **straddling the world edge** — a coarse box there reads chunks that never arrive and
  retries forever (this rule predates the change and is unaltered);
- a node within **`UnconditionalRadius` (64 m)** of the eye — the deliberate special case. A pure
  error model leaves the ground behind you coarse, and you turn faster than terrain can be meshed.

**The anchor is the active observer.** During normal play `TerrainViewBuilder` takes eye and facing
from the player, keeping camera shake out of selection, and takes the lens from the camera so scoping
refines. While F3 is active, eye and facing come from the detached camera instead, moving the detail
bubble and view-weighted refinement to the area being spectated. The editor likewise uses its fly
camera because it is the only observer there.

### Cache: `Client/Rendering/ClientTerrain.cs`

Selection changes with the view now, not only with position, so aiming across a valley and lowering
the rifle would otherwise mesh the same few hundred boxes twice a second. Meshing results are kept
and `Entity.Scene` is toggled instead.

- **Empties are cached too.** Three quarters of boxes are open air. A cache that only remembered
  geometry would re-mesh the empty majority of the world on every view change — the same mistake
  `resolved` already exists to prevent, one layer along.
- **`resolved.IntersectWith(desired)` is deleted**, and that single line *is* the cache. It was there
  for a correctness reason: `EnqueueDirty` drops marks for boxes that are not currently desired, so a
  box that left the set could miss an edit and return stale.
- **That obligation moved to `DirtySectionSink.Add`,** which now visits *every* level containing the
  changed voxel instead of returning at the first desired one. Exactly one level is desired (levels
  partition the world) and is re-meshed as before; the other two may hold cached geometry predating
  the edit and are evicted. Edits are rare next to view changes, so paying one re-mesh later is the
  right side of that trade.
- **LRU eviction** on two ceilings: `MaxCachedGeometry` (1024 sections holding GPU buffers) and
  `MaxCachedSections` (16,384 entries including empties). Only detached, undesired boxes are
  candidates.
- **`RetireCovered` cancels a retirement** when its box becomes desired again, which is what stops the
  cache opening a hole every time the view swings away and back.

### Reselection gating

Recompute on a chunk crossing (as before), a **5°** turn, or a **2%** change in `tanHalfFovY`.

The selection frustum is **widened by 8°**. This does not remove the boolean edge where boxes flip
level — it moves that edge outside what is drawn, so the churn happens where nothing is visible.
Hysteresis in the geometry rather than in a scheduler, which would have delayed genuine refinement
too.

## Measurements

Selection cost, from the middle of the conquest map:

| lens | sections | vs. old rule |
|---|---:|---:|
| old omnidirectional rule | 4,092 | — |
| hip (74°) | 3,126 | **−24%** |
| 2× | 5,114 | +25% |
| **3× (peak)** | **6,150** | **+50%** |
| 4× | 5,632 | +38% |
| 8× | 4,778 | +17% |
| 32× | 4,134 | +1% |

**Cost peaks in the MIDDLE of the magnification range, not at the top.** Zooming trades cone width
for depth, and depth is capped by a 1 km world, so past about 3× the narrowing wins. This contradicted
the estimate the design was sketched against — the first ceiling test asserted that a 16× optic would
exhaust the budget, and it failed — and it is the single most useful thing to know before tuning any
constant here.

Consequently **`MaxDesiredSections = 8192` does not currently bind at any magnification.** It is kept
as a guard against a bigger world, a lower `PixelErrorBudget`, or another LOD level.
`TerrainLodTests.SelectionCostStaysUnderTheCeilingAtEveryMagnification` asserts it is *not* reached,
so it will fail when one of those changes rather than silently degrading what a player is looking at.

`CollectDesired` costs **0.31 ms at hip, 0.53 ms at the 3× peak**. At 5° and a fast 180 °/s turn that
is one reselection every 28 ms — about 3% of a single 16.6 ms frame, and nothing on the frames
between. Halving `ReselectDegrees` doubles it.

One quadratic was introduced and fixed during implementation: finding a superseded box's replacements
scanned the whole desired set per box, which was affordable when reselection happened on chunk
boundaries and is not when it happens on turning. Levels nest exactly, so replacements are now found
by **shifting** — at most 72 lookups.

## Testing

`Common.Tests/TerrainLodTests.cs`, 16 tests, all properties of the model rather than traces through
the refinement loop — which matters unusually much here, because the thing being replaced was a table
of three distances and it would be easy to pin those three numbers again.

- the old table is what the formula evaluates to at hip (`56 × cellSize` ⇒ 12.8 px);
- narrowing the lens refines what is aimed at, at 150 m, which is the reported bug;
- ground behind the eye stays coarse, and ground inside the bubble does not;
- **leaves tile the meshable world exactly once** — the watertightness invariant, and the one most
  easily broken by anything touching splitting or dropping;
- every leaf carries its whole column;
- hip fire costs less than the omnidirectional rule it replaced (`TerrainView.Everywhere` reproduces
  that rule exactly, so it is a like-for-like comparison rather than a remembered number);
- a small turn changes no level **inside the unwidened frustum** — the visible-stability invariant,
  which is what the 8° margin actually buys.

Full fast suite green: 820 tests.

## Open

Two things deliberately left as judgment calls rather than decided in code:

1. **The bubble drop from 112 m to 64 m is the payback and the part most likely to feel wrong.**
   Ground behind you between 64 m and 112 m is now LOD 2 until you turn; the first turn in a fresh
   area is coarse for a moment and every later one is a cached toggle. `UnconditionalRadius` is the
   one knob.
2. **`MaxCachedGeometry = 1024` is a guess, not a measurement.** Batched buffers are refcounted
   64-ways (`SectionBuffers`), so one cached section can pin 63 others' VRAM — and on the SER5 the
   iGPU shares DDR4 with the CPU, so that is real memory pressure rather than spare VRAM. Per-buffer
   byte accounting was deliberately **not** built; the `terrain cache:` log line reports
   geometry/empty/reused/evicted/invalidated counts, which will show whether pinning is a real problem
   before anyone builds machinery for it. Measure before optimizing.

Not addressed, and pre-existing: the world-edge ring is force-split to fine detail, which is a
meaningful share of the section count for terrain nobody looks at. CLAUDE.md's terrain section also
still lists LOD under "still missing", which was stale before this change.
