namespace Demiurge.Tests;

public sealed class DataPackRegistryTests
{
    [Fact]
    public void BasePackDefinesTheExpectedItemsAndDefaults()
    {
        string[] expected =
        [
            "demiurge:body_armor",
            "demiurge:dp27",
            "demiurge:grenade",
            "demiurge:mortar",
            "demiurge:mosin",
            "demiurge:ppsh",
            "demiurge:shovel",
            "demiurge:sks",
        ];

        Assert.Equal(expected, ItemCatalog.All.Select(item => item.Id).Order().ToArray());
        Assert.Equal(ItemType.Mosin, ItemConfig.DefaultPlayerPrimaryWeapon);
        Assert.Equal(ItemType.Sks, ItemConfig.DefaultNpcPrimaryWeapon);
        Assert.Equal(ItemType.Ppsh, ItemConfig.DefaultAssaultWeapon);
        Assert.Equal(ItemType.Mosin, ItemConfig.DefaultMarksmanWeapon);
        Assert.Equal(64, ItemCatalog.Registry.GameplayHash.Length);
    }

    [Fact]
    public void AdditionalPackCanAddAWeaponWithoutChangingCode()
    {
        string root = Path.Combine(Path.GetTempPath(), "demiurge-datapack-tests", Guid.NewGuid().ToString("N"));
        string pack = Path.Combine(root, "example_pack");
        string items = Path.Combine(pack, "data", "example", "items");
        Directory.CreateDirectory(items);

        try
        {
            File.WriteAllText(
                Path.Combine(pack, "pack.json"),
                """
                { "schemaVersion": 1, "id": "example:test_pack", "priority": 100 }
                """);
            File.WriteAllText(
                Path.Combine(items, "field_rifle.json"),
                """
                {
                  "schemaVersion": 1,
                  "id": "example:field_rifle",
                  "displayName": "Field Rifle",
                  "aliases": ["field-rifle"],
                  "category": "equippable",
                  "slot": "hand",
                  "hotbar": "primary",
                  "behavior": "firearm",
                  "weapon": {
                    "magazineCapacity": 12,
                    "roundsPerMinute": 300,
                    "reloadSeconds": 2.25,
                    "damage": 42,
                    "ballistics": "demiurge:semi_automatic_rifle",
                    "fireMode": "semiAutomatic"
                  },
                  "presentation": { "model": "assets/models/sks.gltf" }
                }
                """);

            var first = DataPackLoader.LoadFromRoots([BuiltInRoot(), root]);
            var second = DataPackLoader.LoadFromRoots([BuiltInRoot(), root]);

            Assert.True(first.TryResolve("example:field_rifle", out var handle));
            Assert.True(first.TryResolve("field-rifle", out var alias));
            Assert.Equal(handle, alias);
            Assert.True((ushort)handle >= 1024);
            Assert.Equal((ushort)42, first.RequireItem(handle).Weapon!.Value.Damage);
            Assert.Equal(first.GameplayHash, second.GameplayHash);
            Assert.True(second.TryResolve("example:field_rifle", out var secondHandle));
            Assert.Equal(handle, secondHandle);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownJsonFieldsFailFast()
    {
        string root = Path.Combine(Path.GetTempPath(), "demiurge-datapack-tests", Guid.NewGuid().ToString("N"));
        string pack = Path.Combine(root, "invalid_pack");
        Directory.CreateDirectory(pack);

        try
        {
            File.WriteAllText(
                Path.Combine(pack, "pack.json"),
                """
                { "schemaVersion": 1, "id": "example:invalid", "priority": 1, "typo": true }
                """);

            var error = Assert.Throws<InvalidDataException>(
                () => DataPackLoader.LoadFromRoots([BuiltInRoot(), root]));
            Assert.Contains("typo", error.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string BuiltInRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "datapacks");
            if (Directory.Exists(Path.Combine(candidate, "base"))) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the built-in datapacks");
    }
}
