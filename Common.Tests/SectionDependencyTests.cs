namespace Demiurge.Tests;

/// <summary>
/// Which sections an edit invalidates. Pure index geometry, and the number that decides whether
/// sections were worth having: get it too wide and a dig re-meshes the world, too narrow and you
/// get seams that only appear after digging.
/// </summary>
public class SectionDependencyTests
{
    static HashSet<SectionIndex> Dependents(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        var into = new HashSet<SectionIndex>();
        ChunkMesher.CollectDependentSections(minX, minY, minZ, maxX, maxY, maxZ, into);
        return into;
    }

    /// <summary>
    /// A shovel-sized edit in the middle of a chunk must not reach the neighbours at all. This is the
    /// whole point of the exercise — the old whole-chunk marking hit a 3x3 of 128-tall columns.
    /// </summary>
    [Fact]
    public void SmallEditInTheMiddleOfAChunkStaysLocal()
    {
        // A radius-1.5 dig at (8, 12, 8) plus TerrainEdits' write margin.
        var sections = Dependents(4, 8, 4, 12, 16, 12);

        Assert.All(sections, s => Assert.Equal(new ChunkIndex { x = 0, z = 0 }, s.Chunk));
        Assert.Equal([0, 1], sections.Select(s => s.y).Order());
    }

    /// <summary>
    /// An edit against a chunk's -X face has to invalidate the neighbour, whose apron reads into it.
    /// Missing this is the classic silent seam.
    /// </summary>
    [Fact]
    public void EditOnAChunkBorderInvalidatesTheNeighbour()
    {
        var chunks = Dependents(0, 20, 8, 1, 24, 9).Select(s => s.Chunk).Distinct().Order2D();

        Assert.Equal([(-1, 0), (0, 0)], chunks);
    }

    /// <summary>Vertically the same rule applies between sections of one column.</summary>
    [Fact]
    public void EditOnASectionBoundaryInvalidatesBothSections()
    {
        // y = 16 is section 1's first plane, so section 0's apron reads it.
        var sections = Dependents(8, 16, 8, 8, 16, 8);

        Assert.All(sections, s => Assert.Equal(new ChunkIndex { x = 0, z = 0 }, s.Chunk));
        Assert.Equal([0, 1], sections.Select(s => s.y).Order());
    }

    /// <summary>Nothing outside the world's vertical extent, and nothing below section 0.</summary>
    [Fact]
    public void SectionsStayInsideTheWorld()
    {
        var atFloor = Dependents(8, ChunkConstants.WorldMinY, 8, 8, ChunkConstants.WorldMinY, 8);
        var atCeiling = Dependents(8, ChunkConstants.WorldMaxY - 1, 8, 8, ChunkConstants.WorldMaxY - 1, 8);

        Assert.All(atFloor, s => Assert.InRange(s.y, 0, ChunkConstants.SectionsPerChunk - 1));
        Assert.All(atCeiling, s => Assert.InRange(s.y, 0, ChunkConstants.SectionsPerChunk - 1));
    }
}

static class OrderExtensions
{
    /// <summary>Deterministic order for comparing chunk sets.</summary>
    public static IEnumerable<(int x, int z)> Order2D(this IEnumerable<ChunkIndex> source)
        => source.Select(c => (c.x, c.z)).OrderBy(c => c.x).ThenBy(c => c.z);
}
