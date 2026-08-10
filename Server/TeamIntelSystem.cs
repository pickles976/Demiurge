using System.Numerics;
using Demiurge.Net;

namespace Demiurge.GameServer;

/// <summary>
/// What each team knows about where the enemy is, and the only thing that tells a player's minimap.
///
/// One <see cref="ContactMemory"/> per team, fed from the two things a team actually earns:
///
///  - what its NPCs SEE. Perception is already server-side and already produces exactly this, per
///    actor; a team's picture is the union of its living members' beliefs, so it is merged rather
///    than recomputed and inherits the fade and the retention window for free.
///  - what anyone HEARS. A rifle going off inside GunshotHearing.MaximumDistance of any living
///    member locates its firer, at the same accuracy an NPC gets — see
///    <see cref="GunshotHearing.PerceivedPosition"/>, whose error grows with range so a shot across
///    the field is a rough bearing and one behind you is a man.
///
/// The distinction that matters: the client is already sent every actor's position, because it has
/// to draw them. A minimap filtered from THAT would show the enemy through hills. This is the
/// smaller set, and keeping the two apart is why it is worth a message rather than a client filter.
///
/// Players are listeners here even though they are not listeners in <c>MobSystem.ProcessGunshots</c>,
/// and the asymmetry is deliberate: that method drives an NPC's investigate-the-noise behaviour,
/// which a human does not need because he can hear the game. Feeding his ears into the team picture
/// is a different question with a different answer.
/// </summary>
public sealed class TeamIntelSystem
{
    /// <summary>
    /// How often the picture is pushed. A minimap marker is a belief that decays over seconds, so
    /// twice a second is smooth to look at and costs a few hundred bytes a second to a human player —
    /// of whom there are far fewer than there are NPCs.
    /// </summary>
    public const uint BroadcastTicks = NetworkConfig.TickRate / 2;

    /// <summary>
    /// How long a contact nobody has refreshed stays on the map.
    ///
    /// The squad window rather than the individual one: this is a TEAM's memory, and a team does not
    /// forget a sighting as fast as the man who made it stops looking at it.
    /// </summary>
    private const int RetentionTicks = ContactMemory.SquadRetentionTicks;

    private readonly INetServer server;
    private readonly Dictionary<int, ContactMemory> byTeam = new();
    private readonly List<TeamContact> scratch = [];

    public TeamIntelSystem(INetServer server) => this.server = server;

    /// <summary>A shot was fired and somebody on <paramref name="listenerTeam"/> was close enough to
    /// place it. Called per listener, so the closest hearer's estimate is the one that lands — a
    /// later, worse fix cannot overwrite a better one within the same tick because
    /// <see cref="ContactMemory.Observe"/> keeps the newest and they share a tick.</summary>
    public void Heard(int listenerTeam, ushort shooterId, Vector3 perceived, uint tick)
        => Memory(listenerTeam).Observe(shooterId, perceived, tick);

    /// <summary>
    /// Folds every living NPC's own belief into its team's, then ages the lot.
    ///
    /// Merging rather than snapshotting-and-copying because ContactMemory already knows how to take
    /// the better of two observations of the same man, which is precisely what a team picture built
    /// from sixteen overlapping fields of view needs.
    /// </summary>
    public void Update(uint tick, IEnumerable<ServerPlayer> actors, Func<ushort, ContactMemory?> beliefOf)
    {
        foreach (var actor in actors)
        {
            if (!actor.IsMob || actor.Team <= 0 || actor.Status is { Health.Current: 0 }) continue;
            beliefOf(actor.Id)?.MergeInto(Memory(actor.Team), tick);
        }

        foreach (var memory in byTeam.Values) memory.Prune(tick);
    }

    /// <summary>Pushes each team's picture to its own human players. Nothing is sent to a team with
    /// nobody watching, which in a normal match is every team but one.</summary>
    public void Broadcast(uint tick, IEnumerable<ServerPlayer> actors)
    {
        if (tick % BroadcastTicks != 0) return;

        foreach (var group in actors.Where(actor => !actor.IsMob && actor.Team > 0).GroupBy(actor => actor.Team))
        {
            var message = Build(group.Key, tick);
            foreach (var player in group) server.Send(message, player.Id);
        }
    }

    /// <summary>
    /// What one team believes, as the wire would carry it. Separate from <see cref="Build"/> so the
    /// two rules that feed this — what NPCs see, what anybody hears — can be checked without a
    /// socket. They are worth checking: an empty picture and a picture nobody is looking at produce
    /// the same blank minimap.
    /// </summary>
    internal IReadOnlyList<TeamContact> SnapshotFor(int team, uint tick)
    {
        scratch.Clear();
        if (byTeam.TryGetValue(team, out var memory))
            foreach (var contact in memory.Snapshot(tick))
                scratch.Add(new TeamContact
                {
                    ActorId = contact.ActorId,
                    Position = contact.Position,
                    Confidence = (byte)Math.Clamp((int)MathF.Round(contact.Confidence * 255f), 0, 255),
                });
        return scratch;
    }

    private Message Build(int team, uint tick)
    {
        var message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.TeamIntel);
        message.AddSerializable(
            new TeamIntelData { Tick = tick, Contacts = SnapshotFor(team, tick).ToArray() });
        return message;
    }

    private ContactMemory Memory(int team)
    {
        if (!byTeam.TryGetValue(team, out var memory))
            byTeam[team] = memory = new ContactMemory(RetentionTicks);
        return memory;
    }
}
