using System.Numerics;
using Demiurge;
using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the LOD MODEL, not traces through the refinement loop.
///
/// The distinction matters here more than usual, because the thing being replaced was a table of
/// three distances and it would be very easy to write tests that pin those three numbers again. What
/// is worth defending is that error in pixels is the only currency: that the old table is what the
/// formula evaluates to at the hip field of view, that narrowing the lens refines and facing away
/// coarsens without either being asked for separately, and that the leaf set still tiles the world.
/// </summary>
public class TerrainLodTests
{
    /// <summary>74 degrees vertical — FirstPersonCameraScript.HipFieldOfView.</summary>
    static readonly float HipTanHalfFov = MathF.Tan(74f * MathF.PI / 360f);

    const float ViewportHeight = 1080f;
    const float Aspect = 16f / 9f;

    /// <summary>Middle of chunk (0, 0), a little above the ground. Well away from the world edge,
    /// whose straddling nodes are force-split and would confuse every distance argument below.</summary>
    static readonly Vector3 Eye = new(8f, 40f, 8f);

    static TerrainView View(Vector3 eye, Vector3 forward, float tanHalfFov)
        => new(eye, forward, Vector3.UnitY, tanHalfFov, Aspect, ViewportHeight,
               TerrainLod.FrustumMarginDegrees);

    static HashSet<LodSection> Select(TerrainView view, out TerrainLod lod)
    {
        lod = new TerrainLod();
        var desired = new HashSet<LodSection>();
        lod.CollectDesired(view, desired);
        return desired;
    }

    static HashSet<LodSection> Select(TerrainView view) => Select(view, out _);

    /// <summary>The leaf nodes of a selection, without the column expansion.</summary>
    static HashSet<(int X, int Z, int Level)> Nodes(HashSet<LodSection> desired)
    {
        var nodes = new HashSet<(int, int, int)>();
        foreach (var box in desired) nodes.Add((box.X, box.Z, box.Level));
        return nodes;
    }

    /// <summary>The level the leaf set covers one chunk column at, or -1 if nothing does.</summary>
    static int LevelAt(HashSet<(int X, int Z, int Level)> nodes, int chunkX, int chunkZ)
    {
        for (int level = 0; level <= LodSection.MaxLevel; level++)
            if (nodes.Contains((chunkX >> level, chunkZ >> level, level))) return level;

        return -1;
    }

    static int LevelAt(HashSet<LodSection> desired, int chunkX, int chunkZ)
        => LevelAt(Nodes(desired), chunkX, chunkZ);

    /// <summary>
    /// The pixel error the OLD distance table corresponded to. <c>SplitWithin = {0, 112, 224}</c> is
    /// 56 world units per world unit of cell size, which at the hip lens on a 1080-tall buffer is
    /// this many pixels. Kept as a literal because it is a historical fact about the code this
    /// replaced, not a live tuning value.
    /// </summary>
    const float LegacyTablePixelError = 12.8f;

    /// <summary>
    /// The old table, recovered from the formula — the claim that those three constants were a
    /// screen-space error budget with the field of view baked into them. This is what licenses
    /// reasoning about the new system in terms of the old distances, so it is worth pinning even
    /// though nothing reads 56 any more.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HipFieldOfViewReproducesTheOldDistanceTable(int level)
    {
        var view = View(Eye, Vector3.UnitZ, HipTanHalfFov);
        float cellSize = 1 << level;

        Assert.Equal(LegacyTablePixelError, view.PixelError(cellSize, 56f * cellSize), 1);
    }

    /// <summary>
    /// The live budget is deliberately SHARPER than the table it replaced, because thin structures
    /// read wrong at ordinary engagement range and not only through an optic. Guards against somebody
    /// "restoring" the historical value while thinking they are restoring a default.
    /// </summary>
    [Fact]
    public void TheLiveBudgetIsSharperThanTheTableItReplaced()
    {
        Assert.True(
            TerrainLod.PixelErrorBudget < LegacyTablePixelError,
            $"budget {TerrainLod.PixelErrorBudget} px is no sharper than the old table's "
          + $"{LegacyTablePixelError} px");
    }

    /// <summary>
    /// What that budget buys, stated as the distance it is actually chosen for: full detail out to
    /// 200 m at the hip field of view, which is past the range most of this map's fighting happens
    /// at. Expressed as a distance because that is the reviewable form — "6.4 pixels" is not
    /// something anybody can hold an opinion about.
    /// </summary>
    [Fact]
    public void FullDetailReachesTwoHundredMetresAtHipFire()
    {
        var hip = Nodes(Select(View(Eye, Vector3.UnitZ, HipTanHalfFov)));

        Assert.Equal(0, LevelAt(hip, 0, 200 / ChunkConstants.ChunkWidth));
    }

