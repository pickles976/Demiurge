using System.Numerics;
using Demiurge;
using Stride.Engine;
using Stride.Input;

public class LocalPlayerController : SyncScript
{
	public required Entity CameraEntity { get; init; }
	public required PlayerRegistry Registry { get; init; }

	public override void Update()
	{
		var local = Registry.LocalPlayer;
		if (local == null) return;   // not spawned yet

		// Position
		var intent = ComputeIntent();   // the WASD + camera-flatten math you already have
		local.Update(intent, (float)Game.UpdateTime.Elapsed.TotalSeconds);

		// State
		local.State = local.State
			.With(PlayerStateFlags.Moving, intent != Vector3.Zero)
			.With(PlayerStateFlags.Sprinting, Input.IsKeyDown(Keys.LeftShift))
			.With(PlayerStateFlags.Aiming, Input.IsMouseButtonDown(MouseButton.Right));


		// Rotation
		var camera = CameraEntity.Get<ThirdPersonCameraScript>();

		if (camera != null && (local.State.HasFlag(PlayerStateFlags.Aiming) || !local.State.HasFlag(PlayerStateFlags.Moving)))
		{
			// Face the camera's look-ahead target
			var lookDir = camera.Target - local.Position.ToStride();
			local.Yaw = MathF.Atan2(lookDir.X, lookDir.Z);
		}
		else if (intent != Vector3.Zero)
		{
			// Face movement direction; keep the old yaw when intent is zero
			local.Yaw = MathF.Atan2(intent.X, intent.Z);
		}
	}

	private Vector3 ComputeIntent()
	{
		var camRot = CameraEntity.Transform.Rotation;
		var forward = (camRot * -Vector3.UnitZ).FlattenY(); // Stride camera looks down -Z
		var right = (camRot * Vector3.UnitX).FlattenY();

		var direction = Vector3.Zero;
		if (Input.IsKeyDown(Keys.W)) direction += forward;
		if (Input.IsKeyDown(Keys.S)) direction -= forward;
		if (Input.IsKeyDown(Keys.D)) direction += right;
		if (Input.IsKeyDown(Keys.A)) direction -= right;

		return direction;

	}
}


