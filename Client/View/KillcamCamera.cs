using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using NVector3 = System.Numerics.Vector3;

namespace Demiurge;

/// <summary>
/// Owns the runtime camera while the local player is waiting for a respawn wave. This is a
/// deliberately simple body camera: it is created from the death position, backs away from the
/// corpse in the direction opposite the player's facing, and remains fixed until respawn.
/// </summary>
public sealed class KillcamCameraScript : SyncScript
{
    private const float TargetHeight = 0.72f;
    private const float BackDistance = 3.2f;
    private const float CameraLift = 1.55f;
    private const float WallClearance = 0.15f;
    private const float MinimumDistance = 0.05f;

    public required PlayerRegistry Registry { get; init; }
    public required TerrainState Terrain { get; init; }
    public required ClientInputState InputState { get; init; }

    private bool active;
    private NVector3 target;
    private NVector3 desiredOffset;

    public override void Update()
    {
        if (InputState.TerminalOpen
            || Entity.Get<DebugFlyCameraScript>()?.Active == true)
        {
            active = false;
            return;
        }

        if (Registry.LocalPlayer is not { } local || !local.IsDead)
        {
            active = false;
            return;
        }

        if (!active)
        {
            target = local.Position + NVector3.UnitY * TargetHeight;
            var forward = new NVector3(MathF.Sin(local.Yaw), 0f, MathF.Cos(local.Yaw));
            desiredOffset = -forward * BackDistance + NVector3.UnitY * CameraLift;
            active = true;
        }

        NVector3 offset = desiredOffset;
        float desiredDistance = offset.Length();
        if (desiredDistance > 1e-4f)
        {
            var direction = offset / desiredDistance;
            if (TerrainRaycast.Cast(Terrain.Map, target, direction, desiredDistance) is { } hit)
            {
                float distance = MathF.Max(
                    MinimumDistance,
                    MathF.Min(desiredDistance, hit.Distance - WallClearance));
                offset = direction * distance;
            }
        }

        var position = (target + offset).ToStride();
        var lookAt = Matrix.LookAtRH(position, target.ToStride(), Vector3.UnitY);
        lookAt.Invert();

        Entity.Transform.Position = position;
        Entity.Transform.Rotation = Quaternion.RotationMatrix(lookAt);
    }
}
