using Demiurge.Net;

namespace Demiurge.GameServer;

/// <summary>
/// Who is in the match, on whose side, and how they are doing.
///
/// Two counters and a broadcast, but the placement of the two counters is the whole design:
///
///  - a KILL is credited in <see cref="ActivityFeedSystem.ReportKill"/>, because every weapon that
///    can kill somebody already reports there — rifle, grenade and bomb all funnel through it — and
///    a second funnel would be a second place to forget when a fourth arrives;
///  - a DEATH is counted where <see cref="GameWorld"/> observes one, which is the single tick a
///    body's health first reads zero. That catches the deaths nobody is credited with, and a
///    scoreboard where the kills and the deaths disagree because somebody drowned is worse than one
///    with no deaths on it at all.
///
/// The board is pushed rather than polled, and only when it has actually changed — see
/// <see cref="ScoreboardData"/> for why a whole roster rather than deltas.
/// </summary>
public sealed class MatchScoreSystem
{
    private readonly INetServer server;
    private bool dirty = true;

    public MatchScoreSystem(INetServer server) => this.server = server;

    /// <summary>The roster changed in a way a client would see. Cheap and idempotent, so callers
    /// may say so without checking whether anybody was listening.</summary>
    public void Invalidate() => dirty = true;

    public void CreditKill(ServerPlayer killer, ServerPlayer victim)
    {
        // Shooting your own side is not an achievement, and neither is shooting yourself. The death
        // still counts against the victim — GameWorld does that — so the board stays honest about
        // what happened without rewarding it.
        if (killer.Id != victim.Id && killer.Team != victim.Team)
            killer.Kills++;
        dirty = true;
    }

    public void CountDeath(ServerPlayer victim)
    {
        victim.Deaths++;
        dirty = true;
    }

    /// <summary>Sends the board if anything moved. Called once a tick; a tick in which nobody died
    /// and nobody joined costs one boolean.</summary>
    public void BroadcastIfChanged(IEnumerable<ServerPlayer> actors)
    {
        if (!dirty) return;
        dirty = false;
        server.SendToAll(Build(actors));
    }

    /// <summary>The board as it stands, for a client that has just arrived. Everything else about
    /// the match reaches a newcomer this way too — see GameWorld.AddPlayer.</summary>
    public void SendTo(ushort clientId, IEnumerable<ServerPlayer> actors)
        => server.Send(Build(actors), clientId);

    private static Message Build(IEnumerable<ServerPlayer> actors)
    {
        var entries = actors
            .Where(actor => actor.Team > 0)
            // Living players first, then by kills, then by id: the order is the server's so every
            // client's board agrees, and so the client can draw the rows it is handed without
            // deciding anything.
            .OrderBy(actor => actor.IsMob ? 1 : 0)
            .ThenByDescending(actor => actor.Kills)
            .ThenBy(actor => actor.Id)
            .Select(actor => new ScoreboardEntry
            {
                ActorId = actor.Id,
                Team = (byte)Math.Clamp(actor.Team, 0, byte.MaxValue),
                Flags = actor.IsMob ? ScoreboardEntry.MobFlag : (byte)0,
                Kills = (ushort)Math.Clamp(actor.Kills, 0, ushort.MaxValue),
                Deaths = (ushort)Math.Clamp(actor.Deaths, 0, ushort.MaxValue),
            })
            .ToArray();

        var message = Message.Create(MessageSendMode.Reliable, ServerToClientId.Scoreboard);
        message.AddSerializable(new ScoreboardData { Entries = entries });
        return message;
    }
}
