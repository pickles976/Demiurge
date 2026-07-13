using Demiurge;
using Stride.Core.Mathematics;
using Stride.Engine;

// Muzzle-to-cursor aim line for the local player, shown while aiming. Successor
// to the old DrawAimLine (commit 5b6dc42): same LineRenderer call, but it reads
// the sim (Registry.LocalPlayer) instead of the deleted PlayerScript. Lives on
// the camera entity, whose CameraComponent projects the muzzle to screen space.
public class AimLineScript : SyncScript
{
    public required PlayerRegistry Registry { get; init; }

    public override void Update()
    {
        var local = Registry.LocalPlayer;
        if (local is not { IsArmed: true }) return;   // no weapon, no aim line
        if (!local.State.HasFlag(PlayerStateFlags.Aiming)) return;

        var camera = Entity.Get<CameraComponent>();
        if (camera == null) return;

        var muzzle = local.Position.ToStride() + Vector3.UnitY * GunConfig.MuzzleHeight;
        var muzzleScreen = MathExtensions.WorldToScreen(muzzle, camera.ViewProjectionMatrix, Game.Window.ClientBounds);
        var cursor = MathExtensions.MousePosToScreenCoords(Input.MousePosition, Game.Window.ClientBounds);
        LineRenderer.DrawLine2D(muzzleScreen, cursor, Color.White);
    }
}
