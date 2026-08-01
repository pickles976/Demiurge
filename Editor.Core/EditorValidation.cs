namespace Demiurge.Editor;

public readonly record struct EditorValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public static class EditorValidation
{
    public static EditorValidationResult Validate(EditorDocument document)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (document.SchemaVersion != EditorDocument.CurrentSchemaVersion)
            errors.Add($"Unsupported source schema {document.SchemaVersion}");
        if (document.MapId == Guid.Empty) errors.Add("Map ID cannot be empty");
        if (!MapPathResolver.IsValidName(document.Name)) errors.Add("Map name is not a safe slug");
        if (document.BaseTerrain.GeneratorId != "demiurge:terrain-v1")
            errors.Add($"Unsupported generator {document.BaseTerrain.GeneratorId}");
        if (document.BaseTerrain.MinChunkX != WorldGen.Min.x
            || document.BaseTerrain.MinChunkZ != WorldGen.Min.z
            || document.BaseTerrain.MaxChunkX != WorldGen.Max.x
            || document.BaseTerrain.MaxChunkZ != WorldGen.Max.z)
            errors.Add("Source map bounds do not match runtime version 1");

        var ids = new HashSet<Guid>();
        foreach (var stroke in document.TerrainStrokes)
        {
            if (!ids.Add(stroke.Id)) errors.Add($"Duplicate editor ID {stroke.Id}");
            if (stroke.Dabs.Count == 0) errors.Add($"Terrain stroke {stroke.Id} has no dabs");
            if (!IsFinite(stroke.HalfExtent) || stroke.HalfExtent.X <= 0
                || stroke.HalfExtent.Y <= 0 || stroke.HalfExtent.Z <= 0)
                errors.Add($"Terrain stroke {stroke.Id} has invalid extent");
            if (!float.IsFinite(stroke.Strength) || stroke.Strength is <= 0 or > 1)
                errors.Add($"Terrain stroke {stroke.Id} has invalid strength");
            if (!BlockCatalog.TryResolve(stroke.MaterialId, out _))
                errors.Add($"Terrain stroke {stroke.Id} has unknown material {stroke.MaterialId}");
            foreach (var dab in stroke.Dabs)
            {
                if (!IsFinite(dab))
                {
                    errors.Add($"Terrain stroke {stroke.Id} has a non-finite dab");
                    continue;
                }
                if (IsFinite(stroke.HalfExtent) && !IsBrushInBounds(dab, stroke.HalfExtent))
                    errors.Add($"Terrain stroke {stroke.Id} extends outside editable bounds");
            }
        }

        var occupied = new HashSet<Int3>();
        foreach (var block in document.Blocks)
        {
            if (!ids.Add(block.Id)) errors.Add($"Duplicate editor ID {block.Id}");
            if (!occupied.Add(block.Cell)) errors.Add($"Multiple blocks occupy {block.Cell}");
            if (!IsCellInBounds(block.Cell)) errors.Add($"Block {block.Id} is outside editable bounds");
            if (!BlockCatalog.TryResolve(block.BlockId, out _))
                errors.Add($"Block {block.Id} has unknown material {block.BlockId}");
        }

        int spawns = 0;
        foreach (var placement in document.Placements)
        {
            if (!ids.Add(placement.Id)) errors.Add($"Duplicate editor ID {placement.Id}");
            if (!IsCellInBounds(placement.Cell)) errors.Add($"Placement {placement.Id} is outside editable bounds");
            if (!float.IsFinite(placement.Yaw)) errors.Add($"Placement {placement.Id} has invalid yaw");
            switch (placement.Kind)
            {
                case EditorPlacementKind.Pickup:
                    if (!ItemCatalog.TryResolve(placement.ArchetypeId, out _))
                        errors.Add($"Placement {placement.Id} has unknown item {placement.ArchetypeId}");
                    break;
                case EditorPlacementKind.Mob:
                    if (placement.ArchetypeId != "demiurge:mob")
                        errors.Add($"Placement {placement.Id} has unknown mob {placement.ArchetypeId}");
                    string weaponId = placement.WeaponId ?? ItemCatalog.Id(ItemConfig.DefaultPrimaryWeapon);
                    if (!ItemCatalog.TryResolve(weaponId, out var weapon)
                        || WeaponConfig.Get(weapon) is null)
                        errors.Add($"Placement {placement.Id} has unknown weapon {weaponId}");
                    if (placement.Team <= 0)
                        errors.Add($"Placement {placement.Id} must use a positive team");
                    break;
                case EditorPlacementKind.PlayerSpawn:
                    spawns++;
                    if (!placement.ArchetypeId.StartsWith("demiurge:spawn/", StringComparison.Ordinal))
                        errors.Add($"Placement {placement.Id} has invalid spawn ID {placement.ArchetypeId}");
                    if (placement.Team <= 0)
                        errors.Add($"Placement {placement.Id} must use a positive team");
                    break;
                case EditorPlacementKind.Flag:
                    if (placement.ArchetypeId != "demiurge:flag")
                        errors.Add($"Placement {placement.Id} has unknown flag {placement.ArchetypeId}");
                    if (placement.Team != 0)
                        errors.Add($"Flag placement {placement.Id} must start neutral");
                    break;
                default:
                    errors.Add($"Placement {placement.Id} has unknown kind {placement.Kind}");
                    break;
            }
        }

        if (spawns == 0) errors.Add("Source map requires at least one player spawn");
        if (document.TerrainStrokes.Count > 10_000)
            warnings.Add("Map has more than 10,000 terrain strokes; consider compaction");
        if (!document.Placements.Any(p => p.Kind == EditorPlacementKind.Pickup))
            warnings.Add("Map has no pickups");
        if (!document.Placements.Any(p => p.Kind == EditorPlacementKind.Mob))
            warnings.Add("Map has no mobs");

        return new EditorValidationResult(errors, warnings);
    }

    public static EditorValidationResult Validate(EditorDocument document, ChunkMap terrain)
    {
        var source = Validate(document);
        var errors = source.Errors.ToList();
        var warnings = source.Warnings.ToList();

        foreach (var placement in document.Placements)
        {
            bool hasSupport = EditorPlacementPosition.TryResolve(
                terrain, placement.Cell, out _);
            var position = EditorPlacementPosition.Resolve(terrain, placement);
            if (placement.Kind == EditorPlacementKind.PlayerSpawn)
            {
                if (!hasSupport
                    || !TerrainCollision.TryDeepestContact(
                        terrain, PlayerMovement.Body, position, out var contact)
                    || contact.Distance < PlayerMovement.Body.Radius)
                    errors.Add($"Player spawn {placement.Id} intersects terrain");
                continue;
            }

            if (!hasSupport)
                warnings.Add($"Placement {placement.Id} has no nearby support");
        }

        foreach (var group in document.Placements.GroupBy(placement => placement.Cell))
            if (group.Skip(1).Any())
                warnings.Add($"Multiple placements share anchor {group.Key}");

        return new EditorValidationResult(errors, warnings);
    }

    private static bool IsFinite(Float3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static bool IsCellInBounds(Int3 cell)
    {
        int minX = WorldGen.MeshableMin.x * ChunkConstants.ChunkWidth;
        int minZ = WorldGen.MeshableMin.z * ChunkConstants.ChunkWidth;
        int maxX = (WorldGen.MeshableMax.x + 1) * ChunkConstants.ChunkWidth - 1;
        int maxZ = (WorldGen.MeshableMax.z + 1) * ChunkConstants.ChunkWidth - 1;
        return cell.X >= minX && cell.X <= maxX
            && cell.Z >= minZ && cell.Z <= maxZ
            && cell.Y >= ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness
            && cell.Y < ChunkConstants.WorldMaxY;
    }

    public static bool IsBrushInBounds(Float3 center, Float3 halfExtent)
    {
        float minX = WorldGen.MeshableMin.x * ChunkConstants.ChunkWidth;
        float minZ = WorldGen.MeshableMin.z * ChunkConstants.ChunkWidth;
        float maxX = (WorldGen.MeshableMax.x + 1) * ChunkConstants.ChunkWidth;
        float maxZ = (WorldGen.MeshableMax.z + 1) * ChunkConstants.ChunkWidth;
        return center.X - halfExtent.X >= minX
            && center.X + halfExtent.X < maxX
            && center.Z - halfExtent.Z >= minZ
            && center.Z + halfExtent.Z < maxZ
            && center.Y - halfExtent.Y >= ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness
            && center.Y + halfExtent.Y < ChunkConstants.WorldMaxY;
    }
}
