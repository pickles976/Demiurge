using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge;

/// <summary>Camera-facing identification above living friendly NPCs.</summary>
public sealed class FriendlyMarkerScript : SyncScript
{
    private const float Rise = 0.55f;
    private const float Radius = 0.24f;
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
            var centre = feet.ToStride()
                + Vector3.UnitY * (PlayerMovement.Body.Height + Rise);

            if (player.State.HasFlag(PlayerStateFlags.SquadLeader))
                DrawStar(centre, right, up);
            else
                DrawSquare(centre, right, up);
        }
    }

    private static void DrawSquare(Vector3 centre, Vector3 right, Vector3 up)
    {
        var x = right * Radius;
        var y = up * Radius;
        LineRenderer.DrawLine(centre - x - y, centre + x - y, MemberColor);
        LineRenderer.DrawLine(centre + x - y, centre + x + y, MemberColor);
        LineRenderer.DrawLine(centre + x + y, centre - x + y, MemberColor);
        LineRenderer.DrawLine(centre - x + y, centre - x - y, MemberColor);
    }

    private static void DrawStar(Vector3 centre, Vector3 right, Vector3 up)
    {
        const int points = 10;
        Vector3 first = default;
        Vector3 previous = default;
        for (int i = 0; i < points; i++)
        {
            float angle = -MathF.PI / 2f + i * MathF.PI / 5f;
            float radius = (i & 1) == 0 ? Radius : Radius * 0.42f;
            var point = centre
                + right * (MathF.Cos(angle) * radius)
                + up * (MathF.Sin(angle) * radius);
            if (i == 0) first = point;
            else LineRenderer.DrawLine(previous, point, LeaderColor);
            previous = point;
        }
        LineRenderer.DrawLine(previous, first, LeaderColor);
    }
}
