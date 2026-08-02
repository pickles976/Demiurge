using System.Numerics;
using Demiurge.Net;

namespace Demiurge.GameServer;

/// <summary>Publishes authoritative match events as reliable, short-lived client UI messages.</summary>
public sealed class ActivityFeedSystem
{
    private readonly INetServer server;

    public ActivityFeedSystem(INetServer server) => this.server = server;

    public void ReportKill(ServerPlayer killer, ServerPlayer victim)
        => Broadcast($"{ActorName(killer)} killed {ActorName(victim)}");

    public void ReportFlagCaptured(int team, Vector3 position)
        => Broadcast($"Team {team} captured flag at {Coordinates(position)}");

    public void ReportFlagNeutralized(int team, Vector3 position)
        => Broadcast($"Team {team} neutralized flag at {Coordinates(position)}");

    public void ReportNpcRelocated(ushort npcId, string reason)
        => Broadcast($"NPC {npcId} relocated by server: {reason}");

    private void Broadcast(string text)
    {
        var message = Message.Create(
            MessageSendMode.Reliable,
            ServerToClientId.ActivityFeed);
        message.AddSerializable(new ActivityFeedData { Text = text });
        server.SendToAll(message);
    }

    private static string ActorName(ServerPlayer actor)
        => $"{(actor.IsMob ? "NPC" : "Player")} {actor.Id}";

    private static string Coordinates(Vector3 position)
        => FormattableString.Invariant(
            $"({position.X:0}, {position.Y:0}, {position.Z:0})");
}
