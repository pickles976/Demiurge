using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge;

/// <summary>Camera-facing identification above living friendly NPCs.</summary>
public sealed class FriendlyMarkerScript : SyncScript
{
    /// <summary>
    /// Clearance between the top of the DRAWN head and the bottom of the glyph.
    ///
    /// Anchored to the model, not to <see cref="PlayerMovement.Body"/>: the collision capsule is
    /// 1.80 m tall and the head a player can actually see ends at about 1.46, so measuring from the
    /// capsule floated these markers the better part of a metre above the visible NPC.
    /// </summary>
    private const float Rise = 0.10f;
    private const float CrownHeight = GunConfig.HeadCenterHeight + GunConfig.HeadRadius;
    private const float StarRadius = 0.08f;
    private const float SquareRadius = 0.04f;
    private static readonly Color LeaderColor = new(70, 255, 105, 245);
    private static readonly Color MemberColor = new(255, 220, 55, 245);

    public required PlayerRegistry Registry { get; init; }

    public override void Update()
    {
        if (Registry.LocalPlayer is not { Team: > 0 } local
            || LineRenderer.Camera?.Entity is not { } camera)
            return;

        var right = camera.Transform.Rotation * Vector3.UnitX;
        var up = camera.Transform.Rotation * Vector3.UnitY;
        foreach (var player in Registry.Players)
        {
            if (player.IsDead
                || player.Team != local.Team
                || !ActorIds.IsMob(player.Id))
                continue;

            var feet = player is RemotePlayer remote
                ? remote.Snapshots.GetInterpolated(Registry.RenderTick, remote.Position)
                : player.Position;
            bool leader = player.State.HasFlag(PlayerStateFlags.SquadLeader);
            // Radius is part of the offset so both glyphs clear the head by the same Rise, rather
            // than the smaller one sitting lower for being smaller.
            float radius = leader ? StarRadius : SquareRadius;
            var centre = feet.ToStride()
                + Vector3.UnitY * (CrownHeight + Rise + radius);

            if (leader)
                DrawStar(centre, right, up, radius);
            else
                DrawSquare(centre, right, up, radius);
        }
    }

    private static void DrawSquare(Vector3 centre, Vector3 right, Vector3 up, float radius)
    {
        var x = right * radius;
        var y = up * radius;
        LineRenderer.DrawLine(centre - x - y, centre + x - y, MemberColor);
        LineRenderer.DrawLine(centre + x - y, centre + x + y, MemberColor);
        LineRenderer.DrawLine(centre + x + y, centre - x + y, MemberColor);
        LineRenderer.DrawLine(centre - x + y, centre - x - y, MemberColor);
    }

    private static void DrawStar(Vector3 centre, Vector3 right, Vector3 up, float radius)
    {
        const int points = 10;
        Vector3 first = default;
        Vector3 previous = default;
        for (int i = 0; i < points; i++)
        {
            // +PI/2 puts the first OUTER vertex on +up. Starting at -PI/2 put it on -up, which draws
            // the star point-down.
            float angle = MathF.PI / 2f + i * MathF.PI / 5f;
            float vertexRadius = (i & 1) == 0 ? radius : radius * 0.42f;
            var point = centre
                + right * (MathF.Cos(angle) * vertexRadius)
                + up * (MathF.Sin(angle) * vertexRadius);
            if (i == 0) first = point;
            else LineRenderer.DrawLine(previous, point, LeaderColor);
            previous = point;
        }
        LineRenderer.DrawLine(previous, first, LeaderColor);
    }
}
