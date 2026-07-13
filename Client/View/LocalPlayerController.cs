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

		// State
		bool aiming = Input.IsMouseButtonDown(MouseButton.Right);

		local.State = local.State
			.With(PlayerStateFlags.Moving, intent != Vector3.Zero)
			.With(PlayerStateFlags.Sprinting, Input.IsKeyDown(Keys.LeftShift))
			.With(PlayerStateFlags.Aiming, aiming)
			.With(PlayerStateFlags.Crouching, Input.IsKeyDown(Keys.LeftCtrl))
			.With(PlayerStateFlags.Shooting, aiming && Input.IsMouseButtonDown(MouseButton.Left))
			.With(PlayerStateFlags.Reloading, local.IsReloading);

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

		local.Update(intent, (float)Game.UpdateTime.Elapsed.TotalSeconds);

		// After the rotation block. Holding LMB is level-triggered input, but TryFire's
		// cooldown gate turns it into one edge-triggered PlayerFire per shot — that's
		// where "hold to fire at 10/s" comes from. Aiming-only, so the fire direction
		// (the yaw the aim code just pointed at the cursor) is always meaningful.
		if (aiming && Input.IsMouseButtonDown(MouseButton.Left))
			local.TryFire(new Vector3(MathF.Sin(local.Yaw), 0f, MathF.Cos(local.Yaw)), Registry.RenderTick);

		if (Input.IsKeyPressed(Keys.R))
			local.TryReload();

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