    /// <summary>
    /// The bug this whole change exists for. A one-voxel wall is unsampled by LOD 1's stride of 2, so
    /// wherever hip fire stops asking for LOD 0, a narrowed lens — which multiplies the pixels a given
    /// error covers — has to keep asking further out.
    ///
    /// The distance is picked to sit beyond the hip budget's reach whatever that budget is tuned to;
    /// if <see cref="TerrainLod.PixelErrorBudget"/> ever sharpens far enough that hip fire covers
    /// 300 m, this fails and wants moving rather than deleting.
    /// </summary>
    [Fact]
    public void NarrowingTheLensRefinesWhatIsAimedAt()
    {
        const int chunkZ = 300 / ChunkConstants.ChunkWidth;

        var hip = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov));
        var scoped = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov / 4f));

        Assert.Equal(1, LevelAt(hip, 0, chunkZ));
        Assert.Equal(0, LevelAt(scoped, 0, chunkZ));
    }

    /// <summary>
    /// The payback, and it is the same rule rather than a second one: ground the view does not cover
    /// subtends no error, so it sits at the coarsest level however hard the player is zooming.
    /// </summary>
    [Fact]
    public void GroundBehindTheEyeStaysCoarse()
    {
        var scoped = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov / 4f));

        Assert.Equal(LodSection.MaxLevel, LevelAt(scoped, 0, -150 / ChunkConstants.ChunkWidth));
    }

    /// <summary>
    /// The one deliberate exception. A pure error rule leaves the ground behind you coarse, and you
    /// turn faster than terrain meshes, so close ground is full detail whichever way the view points.
    /// </summary>
    [Fact]
    public void GroundInsideTheBubbleIsFullDetailWhicheverWayYouFace()
    {
        var scoped = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov / 4f));

        Assert.Equal(0, LevelAt(scoped, 0, -1));
        Assert.Equal(0, LevelAt(scoped, -1, 0));
    }

    /// <summary>
    /// No holes and no double cover: every meshable chunk column belongs to exactly one leaf, at
    /// exactly one level. This is what keeps the world watertight, and it is the invariant most
    /// easily broken by anything that changes how nodes are split or dropped.
    /// </summary>
    [Fact]
    public void LeavesTileTheMeshableWorldExactlyOnce()
    {
        var desired = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov / 4f));

        var nodes = new HashSet<(int X, int Z, int Level)>();
        foreach (var box in desired) nodes.Add((box.X, box.Z, box.Level));

        for (int cx = WorldGen.MeshableMin.x; cx <= WorldGen.MeshableMax.x; cx++)
            for (int cz = WorldGen.MeshableMin.z; cz <= WorldGen.MeshableMax.z; cz++)
            {
                int covers = 0;
                for (int level = 0; level <= LodSection.MaxLevel; level++)
                    if (nodes.Contains((cx >> level, cz >> level, level))) covers++;

                Assert.True(covers == 1, $"chunk ({cx}, {cz}) is covered by {covers} leaves, expected 1");
            }
    }

    /// <summary>
    /// A leaf carries its whole column, so an edit high above the surface still lands in a box that
    /// exists. Cheap to get wrong when the section count per level changes.
    /// </summary>
    [Fact]
    public void EveryLeafCarriesItsWholeColumn()
    {
        var desired = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov));

        var columns = new Dictionary<(int X, int Z, int Level), int>();
        foreach (var box in desired)
        {
            columns.TryGetValue((box.X, box.Z, box.Level), out int count);
            columns[(box.X, box.Z, box.Level)] = count + 1;
        }

        foreach (var (node, count) in columns)
            Assert.Equal(LodSection.SectionsPerColumn(node.Level), count);
    }

    /// <summary>
    /// The cost of a selection is bounded across the whole range of optics the game can mount, and
    /// bounded BELOW the ceiling, so refinement never degrades what a player is actually looking at.
    ///
    /// The shape here is worth knowing before tuning anything, because it is not the intuitive one:
    /// cost peaks in the MIDDLE of the magnification range, not at the top. Zooming trades cone width
    /// for depth, depth is capped by a 1 km world, and past about 3x the narrowing wins. Measured on
    /// this map, sections go 3,126 at hip, 5,114 at 2x, 6,150 at 3x, 5,632 at 4x and back to 4,134 at
    /// 32x. So the ceiling does not currently bind at any magnification — it is a guard against a
    /// bigger world, a lower <see cref="TerrainLod.PixelErrorBudget"/>, or another LOD level, and this
    /// test is what will notice when one of those changes.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    [InlineData(8f)]
    [InlineData(16f)]
    public void SelectionCostStaysUnderTheCeilingAtEveryMagnification(float magnification)
    {
        Select(View(Eye, Vector3.UnitZ, HipTanHalfFov / magnification), out var lod);

        Assert.False(
            lod.LastHitCeiling,
            $"{magnification}x exhausted the refinement budget at {lod.LastSectionCount} sections");
        Assert.True(
            lod.LastSectionCount <= TerrainLod.MaxDesiredSections,
            $"{magnification}x asked for {lod.LastSectionCount} sections against a "
          + $"{TerrainLod.MaxDesiredSections} ceiling");
    }

    /// <summary>
    /// The payback, quantified: hip fire selects FEWER sections than the omnidirectional rule this
    /// replaced, because ground behind the player stops being held at full detail. That surplus is
    /// what funds the aim cone, and if it ever inverts, this change stopped paying for itself.
    ///
    /// <see cref="TerrainView.Everywhere"/> with the hip lens is the old rule exactly — same error
    /// scale, no frustum — so this is a like-for-like comparison rather than a remembered number.
    /// </summary>
    [Fact]
    public void HipFireCostsLessThanTheOmnidirectionalRuleItReplaced()
    {
        var oldRule = Select(TerrainView.Everywhere(Eye, HipTanHalfFov, ViewportHeight));
        var newRule = Select(View(Eye, Vector3.UnitZ, HipTanHalfFov));

        Assert.True(
            newRule.Count < oldRule.Count,
            $"hip selection is {newRule.Count} sections against the old rule's {oldRule.Count}");
    }

    /// <summary>
    /// Selection runs on view change, so it must not allocate a queue per call and must not carry
    /// state between calls. Same view twice, same answer.
    /// </summary>
    [Fact]
    public void ReselectingIsIdempotent()
    {
        var view = View(Eye, Vector3.UnitZ, HipTanHalfFov / 4f);
        var lod = new TerrainLod();

        var first = new HashSet<LodSection>();
        var second = new HashSet<LodSection>();

        lod.CollectDesired(view, first);
        lod.CollectDesired(view, second);

        Assert.Equal(first.Count, second.Count);
        Assert.True(first.SetEquals(second));
    }

    /// <summary>
    /// A small turn changes no level the player can SEE.
    ///
    /// Not the same as changing no level at all, and the difference is the point of the margin.
    /// Widening the selection frustum does not remove the boolean edge where boxes flip in and out —
    /// it moves that edge outside the visible frustum, so the churn happens where nothing is drawn.
    /// The invariant worth defending is therefore about the VISIBLE frustum, which is what this
    /// tests: every chunk column inside the unwidened view is at the same level before and after a
    /// turn smaller than <c>ClientTerrain.ReselectDegrees</c>.
    /// </summary>
    [Fact]
    public void SmallTurnsChangeNothingInsideTheVisibleFrustum()
    {
        float tan = HipTanHalfFov / 4f;
        float nudge = 4f * MathF.PI / 180f;
        var turnedAxis = new Vector3(MathF.Sin(nudge), 0f, MathF.Cos(nudge));

        var straight = Nodes(Select(View(Eye, Vector3.UnitZ, tan)));
        var turned = Nodes(Select(View(Eye, turnedAxis, tan)));

        // The unwidened frustum: what is actually on screen before the turn.
        var visible = new TerrainView(
            Eye, Vector3.UnitZ, Vector3.UnitY, tan, Aspect, ViewportHeight, marginDegrees: 0f);

        for (int cx = WorldGen.MeshableMin.x; cx <= WorldGen.MeshableMax.x; cx++)
            for (int cz = WorldGen.MeshableMin.z; cz <= WorldGen.MeshableMax.z; cz++)
            {
                var min = new Vector3(
                    cx * ChunkConstants.ChunkWidth, ChunkConstants.WorldMinY, cz * ChunkConstants.ChunkWidth);
                var max = min + new Vector3(
                    ChunkConstants.ChunkWidth,
                    ChunkConstants.WorldMaxY - ChunkConstants.WorldMinY,
                    ChunkConstants.ChunkWidth);

                if (!visible.Intersects(min, max)) continue;

                Assert.True(
                    LevelAt(straight, cx, cz) == LevelAt(turned, cx, cz),
                    $"chunk ({cx}, {cz}) is visible and changed level on a {nudge * 180f / MathF.PI:F0} "
                  + $"degree turn: {LevelAt(straight, cx, cz)} -> {LevelAt(turned, cx, cz)}");
            }
    }
}
