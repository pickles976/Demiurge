using Demiurge.GameClient;

namespace Demiurge;

/// <summary>
/// What this player's team believes about where the enemy is, as the server last stated it.
///
/// Sim, not view: it holds what arrived and decides nothing. It exists as its own object rather than
/// as a field on the minimap because the belief is a fact about the MATCH, not about a widget — a
/// second consumer (a full-screen map, a spot-callout) reads the same thing rather than subscribing
/// to the same message twice and drifting.
///
/// Snapshots arrive unreliably, so an older one can overtake a newer. It is dropped on the tick
/// stamp instead of being merged: every snapshot is complete, so the newest is always the whole
/// truth and there is nothing in an older one worth keeping.
/// </summary>
public sealed class TeamIntel : IDisposable
{
    private readonly NetworkManager network;
    private TeamContact[] contacts = [];
    private uint newestTick;

    public TeamIntel(NetworkManager network)
    {
        this.network = network;
        network.TeamIntelReceived += OnTeamIntel;
    }

    /// <summary>Believed enemy positions. Never null; empty until the first snapshot arrives, which
    /// is also the honest answer when the team has not located anybody.</summary>
    public IReadOnlyList<TeamContact> Contacts => contacts;

    private void OnTeamIntel(TeamIntelData data)
    {
        // Unsigned wraparound is not a concern at 30 Hz — a uint of ticks is four and a half years.
        if (data.Tick < newestTick) return;
        newestTick = data.Tick;
        contacts = data.Contacts ?? [];
    }

    public void Dispose() => network.TeamIntelReceived -= OnTeamIntel;
}
