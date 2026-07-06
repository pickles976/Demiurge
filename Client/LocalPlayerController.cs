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

		var intent = ComputeIntent();   // the WASD + camera-flatten math you already have

		local.State = local.State
			.With(PlayerStateFlags.Moving, intent != Vector3.Zero)
			.With(PlayerStateFlags.Sprinting, Input.IsKeyDown(Keys.LeftShift))
			.With(PlayerStateFlags.Aiming, Input.IsMouseButtonDown(MouseButton.Right));

		local.Update(intent, (float)Game.UpdateTime.Elapsed.TotalSeconds);
	}

	private Vector3 ComputeIntent()
	{
		var camRot = CameraEntity.Transform.Rotation;
		var forward = MathExtensions.FlattenY(camRot * -Vector3.UnitZ); // Stride camera looks down -Z
		var right = MathExtensions.FlattenY(camRot * Vector3.UnitX);

		var direction = Vector3.Zero;
		if (Input.IsKeyDown(Keys.W)) direction += forward;
		if (Input.IsKeyDown(Keys.S)) direction -= forward;
		if (Input.IsKeyDown(Keys.D)) direction += right;
		if (Input.IsKeyDown(Keys.A)) direction -= right;

		return direction;

	}
}


