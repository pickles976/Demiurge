using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demiurge;

/// <summary>Immutable, fully validated gameplay registry produced by applying datapacks in priority
/// order. Canonical names survive saves; compact handles are used only at runtime and on the wire.</summary>
public sealed class DataPackRegistry
{
    private readonly Dictionary<ItemType, ItemDefinition> byHandle;
    private readonly Dictionary<string, ItemType> byName;
    private readonly Dictionary<string, BallisticsStats> ballistics;

    internal DataPackRegistry(
        IReadOnlyList<ItemDefinition> items,
        Dictionary<string, BallisticsStats> ballistics,
        Dictionary<string, ItemType> byName,
        ItemType defaultPlayerPrimary,
        ItemType defaultNpcPrimary,
        ItemType unidentifiedThreatWeapon,
        ItemType defaultAssaultWeapon,
        ItemType defaultMarksmanWeapon,
        string gameplayHash,
        IReadOnlyList<string> packs)
    {
        Items = items;
        this.ballistics = ballistics;
        this.byName = byName;
        byHandle = items.ToDictionary(item => item.Type);
        DefaultPlayerPrimary = defaultPlayerPrimary;
        DefaultNpcPrimary = defaultNpcPrimary;
        UnidentifiedThreatWeapon = unidentifiedThreatWeapon;
        DefaultAssaultWeapon = defaultAssaultWeapon;
        DefaultMarksmanWeapon = defaultMarksmanWeapon;
        GameplayHash = gameplayHash;
        Packs = packs;
    }

    public IReadOnlyList<ItemDefinition> Items { get; }
    public IReadOnlyList<string> Packs { get; }
    public ItemType DefaultPlayerPrimary { get; }
    public ItemType DefaultNpcPrimary { get; }
    public ItemType UnidentifiedThreatWeapon { get; }
    public ItemType DefaultAssaultWeapon { get; }
    public ItemType DefaultMarksmanWeapon { get; }
    public string GameplayHash { get; }

    public bool TryResolve(string name, out ItemType type)
        => byName.TryGetValue(name, out type);

    public ItemDefinition? GetItem(ItemType type)
        => byHandle.GetValueOrDefault(type);

    public ItemDefinition RequireItem(ItemType type)
        => GetItem(type)
           ?? throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown item handle");

    public BallisticsStats RequireBallistics(string id)
        => ballistics.TryGetValue(id, out var stats)
            ? stats
            : throw new KeyNotFoundException($"Unknown ballistics definition {id}");
}

