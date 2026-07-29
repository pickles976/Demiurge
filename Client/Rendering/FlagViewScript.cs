using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge;

/// <summary>Temporary line-rendered flag and capture boundary.</summary>
public sealed class FlagViewScript : SyncScript
{
    public required NetObject Object { get; init; }

    public override void Update()
    {
        var origin = Object.Transform.Position.ToStride();
        var top = origin + Vector3.UnitY * 2.5f;
        var ownerColor = TeamColor(Object.Team.Value, 235);
        int progressTeam = Object.Team.CapturingTeam != FlagConfig.NeutralTeam
            ? Object.Team.CapturingTeam
            : Object.Team.Value;
        var progressColor = TeamColor(progressTeam, 180);
        LineRenderer.DrawLine(origin, top, new Color(215, 215, 220, 255));

        var inner = top - Vector3.UnitY * 0.15f;
        var outerTop = top + Vector3.UnitX * 1.0f;
        var outerBottom = inner + Vector3.UnitX * 0.8f;
        LineRenderer.DrawLine(top, outerTop, ownerColor);
        LineRenderer.DrawLine(outerTop, outerBottom, ownerColor);
        LineRenderer.DrawLine(outerBottom, inner, ownerColor);
        LineRenderer.DrawLine(inner, top, ownerColor);

        const int segments = 24;
        int filledSegments = (int)MathF.Round(
            Math.Clamp(Object.Team.Progress, 0f, 1f) * segments);
        var emptyColor = new Color(210, 210, 215, 38);
        var previous = origin + new Vector3(FlagConfig.CaptureRadius, 0.05f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * MathUtil.TwoPi / segments;
            var point = origin + new Vector3(
                MathF.Cos(angle) * FlagConfig.CaptureRadius,
                0.05f,
                MathF.Sin(angle) * FlagConfig.CaptureRadius);
            LineRenderer.DrawLine(
                previous,
                point,
                i <= filledSegments ? progressColor : emptyColor);
            previous = point;
        }
    }

    private static Color TeamColor(int team, byte alpha)
    {
        if (team <= 0) return new Color(220, 220, 220, alpha);
        uint hash = unchecked((uint)team * 2654435761u);
        return new Color(
            (byte)(80 + (hash & 0x7f)),
            (byte)(80 + ((hash >> 8) & 0x7f)),
            (byte)(80 + ((hash >> 16) & 0x7f)),
            alpha);
    }
}
