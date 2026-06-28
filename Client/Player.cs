using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;
using Stride.CommunityToolkit.Engine;

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


			// TODO: handle intent when we go to networking
			Vector3 intent = GenerateMovementIntent();
			PlayAnimations();
			UpdateTransform(intent, dt);
		
		}

		private Vector3 GenerateMovementIntent()
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
			if (!moving) return Vector3.Zero;

			direction.Normalize();

			float speed =
				(State & SlowingStates) != 0 ? SlowSpeed :
				State.HasFlag(PlayerStateFlags.Sprinting) ? SprintSpeed :
				Speed;

			
			// pos.Y = chunkMap.GetHeight(pos) + MeshHeightOffset;   // your terrain hook

			return direction * speed;

		}

		private void PlayAnimations()
		{
			var animationPlayer = Entity.GetComponent<AnimationComponent>();

			if (animationPlayer == null) return;

			if (State.HasFlag(PlayerStateFlags.Moving)) {
				if (!animationPlayer.IsPlaying("Walk")) {
    				animationPlayer.Play("Walk");
				}
			} 
			else
			{
				animationPlayer.Play("Idle");
			}
		}

		private void UpdateTransform(Vector3 intent, float dt)
		{

			Entity.Transform.Position = Entity.Transform.Position + intent * dt;


			if (State.HasFlag(PlayerStateFlags.Aiming) || !State.HasFlag(PlayerStateFlags.Moving))
			{
				if (CameraEntity == null) return;
				if (CameraEntity.GetComponent<ThirdPersonCameraScript>() == null) return;

				// TODO: fix this
				// Face towards Mouse
				var target = CameraEntity.GetComponent<ThirdPersonCameraScript>().Target;

				var lookDir = target - Entity.Transform.Position;

				float yaw = MathF.Atan2(lookDir.X, lookDir.Z);
				Entity.Transform.Rotation = Quaternion.RotationY(yaw);

			} else
			{
				// face movement direction (Rust: atan2(x, z))
				float yaw = MathF.Atan2(intent.X, intent.Z);
				Entity.Transform.Rotation = Quaternion.RotationY(yaw);
			}


		}

		private static Entity SpawnGun(Entity owner) => throw new NotImplementedException();


	}

}
