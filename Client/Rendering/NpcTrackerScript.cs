using Demiurge.GameServer;
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

    /// <summary>
    /// The volumes a shot is tested against — head sphere and hit capsule — for EVERY actor rather
    /// than only the NPCs, since the question this answers is usually about a player.
    /// </summary>
    Colliders = 8,

    /// <summary>Each NPC's actor id over its head, so the overlay and the terminal name the same man.</summary>
    Ids = 16,

    /// <summary>Each NPC's current decision over its head. See <see cref="MobDebugFeed"/>.</summary>
    States = 32,

    /// <summary>
    /// The route each NPC is following, as a line through what it has left of its waypoints, with
    /// the non-walk actions marked. See <see cref="MobPathFeed"/>.
    /// </summary>
    Paths = 64,

    All = Beacons | Facing | Clustering | Colliders | Ids | States | Paths,
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

    /// <summary>The capsule a bullet is tested against, and the head sphere that doubles the damage.</summary>
    private static readonly Color HitVolumeColor = new(255, 235, 120, 200);
    private static readonly Color HeadVolumeColor = new(255, 120, 120, 220);

    /// <summary>
    /// The MOVEMENT capsule, which is a different volume from the one shots use — 0.4 m of radius
    /// against 0.6 m, on the same 1.8 m of height. Dimmer because it is the secondary answer, and
    /// drawn because two capsules that are easy to assume agree do not.
    /// </summary>
    private static readonly Color BodyVolumeColor = new(120, 200, 255, 130);

    private const int VolumeSegments = 16;

    /// <summary>Cell height of a label, and how far over the head it floats.</summary>
    private const float LabelHeight = 0.22f;
    private const float LabelRise = 0.35f;
    private static readonly Color LabelColor = new(255, 255, 255, 235);
    private static readonly Color StateColor = new(140, 255, 190, 235);

    /// <summary>
    /// Route colours. Walk legs take the actor's team colour so two squads' routes stay apart; the
    /// non-walk actions get their own, because "why is he not moving" is usually answered by one of
    /// them — a jump he cannot take off for, a drop, or soil he is waiting to dig.
    /// </summary>
    private static readonly Color JumpLegColor = new(255, 235, 120, 235);
    private static readonly Color FallLegColor = new(120, 220, 255, 235);
    private static readonly Color DigLegColor = new(255, 150, 80, 235);

    /// <summary>Route lines float at ankle height so they read over the ground rather than in it.</summary>
    private const float PathRise = 0.15f;
    private const float PathMarkerSize = 0.25f;

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
            if (player.IsDead) continue;

            // Colliders are the one layer that is about actors rather than about AI: a player's own
            // hit volume is usually the thing in question, and he is not in `visible`.
            if (layers.HasFlag(NpcTrackerLayers.Colliders)) DrawVolumes(player);
            if (!ActorIds.IsMob(player.Id)) continue;
            visible.Add(player);
        }

        var states = layers.HasFlag(NpcTrackerLayers.States)
            ? MobDebugFeed.Latest
            : null;

        if (layers.HasFlag(NpcTrackerLayers.Paths))
        {
            var routes = MobPathFeed.Latest;
            foreach (var npc in visible)
                if (routes.TryGetValue(npc.Id, out var route))
                    DrawRoute(npc, route);
        }

        foreach (var npc in visible)
        {
            // Stacked upward so both labels are readable at once rather than one over the other.
            float labelY = PlayerMovement.Body.Height + LabelRise;
            if (layers.HasFlag(NpcTrackerLayers.Ids))
            {
                LineText.Draw(
                    npc.Position.ToStride() + Vector3.UnitY * labelY,
                    npc.Id.ToString(),
                    LabelColor,
                    LabelHeight);
                labelY += LabelHeight * 1.6f;
            }

            if (states is not null && states.TryGetValue(npc.Id, out var state))
                LineText.Draw(
                    npc.Position.ToStride() + Vector3.UnitY * labelY,
                    state,
                    StateColor,
                    LabelHeight);
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

    /// <summary>
    /// One NPC's remaining route: a line from where he is, through his waypoints, with a cross at
    /// each one that is not an ordinary walk.
    ///
    /// Drawn WITHOUT depth testing, deliberately. A route that disappears behind the wall the actor
    /// is stuck against is a route you cannot read, and reading it is the entire purpose.
    /// </summary>
    private void DrawRoute(Player npc, IReadOnlyList<MobPathPoint> route)
    {
        Vector3 previous = npc.Position.ToStride() + Vector3.UnitY * PathRise;
        Color team = ColorFor(npc.Team);
        foreach (var point in route)
        {
            Vector3 next = point.Position.ToStride() + Vector3.UnitY * PathRise;
            Color legColor = point.Action switch
            {
                NavAction.Jump => JumpLegColor,
                NavAction.Fall => FallLegColor,
                NavAction.Dig => DigLegColor,
                _ => team,
            };
            LineRenderer.DrawLine(previous, next, legColor);
            if (point.Action != NavAction.Walk) DrawMarker(next, legColor);
            previous = next;
        }
    }

    /// <summary>A three-axis cross, which stays legible from any angle a ring or a square does not.</summary>
    private static void DrawMarker(Vector3 centre, Color color)
    {
        LineRenderer.DrawLine(centre - Vector3.UnitX * PathMarkerSize, centre + Vector3.UnitX * PathMarkerSize, color);
        LineRenderer.DrawLine(centre - Vector3.UnitY * PathMarkerSize, centre + Vector3.UnitY * PathMarkerSize, color);
        LineRenderer.DrawLine(centre - Vector3.UnitZ * PathMarkerSize, centre + Vector3.UnitZ * PathMarkerSize, color);
    }

    /// <summary>
    /// The three volumes an actor occupies, drawn from the same constants the server tests against
    /// rather than from anything measured off the model — the whole point is to see where those
    /// constants actually put the geometry.
    /// </summary>
    private static void DrawVolumes(Player player)
    {
        Vector3 feet = player.Position.ToStride();
        bool crouching = player.State.HasFlag(PlayerStateFlags.Crouching);

        // Cap centres exactly as GunMath.PlayerHitAt derives them, so the drawing cannot claim a
        // capsule the hit test does not use.
        float hitRadius = GunConfig.HitRadius;
        float height = PlayerMovement.Body.Height;
        DrawCapsule(feet, hitRadius, height, HitVolumeColor);
        DrawCapsule(feet, PlayerMovement.Body.Radius, height, BodyVolumeColor);

        var head = GunConfig.HeadCenter(player.Position, crouching).ToStride();
        DrawSphere(head, GunConfig.HeadRadius, HeadVolumeColor);
    }

    /// <summary>
    /// A capsule spanning exactly [feet, feet + height], as three rings and four uprights. Wireframe
    /// rather than a swept outline because it has to be readable from inside as well as outside.
    /// </summary>
    private static void DrawCapsule(Vector3 feet, float radius, float height, Color color)
    {
        float capOffset = MathF.Min(radius, height * 0.5f);
        DrawRing(feet + Vector3.UnitY * capOffset, radius, color);
        DrawRing(feet + Vector3.UnitY * (height * 0.5f), radius, color);
        DrawRing(feet + Vector3.UnitY * (height - capOffset), radius, color);

        for (int i = 0; i < 4; i++)
        {
            float angle = MathF.Tau * i / 4f;
            var offset = new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            LineRenderer.DrawDepthTestedLine(
                feet + Vector3.UnitY * capOffset + offset,
                feet + Vector3.UnitY * (height - capOffset) + offset,
                color);
        }

        // The domes, as two arcs each, so the ends read as rounded rather than flat.
        DrawArc(feet + Vector3.UnitY * (height - capOffset), radius, Vector3.UnitX, color);
        DrawArc(feet + Vector3.UnitY * (height - capOffset), radius, Vector3.UnitZ, color);
        DrawArc(feet + Vector3.UnitY * capOffset, -radius, Vector3.UnitX, color);
        DrawArc(feet + Vector3.UnitY * capOffset, -radius, Vector3.UnitZ, color);
    }

    private static void DrawSphere(Vector3 centre, float radius, Color color)
    {
        DrawRing(centre, radius, color);
        DrawArc(centre, radius, Vector3.UnitX, color);
        DrawArc(centre, -radius, Vector3.UnitX, color);
        DrawArc(centre, radius, Vector3.UnitZ, color);
        DrawArc(centre, -radius, Vector3.UnitZ, color);
    }

    private static void DrawRing(Vector3 centre, float radius, Color color)
    {
        Vector3 previous = default;
        for (int i = 0; i <= VolumeSegments; i++)
        {
            float angle = MathF.Tau * i / VolumeSegments;
            var point = centre + new Vector3(
                MathF.Cos(angle) * radius,
                0f,
                MathF.Sin(angle) * radius);
            if (i > 0) LineRenderer.DrawDepthTestedLine(previous, point, color);
            previous = point;
        }
    }

    /// <summary>Half a vertical circle, from the equator up over the pole. A negative
    /// <paramref name="radius"/> sweeps the lower half instead.</summary>
    private static void DrawArc(Vector3 centre, float radius, Vector3 axis, Color color)
    {
        Vector3 previous = default;
        int segments = VolumeSegments / 2;
        for (int i = 0; i <= segments; i++)
        {
            float angle = MathF.PI * i / segments;
            var point = centre
                + axis * (MathF.Cos(angle) * MathF.Abs(radius))
                + Vector3.UnitY * (MathF.Sin(angle) * radius);
            if (i > 0) LineRenderer.DrawDepthTestedLine(previous, point, color);
            previous = point;
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