public static class DataPackLoader
{
    private const int SchemaVersion = 1;
    private const ushort DynamicHandleStart = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static DataPackRegistry LoadDefault()
    {
        var roots = new List<string> { FindBuiltInRoot() };
        string? extra = Environment.GetEnvironmentVariable("DEMIURGE_DATAPACK_ROOTS");
        if (!string.IsNullOrWhiteSpace(extra))
            roots.AddRange(extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return LoadFromRoots(roots);
    }

    public static DataPackRegistry LoadFromRoots(IEnumerable<string> roots)
    {
        var errors = new List<string>();
        var packs = DiscoverPacks(roots, errors);
        if (packs.Count == 0)
            errors.Add("No datapacks were found");

        var ballisticsSources = new Dictionary<string, BallisticsSource>(StringComparer.Ordinal);
        var itemSources = new Dictionary<string, ItemSource>(StringComparer.Ordinal);
        DefaultsSource? defaults = null;
        var hashInput = new StringBuilder();

        foreach (var pack in packs)
        {
            foreach (string path in Files(pack.Path, "ballistics"))
            {
                var source = Read<BallisticsSource>(path, errors);
                if (source is null) continue;
                if (!ValidateId(source.Id, path, errors)) continue;
                ballisticsSources[source.Id] = source;
                AppendHash(hashInput, pack.Id, path);
            }

            foreach (string path in Files(pack.Path, "items"))
            {
                var source = Read<ItemSource>(path, errors);
                if (source is null) continue;
                if (!ValidateId(source.Id, path, errors)) continue;
                if (itemSources.TryGetValue(source.Id, out var previous)
                    && source.NetworkId is null)
                    source.NetworkId = previous.NetworkId;
                itemSources[source.Id] = source;
                AppendHash(hashInput, pack.Id, path);
            }

            IEnumerable<string> defaultFiles = Directory.Exists(Path.Combine(pack.Path, "data"))
                ? Directory.GetFiles(Path.Combine(pack.Path, "data"), "defaults.json", SearchOption.AllDirectories).Order()
                : Array.Empty<string>();
            foreach (string path in defaultFiles)
            {
                defaults = Read<DefaultsSource>(path, errors) ?? defaults;
                AppendHash(hashInput, pack.Id, path);
            }
        }

        var ballistics = BuildBallistics(ballisticsSources, errors);
        var items = BuildItems(itemSources, ballistics, errors);
        var names = BuildNames(items, errors);

        if (defaults is null)
            errors.Add("No defaults.json was provided by the active datapacks");
        ItemType player = ResolveDefault(defaults?.PlayerPrimary, "playerPrimary", names, errors);
        ItemType npc = ResolveDefault(defaults?.NpcPrimary, "npcPrimary", names, errors);
        ItemType threat = ResolveDefault(defaults?.UnidentifiedThreatWeapon, "unidentifiedThreatWeapon", names, errors);
        ItemType assault = ResolveDefault(defaults?.AssaultWeapon, "assaultWeapon", names, errors);
        ItemType marksman = ResolveDefault(defaults?.MarksmanWeapon, "marksmanWeapon", names, errors);

        if (errors.Count > 0)
            throw new InvalidDataException("Datapack validation failed:\n- " + string.Join("\n- ", errors));

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())));
        return new DataPackRegistry(
            items,
            ballistics,
            names,
            player,
            npc,
            threat,
            assault,
            marksman,
            hash,
            packs.Select(pack => pack.Id).ToArray());
    }

    private static List<Pack> DiscoverPacks(IEnumerable<string> roots, List<string> errors)
    {
        var result = new List<Pack>();
        foreach (string root in roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(root))
            {
                errors.Add($"Datapack root does not exist: {root}");
                continue;
            }

            foreach (string directory in Directory.GetDirectories(root).Order())
            {
                string manifestPath = Path.Combine(directory, "pack.json");
                if (!File.Exists(manifestPath)) continue;
                var manifest = Read<PackManifest>(manifestPath, errors);
                if (manifest is null) continue;
                if (manifest.SchemaVersion != SchemaVersion)
                    errors.Add($"{manifestPath}: unsupported schemaVersion {manifest.SchemaVersion}");
                if (!ValidateId(manifest.Id, manifestPath, errors)) continue;
                result.Add(new Pack(manifest.Id, manifest.Priority, directory));
            }
        }

        return result.OrderBy(pack => pack.Priority).ThenBy(pack => pack.Id, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, BallisticsStats> BuildBallistics(
        Dictionary<string, BallisticsSource> sources,
        List<string> errors)
    {
        var result = new Dictionary<string, BallisticsStats>(StringComparer.Ordinal);
        foreach (var (id, source) in sources.OrderBy(pair => pair.Key))
        {
            if (source.SchemaVersion != SchemaVersion)
                errors.Add($"Ballistics {id} has unsupported schemaVersion {source.SchemaVersion}");
            float[] numbers =
            [
                source.ProjectileSpeed, source.BenchMoa, source.RecoilPerShotMoa,
                source.RecoilDecayMoaPerSecond, source.RecoilCapMoa, source.SightingMoa,
            ];
            if (numbers.Any(value => !float.IsFinite(value) || value < 0f))
            {
                errors.Add($"Ballistics {id} contains a negative or non-finite number");
                continue;
            }
            result[id] = new BallisticsStats(
                source.ProjectileSpeed,
                source.BenchMoa,
                source.RecoilPerShotMoa,
                source.RecoilDecayMoaPerSecond,
                source.RecoilCapMoa,
                source.SightingMoa);
        }
        return result;
    }

    private static List<ItemDefinition> BuildItems(
        Dictionary<string, ItemSource> sources,
        Dictionary<string, BallisticsStats> ballistics,
        List<string> errors)
    {
        var usedHandles = new HashSet<ushort> { 0, 1, 2, 3 };
        var handles = new Dictionary<string, ItemType>(StringComparer.Ordinal);
        foreach (var (id, source) in sources.OrderBy(pair => pair.Key))
        {
            if (source.NetworkId is not { } numeric) continue;
            if (numeric < 4 || !usedHandles.Add(numeric))
            {
                errors.Add($"Item {id} uses reserved or duplicate networkId {numeric}");
                continue;
            }
            handles[id] = (ItemType)numeric;
        }

        ushort next = DynamicHandleStart;
        foreach (string id in sources.Keys.Order(StringComparer.Ordinal))
        {
            if (handles.ContainsKey(id)) continue;
            while (usedHandles.Contains(next) && next < ushort.MaxValue) next++;
            if (next == ushort.MaxValue)
            {
                errors.Add("Datapacks define too many items for the 16-bit runtime registry");
                break;
            }
            handles[id] = (ItemType)next;
            usedHandles.Add(next++);
        }

        var result = new List<ItemDefinition>();
        foreach (var (id, source) in sources.OrderBy(pair => pair.Key))
        {
            if (source.SchemaVersion != SchemaVersion)
                errors.Add($"Item {id} has unsupported schemaVersion {source.SchemaVersion}");
            if (string.IsNullOrWhiteSpace(source.DisplayName))
                errors.Add($"Item {id} has no displayName");
            if (!Enum.IsDefined(source.Category))
                errors.Add($"Item {id} has unknown category {(byte)source.Category}");
            if (!Enum.IsDefined(source.Slot))
                errors.Add($"Item {id} has unknown slot {(byte)source.Slot}");
            if (!Enum.IsDefined(source.Behavior))
                errors.Add($"Item {id} has unknown behavior {(int)source.Behavior}");
            if (source.Hotbar is { } hotbar && !HotbarConfig.IsValid(hotbar))
                errors.Add($"Item {id} has unknown hotbar slot {(byte)hotbar}");
            if (!float.IsFinite(source.MovementSpeedScale) || source.MovementSpeedScale is <= 0f or > 1f)
                errors.Add($"Item {id} movementSpeedScale must be in (0, 1]");
            if (source.Category == ItemCategory.Carryable && source.MovementSpeedScale >= 1f)
                errors.Add($"Carryable item {id} must reduce movement speed");

            WeaponStats? weapon = null;
            if (source.Weapon is { } w)
            {
                if (w.MagazineCapacity <= 0 || w.RoundsPerMinute <= 0f || w.ReloadSeconds < 0f
                    || !float.IsFinite(w.RoundsPerMinute) || !float.IsFinite(w.ReloadSeconds))
                    errors.Add($"Weapon {id} has invalid magazine, cadence, or reload values");
                if (!Enum.IsDefined(w.FireMode))
                    errors.Add($"Weapon {id} has unknown fireMode {(byte)w.FireMode}");
                if (string.IsNullOrWhiteSpace(w.Ballistics) || !ballistics.ContainsKey(w.Ballistics))
                    errors.Add($"Weapon {id} references unknown ballistics {w.Ballistics}");
                weapon = new WeaponStats(
                    w.MagazineCapacity,
                    60f * NetworkConfig.TickRate / w.RoundsPerMinute,
                    (int)MathF.Round(w.ReloadSeconds * NetworkConfig.TickRate, MidpointRounding.AwayFromZero),
                    w.Damage,
                    w.Ballistics ?? string.Empty,
                    w.FireMode);
            }

            ArmorStats? armor = source.Armor is { } a ? new ArmorStats(a.Max) : null;
            if (armor is { Max: <= 0f }) errors.Add($"Armor {id} max must be positive");

            var presentation = source.Presentation;
            if (presentation is null || string.IsNullOrWhiteSpace(presentation.Model))
            {
                errors.Add($"Item {id} has no presentation model");
                presentation = new PresentationSource { Model = "assets/models/dummy.gltf" };
            }
            if (!IsHexColor(presentation.TracerColor))
                errors.Add($"Item {id} tracerColor must be #RRGGBB or #RRGGBBAA");
            float[] presentationNumbers =
            [
                presentation.WorldScale,
                presentation.AimMagnification,
                presentation.AimSpeedScale,
                presentation.ReloadVolume,
                presentation.CameraTrauma,
                presentation.AdsRecoilScale,
                presentation.VisualRecoil?.Back ?? 0f,
                presentation.VisualRecoil?.Lift ?? 0f,
                presentation.VisualRecoil?.PitchDegrees ?? 0f,
                presentation.Bolt?.DelaySeconds ?? 0f,
                presentation.Bolt?.TravelSeconds ?? 0f,
                presentation.Bolt?.HoldSeconds ?? 0f,
                presentation.Bolt?.ReturnSeconds ?? 0f,
            ];
            if (presentationNumbers.Any(value => !float.IsFinite(value) || value < 0f)
                || presentation.WorldScale == 0f
                || presentation.AimMagnification == 0f
                || presentation.AimSpeedScale == 0f)
                errors.Add($"Item {id} presentation contains an invalid scale, timing, or effect value");
            if (presentation.ShotSounds?.Any(string.IsNullOrWhiteSpace) == true)
                errors.Add($"Item {id} contains an empty shot sound path");

            result.Add(new ItemDefinition(
                handles[id],
                id,
                source.DisplayName,
                source.Aliases ?? [],
                new ItemStats(source.Category, source.Slot, source.MovementSpeedScale),
                source.Behavior,
                source.Hotbar,
                weapon,
                armor,
                new ItemPresentationDefinition(
                    presentation.Model,
                    presentation.WorldScale,
                    presentation.AimMagnification,
                    presentation.AimSpeedScale,
                    presentation.ShotSounds ?? [],
                    presentation.TracerColor,
                    presentation.ReloadSound,
                    presentation.DistantReportSound,
                    presentation.ReloadVolume,
                    presentation.CameraTrauma,
                    new VisualRecoilDefinition(
                        presentation.VisualRecoil?.Back ?? 0.05f,
                        presentation.VisualRecoil?.Lift ?? 0.01f,
                        presentation.VisualRecoil?.PitchDegrees ?? 2.5f),
                    presentation.AdsRecoilScale,
                    presentation.Bolt is { } bolt
                        ? new BoltPresentationDefinition(
                            bolt.DelaySeconds, bolt.TravelSeconds, bolt.HoldSeconds,
                            bolt.ReturnSeconds, bolt.SoundPath)
                        : null)));
        }
        return result;
    }

    private static Dictionary<string, ItemType> BuildNames(
        IReadOnlyList<ItemDefinition> items,
        List<string> errors)
    {
        var result = new Dictionary<string, ItemType>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            AddName(item.Id, item.Type, item.Id);
            foreach (string alias in item.Aliases) AddName(alias, item.Type, item.Id);
        }
        return result;

        void AddName(string name, ItemType handle, string owner)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"Item {owner} contains an empty alias");
                return;
            }
            if (!result.TryAdd(name, handle)) errors.Add($"Duplicate item ID or alias {name}");
        }
    }

    private static ItemType ResolveDefault(
        string? id,
        string property,
        Dictionary<string, ItemType> names,
        List<string> errors)
    {
        if (id is not null && names.TryGetValue(id, out var value)) return value;
        errors.Add($"defaults.{property} references unknown item {id ?? "<missing>"}");
        return default;
    }

    private static IEnumerable<string> Files(string packPath, string kind)
    {
        string data = Path.Combine(packPath, "data");
        return Directory.Exists(data)
            ? Directory.GetFiles(data, "*.json", SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    new DirectoryInfo(Path.GetDirectoryName(path)!).Name,
                    kind,
                    StringComparison.Ordinal))
                .Order()
            : [];
    }

    private static T? Read<T>(string path, List<string> errors)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                   ?? throw new JsonException("document was null");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            errors.Add($"{path}: {ex.Message}");
            return default;
        }
    }

    private static void AppendHash(StringBuilder builder, string pack, string path)
        => builder.Append(pack).Append('\n').Append(Path.GetFileName(path)).Append('\n')
            .Append(File.ReadAllText(path)).Append('\n');

    private static bool ValidateId(string? id, string source, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add($"{source}: missing namespaced ID");
            return false;
        }
        int colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1
            || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':')))
        {
            errors.Add($"{source}: invalid namespaced ID '{id}'");
            return false;
        }
        return true;
    }

    private static bool IsHexColor(string? value)
        => value is not null
           && value.Length is 7 or 9
           && value[0] == '#'
           && value[1..].All(Uri.IsHexDigit);

    private static string FindBuiltInRoot()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "datapacks"),
            Path.Combine(Directory.GetCurrentDirectory(), "datapacks"),
        };
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && current is not null; i++, current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "datapacks"));
        return candidates.FirstOrDefault(Directory.Exists)
               ?? throw new DirectoryNotFoundException("Could not locate the built-in datapacks directory");
    }

    private sealed record Pack(string Id, int Priority, string Path);
    private sealed class PackManifest { public int SchemaVersion { get; set; } public required string Id { get; set; } public int Priority { get; set; } }
    private sealed class DefaultsSource
    {
        public string? PlayerPrimary { get; set; }
        public string? NpcPrimary { get; set; }
        public string? UnidentifiedThreatWeapon { get; set; }
        public string? AssaultWeapon { get; set; }
        public string? MarksmanWeapon { get; set; }
    }
    private sealed class BallisticsSource
    {
        public int SchemaVersion { get; set; }
        public required string Id { get; set; }
        public float ProjectileSpeed { get; set; }
        public float BenchMoa { get; set; }
        public float RecoilPerShotMoa { get; set; }
        public float RecoilDecayMoaPerSecond { get; set; }
        public float RecoilCapMoa { get; set; }
        public float SightingMoa { get; set; }
    }
    private sealed class ItemSource
    {
        public int SchemaVersion { get; set; }
        public required string Id { get; set; }
        public ushort? NetworkId { get; set; }
        public required string DisplayName { get; set; }
        public string[]? Aliases { get; set; }
        public ItemCategory Category { get; set; }
        public EquipSlot Slot { get; set; }
        public HotbarSlot? Hotbar { get; set; }
        public float MovementSpeedScale { get; set; } = 1f;
        public ItemBehavior Behavior { get; set; }
        public WeaponSource? Weapon { get; set; }
        public ArmorSource? Armor { get; set; }
        public PresentationSource? Presentation { get; set; }
    }
    private sealed class WeaponSource
    {
        public int MagazineCapacity { get; set; }
        public float RoundsPerMinute { get; set; }
        public float ReloadSeconds { get; set; }
        public ushort Damage { get; set; }
        public required string Ballistics { get; set; }
        public FireMode FireMode { get; set; }
    }
    private sealed class ArmorSource { public float Max { get; set; } }
    private sealed class PresentationSource
    {
        public required string Model { get; set; }
        public float WorldScale { get; set; } = 1f;
        public float AimMagnification { get; set; } = 1f;
        public float AimSpeedScale { get; set; } = 1f;
        public string[]? ShotSounds { get; set; }
        public string TracerColor { get; set; } = "#FFFF00";
        public string? ReloadSound { get; set; }
        public string? DistantReportSound { get; set; }
        public float ReloadVolume { get; set; } = 1f;
        public float CameraTrauma { get; set; } = 0.4f;
        public VisualRecoilSource? VisualRecoil { get; set; }
        public float AdsRecoilScale { get; set; } = 1f;
        public BoltSource? Bolt { get; set; }
    }
    private sealed class VisualRecoilSource { public float Back { get; set; } public float Lift { get; set; } public float PitchDegrees { get; set; } }
    private sealed class BoltSource
    {
        public float DelaySeconds { get; set; }
        public float TravelSeconds { get; set; }
        public float HoldSeconds { get; set; }
        public float ReturnSeconds { get; set; }
        public string? SoundPath { get; set; }
    }
}
