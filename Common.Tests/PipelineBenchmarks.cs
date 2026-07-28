using System.Diagnostics;
using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// Measurements, not assertions. Every step between "the server has a seed" and "the player sees a
/// triangle", timed, so the bottleneck is a number instead of an opinion.
///
/// Run with output visible:
///     dotnet test --filter PipelineBenchmarks --logger "console;verbosity=detailed"
///
/// These generate the whole world, so they cost a few seconds. Skip them with:
///     dotnet test --filter "Category!=Benchmark"
///
/// The one step NOT here is the GPU upload in ChunkMeshFactory.ToEntity, which needs a GraphicsDevice
/// and so cannot run headlessly. <see cref="ClientMeshBacklog"/> parameterises it instead.
/// </summary>
[Trait("Category", "Benchmark")]
public class PipelineBenchmarks(ITestOutputHelper output)
{

    /// <summary>Where the player spawns, and so the chunk whose mesh they are waiting on.</summary>
    static readonly ChunkIndex PlayerChunk = new() { x = 0, z = 0 };

    // Generated once: several benchmarks need the whole world and it costs about a second.
    static readonly Lazy<ChunkMap> World = new(() =>
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    });

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // ---- 1. Server: turning a seed into voxels ----

    [Fact]
    public void ServerGeneration()
    {
        var sw = Stopwatch.StartNew();
        var map = new ChunkMap();
        WorldGen.Generate(map);
        sw.Stop();

        int chunks = CountChunks();

        output.WriteLine($"WorldGen.Generate      {sw.ElapsedMilliseconds,7} ms total");
        output.WriteLine($"  chunks               {chunks,7}");
        output.WriteLine($"  per chunk            {sw.Elapsed.TotalMilliseconds / chunks,7:F3} ms");
        output.WriteLine($"  ONE TIME, at server startup, before any client connects.");
    }

    // ---- 2. Server: encoding for the wire ----

    [Fact]
    public void WireEncoding()
    {
        var map = World.Value;
        var buffer = new byte[ChunkTransport.MaxPayloadBytes];

        long ticks = 0;
        int bytes = 0;

        // One frame per whole column, which is what the TCP writer sends.
        foreach (var index in AllChunks())
        {
            var chunk = map.Get(index)!;

            long t0 = Stopwatch.GetTimestamp();
            var (_, size) = ChunkWire.Encode(chunk, 0, buffer);
            ticks += Stopwatch.GetTimestamp() - t0;

            bytes += size;
        }

        int chunks = CountChunks();

        output.WriteLine($"ChunkWire.Encode       {Ms(ticks),7:F0} ms total (whole world, once per client)");
        output.WriteLine($"  bytes                {bytes,7} ({bytes / 1024.0 / 1024.0:F2} MB)");
        output.WriteLine($"  per chunk            {bytes / (double)chunks,7:F0} bytes");
        output.WriteLine($"  encode per chunk     {Ms(ticks) / chunks,7:F3} ms  <-- on the TCP writer thread");
        output.WriteLine($"  ceiling from encode  {chunks / (Ms(ticks) / 1000.0),7:F0} chunks/s per writer thread");
    }

    // ---- 3. The wire: when does each chunk actually ARRIVE? ----

    /// <summary>
    /// The SEND ORDER, which is what is left of the old delivery problem.
    ///
    /// Terrain used to be paced by a messages-per-tick constant on Riptide, which put a hard 258 KB/s
    /// ceiling on it and meant the player's chunk did not land for 8.5 s. That constant is gone — TCP's
    /// send window paces the transfer instead — so the wire is no longer the bound. What remains is that
    /// QueueWorldFor still walks x-major from the far corner, so the chunk the player is standing on is
    /// sent from the middle of the queue and everything before it goes first.
    ///
    /// Reported as bytes-before-it rather than seconds, because seconds now depend on real socket
    /// throughput rather than on a constant we chose.
    /// </summary>
    [Fact]
    public void SendOrder()
    {
        var map = World.Value;
        var buffer = new byte[ChunkTransport.MaxPayloadBytes];
        var order = new List<ChunkIndex>(AllChunks());

        int Bytes(ChunkIndex index) => ChunkWire.Encode(map.Get(index)!, 0, buffer).byteCount;

        int total = 0;
        foreach (var index in order) total += Bytes(index);

        // Everything the current order sends before the player's own chunk, and before the last chunk of
        // the 3x3 neighbourhood its mesh actually needs.
        int playerIndex = order.IndexOf(PlayerChunk);

        int gateIndex = playerIndex;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int at = order.IndexOf(new ChunkIndex { x = PlayerChunk.x + dx, z = PlayerChunk.z + dz });
                if (at > gateIndex) gateIndex = at;
            }

        int beforeGate = 0;
        for (int i = 0; i <= gateIndex; i++) beforeGate += Bytes(order[i]);

        // What nearest-first would cost instead: the player's chunk plus its eight neighbours.
        int nearestFirst = 0;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                nearestFirst += Bytes(new ChunkIndex { x = PlayerChunk.x + dx, z = PlayerChunk.z + dz });

        output.WriteLine($"World: {order.Count} chunks, {total / 1024.0 / 1024.0:F2} MB encoded");
        output.WriteLine($"  no rate constant any more — TCP's send window paces this (see ChunkTransport)");
        output.WriteLine("");
        output.WriteLine($"  PLAYER CHUNK {PlayerChunk}");
        output.WriteLine($"    send order         {playerIndex,7} of {order.Count}   (x-major from the far corner)");
        output.WriteLine($"    3x3 gate complete  {gateIndex,7} of {order.Count}   (the mesher's real dependency)");
        output.WriteLine($"    bytes sent first   {beforeGate / 1024.0 / 1024.0,7:F2} MB  = {beforeGate * 100.0 / total:F0}% of the world");
        output.WriteLine("");
        output.WriteLine($"  NEAREST-FIRST would need {nearestFirst / 1024.0,7:F0} KB "
                       + $"({nearestFirst * 100.0 / total:F2}% of the world) before the player sees ground.");
        output.WriteLine($"  Speedup to first mesh: {beforeGate / (double)nearestFirst:F0}x");
    }

    // ---- 4. Client: decoding ----

    [Fact]
    public void WireDecoding()
    {
        var map = World.Value;
        var buffer = new byte[ChunkTransport.MaxPayloadBytes];
        var target = new TerrainChunk(PlayerChunk);

        // Capture one chunk's payloads, then replay them enough times to measure.
        var payloads = new List<(int slabY, int count, byte[] bytes)>();
        var source = map.Get(PlayerChunk)!;

        int cursor = 0;
        while (cursor < ChunkConstants.ChunkHeight)
        {
            var (count, size) = ChunkWire.Encode(source, cursor, buffer);
            payloads.Add((cursor, count, buffer[..size].ToArray()));
            cursor += count;
        }

        const int Repeats = 500;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Repeats; i++)
            foreach (var (slabY, count, bytes) in payloads)
                ChunkWire.Decode(target, slabY, count, bytes);
        sw.Stop();

        int chunks = CountChunks();
        double perChunk = sw.Elapsed.TotalMilliseconds / Repeats;

        output.WriteLine($"ChunkWire.Decode       {perChunk,7:F3} ms per chunk (MAIN THREAD, in Drain)");
        output.WriteLine($"  whole world          {perChunk * chunks,7:F0} ms total, spread across the whole stream");
        output.WriteLine($"  verdict              negligible unless this is a large fraction of a frame");
    }

    // ---- 5. Client: meshing, per phase ----

    [Fact]
    public void MeshPhases()
    {
        var map = World.Value;
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        var sections = new List<SectionIndex>();
        for (int x = WorldGen.MeshableMin.x; x <= WorldGen.MeshableMax.x; x += 4)
            for (int z = WorldGen.MeshableMin.z; z <= WorldGen.MeshableMax.z; z += 4)
                for (int sy = 0; sy < ChunkConstants.SectionsPerChunk; sy++)
                    sections.Add(new SectionIndex(x, sy, z));

        foreach (var s in sections.Take(40))                       // warm up
            if (ChunkMesher.TryFillScratch(map, s, scratch))
                ChunkMesher.SplitCreases(ChunkMesher.GenerateMesh(scratch));

        long fill = 0, contour = 0, crease = 0;
        int withGeometry = 0, triangles = 0;

        foreach (var section in sections)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool ok = ChunkMesher.TryFillScratch(map, section, scratch);
            long t1 = Stopwatch.GetTimestamp();
            fill += t1 - t0;
            if (!ok) continue;

            var mesh = ChunkMesher.GenerateMesh(scratch);
            long t2 = Stopwatch.GetTimestamp();
            contour += t2 - t1;
            if (mesh.Indices.Length == 0) continue;

            mesh = ChunkMesher.SplitCreases(mesh);
            crease += Stopwatch.GetTimestamp() - t2;
            withGeometry++;
            triangles += mesh.Indices.Length / 3;
        }

        double total = Ms(fill) + Ms(contour) + Ms(crease);
        int allSections = TotalMeshableSections();

        output.WriteLine($"Sampled {sections.Count} sections, {withGeometry} with geometry "
                       + $"({withGeometry * 100.0 / sections.Count:F0}%), {triangles} triangles");
        output.WriteLine($"  TryFillScratch       {Ms(fill),7:F1} ms  {Ms(fill) / total * 100,4:F0}%   {Ms(fill) / sections.Count:F3} ms/section");
        output.WriteLine($"  dual contouring      {Ms(contour),7:F1} ms  {Ms(contour) / total * 100,4:F0}%");
        output.WriteLine($"  SplitCreases         {Ms(crease),7:F1} ms  {Ms(crease) / total * 100,4:F0}%");
        output.WriteLine($"  per section          {total / sections.Count,7:F3} ms  (ALL of this is off-thread now)");
        output.WriteLine("");
        output.WriteLine($"  whole world          {allSections} sections, {total / sections.Count * allSections / 1000.0:F1} s of work");
        output.WriteLine($"  across 6 workers     {total / sections.Count * allSections / 1000.0 / 6:F1} s wall clock");
    }

    // ---- 6. Client: what the main thread still has to do ----

    /// <summary>
    /// The upload cannot be measured headlessly, so this reports what the main thread costs WOULD be at
    /// several per-section upload costs. Compare against the delivery schedule: if streaming is slower
    /// than uploading, making the pipeline faster changes nothing.
    /// </summary>
    [Fact]
    public void ClientMeshBacklog()
    {
        var map = World.Value;
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        // How many sections hold geometry, sampled and scaled.
        int sampled = 0, withGeometry = 0;
        for (int x = WorldGen.MeshableMin.x; x <= WorldGen.MeshableMax.x; x += 5)
            for (int z = WorldGen.MeshableMin.z; z <= WorldGen.MeshableMax.z; z += 5)
                for (int sy = 0; sy < ChunkConstants.SectionsPerChunk; sy++)
                {
                    sampled++;
                    var section = new SectionIndex(x, sy, z);
                    if (!ChunkMesher.TryFillScratch(map, section, scratch)) continue;
                    if (ChunkMesher.GenerateMesh(scratch).Indices.Length > 0) withGeometry++;
                }

        int allSections = TotalMeshableSections();
        int uploads = (int)(allSections * (withGeometry / (double)sampled));

        output.WriteLine($"Sections needing a GPU upload: ~{uploads} of {allSections}");
        output.WriteLine($"Main-thread upload budget: 12 ms/frame, batched (ClientTerrain)");
        output.WriteLine("");
        output.WriteLine("  ms/upload   uploads/frame   frames   seconds @60fps");

        foreach (double cost in new[] { 0.1, 0.25, 0.5, 1.0, 2.0 })
        {
            double perFrame = Math.Max(1, 4.0 / cost);
            double frames = uploads / perFrame;
            output.WriteLine($"  {cost,9:F2}   {perFrame,13:F0}   {frames,6:F0}   {frames / 60.0,14:F1}");
        }
    }

    // ---- 7. How much LOD actually removes ----

    /// <summary>
    /// Boxes that exist at each level with the player at the origin, and what that saves against drawing
    /// the whole world at full detail. Entity count is the draw-call count, which is what LOD is for.
    /// </summary>
    [Fact]
    public void LodBoxCount()
    {
        var desired = new HashSet<LodSection>();
        TerrainLod.CollectDesired(Vector3.Zero, desired);

        var perLevel = new int[LodSection.MaxLevel + 1];
        foreach (var box in desired) perLevel[box.Level]++;

        int flat = (WorldGen.MeshableMax.x - WorldGen.MeshableMin.x + 1)
                 * (WorldGen.MeshableMax.z - WorldGen.MeshableMin.z + 1)
                 * ChunkConstants.SectionsPerChunk;

        output.WriteLine($"World {WorldGen.Max.x - WorldGen.Min.x + 1} chunks across, player at origin");
        for (int level = 0; level <= LodSection.MaxLevel; level++)
            output.WriteLine($"  LOD {level}   {perLevel[level],6} boxes");

        output.WriteLine($"  total  {desired.Count,6} boxes against {flat} sections at full detail "
                       + $"= {flat / (double)desired.Count:F1}x fewer");
    }

    // ---- helpers ----

    static IEnumerable<ChunkIndex> AllChunks()
    {
        for (int x = WorldGen.Min.x; x <= WorldGen.Max.x; x++)
            for (int z = WorldGen.Min.z; z <= WorldGen.Max.z; z++)
                yield return new ChunkIndex { x = x, z = z };
    }

    static int CountChunks()
        => (WorldGen.Max.x - WorldGen.Min.x + 1) * (WorldGen.Max.z - WorldGen.Min.z + 1);

    static int TotalMeshableSections()
        => (WorldGen.MeshableMax.x - WorldGen.MeshableMin.x + 1)
         * (WorldGen.MeshableMax.z - WorldGen.MeshableMin.z + 1)
         * ChunkConstants.SectionsPerChunk;

    static double Seconds(int ticks) => ticks / (double)NetworkConfig.TickRate;
}
