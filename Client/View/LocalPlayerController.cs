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

		// The debug fly camera has the input; stand the player down. Note this keeps SENDING a
		// zero-intent move every tick rather than going silent: when the server's move queue
		// starves it re-steps with the last intent it saw, forever (GameWorld.Tick), so a player
		// frozen mid-sprint would keep running server-side while the client stopped predicting.
		if (CameraEntity.Get<DebugFlyCameraScript>()?.Active == true)
		{
			local.State = local.State
				.With(PlayerStateFlags.Moving, false)
				.With(PlayerStateFlags.Sprinting, false)
				.With(PlayerStateFlags.Aiming, false)
				.With(PlayerStateFlags.Crouching, false)
				.With(PlayerStateFlags.Jumping, false)
				.With(PlayerStateFlags.Shooting, false)
				.With(PlayerStateFlags.Reloading, local.IsReloading);   // let an in-flight reload finish

			local.Update(Vector3.Zero, (float)Game.UpdateTime.Elapsed.TotalSeconds);
			return;
		}

		// Position
		var intent = ComputeIntent();   // the WASD + camera-flatten math you already have

		// State
		bool aiming = Input.IsMouseButtonDown(MouseButton.Right);

		local.State = local.State
			.With(PlayerStateFlags.Moving, intent != Vector3.Zero)
			.With(PlayerStateFlags.Sprinting, Input.IsKeyDown(Keys.LeftShift))
			.With(PlayerStateFlags.Aiming, aiming)
			.With(PlayerStateFlags.Crouching, Input.IsKeyDown(Keys.LeftCtrl))
			// Level-triggered on purpose: the shared step only acts on Jumping while grounded, so
			// holding Space jumps again the moment you land. It also sidesteps IsKeyPressed, which
			// re-fires on OS auto-repeat and is not a reliable one-shot for a held key.
			.With(PlayerStateFlags.Jumping, Input.IsKeyDown(Keys.Space))
			.With(PlayerStateFlags.Shooting, aiming && Input.IsMouseButtonDown(MouseButton.Left))
			.With(PlayerStateFlags.Reloading, local.IsReloading);

		// Rotation. The shoulder camera OWNS facing — you look where the camera looks, which is what
		// makes an over-the-shoulder camera feel like one. Turning only while aiming or standing still,
		// as the old cursor-aimed camera did, reads as the character ignoring the mouse.
		var shoulder = CameraEntity.Get<ShoulderCameraScript>();
		if (shoulder != null)
		{
			local.Yaw = shoulder.Yaw;
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

		if (Input.IsKeyPressed(Keys.E))
			local.TryInteract();

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


