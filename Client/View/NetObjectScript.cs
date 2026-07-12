using Demiurge;
using Stride.Core.Mathematics;
using Stride.Engine;

// Interpolated transform — the RemotePlayer branch of PlayerViewScript, generalized.
public class NetTransformScript : SyncScript
{
    public required NetObject Object { get; init; }

    public override void Update()
    {
        double renderTick = Object.Snapshots.NewestTick
            + Object.Snapshots.SecondsSinceNewest * NetworkConfig.TickRate - 3.0;
        Entity.Transform.Position =
            Object.Snapshots.GetInterpolated(renderTick, Object.Transform.Position).ToStride();
        Entity.Transform.Rotation = Quaternion.RotationY(Object.Transform.Yaw);
    }
}

// TEMPORARY health visual: squash the model by health fraction. Stand-in until
// a real world-space health bar exists; proves the per-component view pipeline.
public class HealthScaleScript : SyncScript
{
    public required NetObject Object { get; init; }

    public override void Update()
    {
        float frac = Object.Health.Max == 0 ? 1f : Object.Health.Current / (float)Object.Health.Max;
        Entity.Transform.Scale = new Vector3(1f, 0.3f + 0.7f * frac, 1f);
    }
}