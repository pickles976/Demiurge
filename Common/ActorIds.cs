namespace Demiurge;

/// <summary>
/// Session actor id ranges. Human players take ids assigned by the transport, mobs are allocated from
/// a fixed high range so an id alone identifies an NPC. That is what lets the client tell an AI from a
/// remote human without replicating a flag for it, and it is the same range the terminal's actor
/// selectors display (<c>@60000</c>).
/// </summary>
public static class ActorIds
{
    public const ushort FirstMob = 60000;

    public static bool IsMob(ushort actorId) => actorId >= FirstMob;
}
