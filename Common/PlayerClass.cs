namespace Demiurge;

/// <summary>
/// The kit a human player asks to be issued on his next respawn.
///
/// Players only. An NPC's weapon is decided by <c>NpcSquadLoadout</c> from its place in the spawn
/// cohort, which is a squad composition problem and not a choice anybody makes — this is the choice
/// a man makes about himself, and the two must not be collapsed into one enum just because they
/// currently name the same three guns.
///
/// These values ARE the wire protocol: append, never reorder, never delete.
/// </summary>
public enum PlayerClass : byte
{
    Rifleman = 0,
    Marksman = 1,
    Assault = 2,
}

/// <summary>
/// The class table: what each class is called and what it carries. Shared, because the server has
/// to issue the kit and the client has to draw the menu, and a menu that disagreed with the
/// armoury would be the whole feature broken.
/// </summary>
public static class PlayerClasses
{
    public static readonly PlayerClass Default = PlayerClass.Rifleman;

    /// <summary>Selection order, and the order the picker draws them in.</summary>
    public static readonly PlayerClass[] All =
        [PlayerClass.Rifleman, PlayerClass.Marksman, PlayerClass.Assault];

    /// <summary>
    /// The primary each class is issued.
    ///
    /// Resolved through the datapack defaults rather than by item name, so the three classes name
    /// the same weapons the rest of the game already means by "the marksman's gun" and "the assault
    /// gun". A pack that swaps the assault weapon for something else moves the class with it.
    /// The rifleman's is <see cref="ItemConfig.DefaultPlayerPrimaryWeapon"/> because he IS the
    /// default: a player who never opens the picker gets exactly what he got before it existed.
    /// </summary>
    public static ItemType Weapon(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Marksman => ItemConfig.DefaultMarksmanWeapon,
        PlayerClass.Assault => ItemConfig.DefaultAssaultWeapon,
        _ => ItemConfig.DefaultPlayerPrimaryWeapon,
    };

    public static string Name(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Marksman => "MARKSMAN",
        PlayerClass.Assault => "ASSAULT",
        _ => "RIFLEMAN",
    };

    /// <summary>
    /// Whether a byte off the wire names a class. A client is free to send anything; an undefined
    /// value must be rejected rather than cast, or a stray byte silently becomes a weapon nobody
    /// has a name for.
    /// </summary>
    public static bool IsDefined(byte value) => value <= (byte)PlayerClass.Assault;
}
