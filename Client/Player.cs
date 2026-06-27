using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;

namespace Demiurge
{

	[Flags]
	public enum PlayerStateFlags
	{
		None      = 0,
		Moving    = 1 << 0,
		Sprinting = 1 << 1,
		Crouching = 1 << 2,
		Jumping   = 1 << 3,
		Aiming    = 1 << 4,
		Shooting  = 1 << 5,
		Reloading = 1 << 6,
	}

	public static class PlayerStateFlagExtensions
	{
		public static PlayerStateFlags With(this PlayerStateFlags flags, PlayerStateFlags flag, bool on)
          => on ? flags | flag : flags & ~flag;
	}


	public class PlayerScript : SyncScript
	{
		public float Speed       { get; set; } = 2f;
		public float SlowSpeed   { get; set; } = 1f;
		public float SprintSpeed { get; set; } = 3f;

		// ---- runtime state ----
		public PlayerStateFlags State { get; private set; }

		private const PlayerStateFlags SlowingStates =
			PlayerStateFlags.Crouching | PlayerStateFlags.Aiming |
			PlayerStateFlags.Shooting  | PlayerStateFlags.Reloading;

    	public Entity? EquippedWeapon { get; private set; }

		public required Entity CameraEntity { get; set; }

        public override void Update()
        {
        
			var dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;

			State = State
				.With(PlayerStateFlags.Sprinting, Input.IsKeyDown(Keys.LeftShift))
				.With(PlayerStateFlags.Aiming, Input.IsMouseButtonDown(MouseButton.Right));

			MovePlayer(dt);
			// DrawAimLine();
		
		}

		private void MovePlayer(float dt)
		{
			var camRot = CameraEntity.Transform.Rotation;
			var forward = MathExtensions.FlattenY(camRot * -Vector3.UnitZ); // Stride camera looks down -Z
			var right   = MathExtensions.FlattenY(camRot *  Vector3.UnitX);

			var direction = Vector3.Zero;
			if (Input.IsKeyDown(Keys.W)) direction += forward;
			if (Input.IsKeyDown(Keys.S)) direction -= forward;
			if (Input.IsKeyDown(Keys.D)) direction += right;
			if (Input.IsKeyDown(Keys.A)) direction -= right;

			bool moving = direction.LengthSquared() > 0f;
			State = State.With(PlayerStateFlags.Moving, moving);
			if (!moving) return;

			direction.Normalize();

			float speed =
				(State & SlowingStates) != 0 ? SlowSpeed :
				State.HasFlag(PlayerStateFlags.Sprinting) ? SprintSpeed :
				Speed;

			var pos = Entity.Transform.Position + direction * speed * dt;
			// pos.Y = chunkMap.GetHeight(pos) + MeshHeightOffset;   // your terrain hook
			Entity.Transform.Position = pos;

			// face movement direction (Rust: atan2(x, z))
			float yaw = MathF.Atan2(direction.X, direction.Z);
			Entity.Transform.Rotation = Quaternion.RotationY(yaw);
		}

		// private void DrawAimLine()
		// {
		// 	if (State.HasFlag(PlayerStateFlags.Aiming))
		// 	{
		// 		var cursor = ScreenToCentered(Input.MousePosition); // your 2D convention
		// 		LineRenderer.DrawPoint2D(cursor, Color.White, size: 10f);            // draw_reticle
		// 		LineRenderer.DrawLine2D(barrelScreenPos, cursor, Color.White);       // draw_aim_line
		// 	}

		// }

		private static Entity SpawnGun(Entity owner) => throw new NotImplementedException();


	}

}
