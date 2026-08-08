using System.Numerics;
using Demiurge.Net;

namespace Demiurge.GameServer;

/// <summary>Publishes authoritative match events as reliable, short-lived client UI messages.</summary>
public sealed class ActivityFeedSystem
{
    private readonly INetServer server;

    public ActivityFeedSystem(INetServer server) => this.server = server;

    public void ReportKill(ServerPlayer killer, ServerPlayer victim)
        => Broadcast(Actor(killer), Plain(" killed "), Actor(victim));

    public void ReportFlagCaptured(int team, Vector3 position)
        => Broadcast(TeamName(team), Plain($" captured flag at {Coordinates(position)}"));

    public void ReportFlagNeutralized(int team, Vector3 position)
        => Broadcast(TeamName(team), Plain($" neutralized flag at {Coordinates(position)}"));

    public void ReportNpcRelocated(ServerPlayer npc, string reason)
        => Broadcast(Actor(npc), Plain($" relocated by server: {reason}"));

    private void Broadcast(params ActivityFeedSegment[] segments)
    {
        var message = Message.Create(
            MessageSendMode.Reliable,
            ServerToClientId.ActivityFeed);
        message.AddSerializable(new ActivityFeedData { Segments = segments });
        server.SendToAll(message);
    }

    private static ActivityFeedSegment Plain(string text)
        => new() { Text = text, Team = 0 };

    private static ActivityFeedSegment Actor(ServerPlayer actor)
        => new()
        {
            Text = $"{(actor.IsMob ? "NPC" : "Player")} {actor.Id}",
            Team = (byte)Math.Clamp(actor.Team, 0, byte.MaxValue),
        };

    private static ActivityFeedSegment TeamName(int team)
        => new() { Text = $"Team {team}", Team = (byte)Math.Clamp(team, 0, byte.MaxValue) };

    private static string Coordinates(Vector3 position)
        => FormattableString.Invariant(
            $"({position.X:0}, {position.Y:0}, {position.Z:0})");
}
