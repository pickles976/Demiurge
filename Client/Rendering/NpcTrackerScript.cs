using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge;

/// <summary>
/// What the NPC tracker overlay draws. Layers are independent so the noisy ones can be left off while
/// keeping the cheap always-useful beacon; every combination is one immediate-mode line batch.
/// </summary>
[Flags]
public enum NpcTrackerLayers
{
    None = 0,
    /// <summary>A vertical beam per NPC, drawn without depth testing so it reads through terrain.</summary>
    Beacons = 1,
    /// <summary>Ground ring plus a facing spoke, depth tested, for reading stance and heading up close.</summary>
    Facing = 2,
    /// <summary>A line between every pair of NPCs within bunching distance of each other.</summary>
    Clustering = 4,

    All = Beacons | Facing | Clustering,
}

/// <summary>
/// Process-wide toggle for the overlay. Static for the same reason <see cref="LineRenderer.Camera"/> is:
/// the developer terminal is owned by the process and outlives every session, so a view-only debug
/// switch it sets must not have to be plumbed through session construction to be reachable.
/// </summary>
public static class NpcTracker
{
    public static NpcTrackerLayers Layers { get; set; } = NpcTrackerLayers.None;

    public static bool Enabled => Layers != NpcTrackerLayers.None;
}

/// <summary>
/// Draws every AI actor the client knows about. Purely a view: it reads the replicated player registry
/// and issues immediate-mode lines, never touching simulation or network state. NPCs are identified by
/// <see cref="ActorIds.IsMob"/> rather than by a replicated flag.
/// </summary>
public sealed class NpcTrackerScript : SyncScript
{
    /// <summary>Beam height. Tall enough to clear terrain folds without filling the screen.</summary>
    private const float BeaconHeight = 4f;
    private const float GroundRingRadius = 0.45f;
    private const int GroundRingSegments = 12;
    private const float FacingSpokeLength = 1.5f;

    /// <summary>
    /// Two NPCs closer than this are drawn as bunched. Roughly what one burst or grenade covers, which
    /// is the thing bunching actually costs them.
    /// </summary>
    private const float BunchingDistance = 4f;
    private const float BunchingDistanceSquared = BunchingDistance * BunchingDistance;

    private static readonly Color[] TeamColors =
    [
        new Color(235, 235, 240, 255),
        new Color(90, 170, 255, 255),
        new Color(255, 110, 90, 255),
        new Color(150, 255, 130, 255),
        new Color(255, 210, 90, 255),
    ];

    private static readonly Color BunchedColor = new(255, 80, 200, 210);

    private readonly List<Player> visible = [];

    public required PlayerRegistry Registry { get; init; }

    public override void Update()
    {
        // ScriptComponent has no Enabled switch and ScriptSystem schedules every registered sync
        // script unconditionally, so an early return is the only way to actually stop the work.
        if (!NpcTracker.Enabled) return;

        var layers = NpcTracker.Layers;
        visible.Clear();
        foreach (var player in Registry.Players)
        {
            if (!ActorIds.IsMob(player.Id) || player.IsDead) continue;
            visible.Add(player);
        }

        foreach (var npc in visible)
        {
            Color color = ColorFor(npc.Team);
            Vector3 feet = npc.Position.ToStride();

            if (layers.HasFlag(NpcTrackerLayers.Beacons))
                LineRenderer.DrawLine(feet, feet + Vector3.UnitY * BeaconHeight, color);

            if (!layers.HasFlag(NpcTrackerLayers.Facing)) continue;

            DrawGroundRing(feet, color);
            var heading = new Vector3(MathF.Sin(npc.Yaw), 0f, MathF.Cos(npc.Yaw));
            Vector3 chest = feet + Vector3.UnitY * GunConfig.PlayerCenterHeight;
            LineRenderer.DrawDepthTestedLine(chest, chest + heading * FacingSpokeLength, color);
        }

        if (!layers.HasFlag(NpcTrackerLayers.Clustering)) return;

        for (int i = 0; i < visible.Count; i++)
            for (int j = i + 1; j < visible.Count; j++)
            {
                var a = visible[i].Position;
                var b = visible[j].Position;
                float dx = a.X - b.X;
                float dz = a.Z - b.Z;
                if (dx * dx + dz * dz > BunchingDistanceSquared) continue;
                Vector3 up = Vector3.UnitY * GunConfig.PlayerCenterHeight;
                LineRenderer.DrawLine(a.ToStride() + up, b.ToStride() + up, BunchedColor);
            }
    }

    private static void DrawGroundRing(Vector3 centre, Color color)
    {
        Vector3 previous = default;
        for (int i = 0; i <= GroundRingSegments; i++)
        {
            float angle = MathF.Tau * i / GroundRingSegments;
            var point = new Vector3(
                centre.X + MathF.Cos(angle) * GroundRingRadius,
                centre.Y + 0.05f,
                centre.Z + MathF.Sin(angle) * GroundRingRadius);
            if (i > 0) LineRenderer.DrawDepthTestedLine(previous, point, color);
            previous = point;
        }
    }

    private static Color ColorFor(int team)
        => TeamColors[team < 0 || team >= TeamColors.Length ? 0 : team];
}
