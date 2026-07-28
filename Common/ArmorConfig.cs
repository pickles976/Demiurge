namespace Demiurge
{
    /// <summary>Static per-armor numbers. Nothing consumes these yet — damage
    /// absorption is a future pass — but the row already drives the ArmorState
    /// mask bit and its initial values on spawn.</summary>
    public readonly record struct ArmorStats(float Max);

    /// <summary>The armor trait table: only armor has rows, null means "not
    /// armor". Same contract as WeaponConfig — a row here IS the trait.</summary>
    public static class ArmorConfig
    {
        public static ArmorStats? Get(ItemType type) => type switch
        {
            ItemType.BodyArmor => new ArmorStats(Max: 50f),
            _ => null,
        };
    }
}
