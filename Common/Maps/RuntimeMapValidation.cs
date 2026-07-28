using System.Numerics;

namespace Demiurge;

public static class RuntimeMapValidation
{
    public static RuntimeMapValidationResult Validate(RuntimeMap map)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (map.MapId == Guid.Empty) errors.Add("Map ID cannot be empty");
        if (string.IsNullOrWhiteSpace(map.Name) || map.Name.Length > 128)
            errors.Add("Map name must be 1-128 characters");
        if (map.SourceHash.Length != RuntimeMapSerializer.HashBytes)
            errors.Add($"Source hash must be {RuntimeMapSerializer.HashBytes} bytes");

        int expectedChunks =
            (WorldGen.Max.x - WorldGen.Min.x + 1) *
            (WorldGen.Max.z - WorldGen.Min.z + 1);
        if (map.Terrain.Count != expectedChunks)
            errors.Add($"Map has {map.Terrain.Count} chunks; version 1 requires {expectedChunks}");

        for (int z = WorldGen.Min.z; z <= WorldGen.Max.z; z++)
            for (int x = WorldGen.Min.x; x <= WorldGen.Max.x; x++)
                if (!map.Terrain.Has(new ChunkIndex { x = x, z = z }))
                    errors.Add($"Missing chunk ({x}, {z})");

        foreach (var chunk in map.Terrain.Snapshot())
        {
            if (chunk.index.x < WorldGen.Min.x || chunk.index.x > WorldGen.Max.x
                || chunk.index.z < WorldGen.Min.z || chunk.index.z > WorldGen.Max.z)
                errors.Add($"Chunk {chunk.index} is outside version 1 bounds");

            for (int z = 0; z < ChunkConstants.ChunkWidth; z++)
                for (int x = 0; x < ChunkConstants.ChunkWidth; x++)
                {
                    int i = ChunkTransforms.LocalVoxelIndex(x, 0, z);
                    if (chunk[i].Distance >= 0f)
                        errors.Add($"Chunk {chunk.index} has non-solid bedrock at ({x}, 0, {z})");
                }
        }

        int spawnCount = 0;
        var occupiedAnchors = new HashSet<(int X, int Y, int Z)>();
        foreach (var placement in map.Placements)
        {
            if (!IsFinite(placement.Position) || !float.IsFinite(placement.Yaw))
            {
                errors.Add($"Placement {placement.Kind} has non-finite transform");
                continue;
            }

            var chunk = ChunkTransforms.ChunkAt(placement.Position);
            if (chunk.x < WorldGen.MeshableMin.x || chunk.x > WorldGen.MeshableMax.x
                || chunk.z < WorldGen.MeshableMin.z || chunk.z > WorldGen.MeshableMax.z)
                errors.Add($"Placement {placement.Kind} is outside meshable bounds");
            var anchor = (
                (int)MathF.Floor(placement.Position.X),
                (int)MathF.Floor(placement.Position.Y),
                (int)MathF.Floor(placement.Position.Z));
            if (!occupiedAnchors.Add(anchor))
                warnings.Add($"Multiple placements share anchor ({anchor.Item1}, {anchor.Item2}, {anchor.Item3})");

            switch (placement.Kind)
            {
                case RuntimePlacementKind.Pickup:
                    try { _ = ItemCatalog.Get(placement.Item); }
                    catch (ArgumentOutOfRangeException) { errors.Add($"Unknown pickup item {placement.Item}"); }
                    WarnIfUnsupported(map, placement, warnings);
                    break;
                case RuntimePlacementKind.PlayerSpawn:
                    spawnCount++;
                    if (string.IsNullOrWhiteSpace(placement.SpawnId) || placement.SpawnId.Length > 64)
                        errors.Add("Player spawn ID must be 1-64 characters");
                    if (!TerrainCollision.TryDeepestContact(
                            map.Terrain, PlayerMovement.Body, placement.Position, out var contact)
                        || contact.Distance < PlayerMovement.Body.Radius)
                        errors.Add($"Player spawn {placement.SpawnId} intersects terrain");
                    break;
                case RuntimePlacementKind.Mob:
                    WarnIfUnsupported(map, placement, warnings);
                    break;
                default:
                    errors.Add($"Unknown placement kind {(byte)placement.Kind}");
                    break;
            }
        }

        if (spawnCount == 0) errors.Add("Runtime map requires at least one player spawn");
        if (!map.Placements.Any(p => p.Kind == RuntimePlacementKind.Pickup))
            warnings.Add("Map has no pickup placements");
        if (!map.Placements.Any(p => p.Kind == RuntimePlacementKind.Mob))
            warnings.Add("Map has no mob placements");

        return new RuntimeMapValidationResult(errors, warnings);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void WarnIfUnsupported(
        RuntimeMap map,
        RuntimePlacement placement,
        List<string> warnings)
    {
        float surfaceY = SurfaceQuery.SurfacePosition(
            map.Terrain, placement.Position.X, placement.Position.Z).Y;
        if (MathF.Abs(surfaceY - placement.Position.Y) > 0.75f)
            warnings.Add(
                $"{placement.Kind} at ({placement.Position.X:0.##}, {placement.Position.Y:0.##}, {placement.Position.Z:0.##}) has no nearby support");
    }
}
