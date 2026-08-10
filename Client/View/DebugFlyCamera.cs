using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge
{

	/// <summary>
	/// Debug free-fly camera. F3 detaches the camera from the follow rig and flies it on
	/// WASD + mouse look; F3 again hands control back to <see cref="FirstPersonCameraScript"/>,
	/// which re-derives its pose from the player — so re-entering always starts fresh from the
	/// follow position rather than wherever this was left.
	///
	/// This script arbitrates the whole mode: the follow cam, the player controller and the
	/// reticle each early-return on <see cref="Active"/>. That indirection isn't stylistic —
	/// Stride's ScriptComponent derives from EntityComponent, NOT ActivableEntityComponent, so
	/// there is no Enabled switch to turn a script off, and ScriptSystem schedules every
	/// registered script unconditionally. A flag the others read is the only way to stand
	/// them down.
	/// </summary>
	public class DebugFlyCameraScript : SyncScript
	{
		public required ClientInputState InputState { get; init; }

		public float Speed { get; set; } = 15.0f;

		public float BoostMultiplier { get; set; } = 4.0f;

		/// <summary>Radians of rotation per pixel of mouse movement.</summary>
		public float LookSensitivity { get; set; } = 0.004f;

		/// <summary>True while the camera is detached. Read by the scripts this mode stands down.</summary>
		public bool Active { get; private set; }

		private float yaw;
		private float pitch;
		private bool toggleWasDown;
		private bool mouseLocked;

		// Just short of straight up/down, so forward never degenerates.
		private const float PitchLimit = MathUtil.PiOverTwo - 0.01f;

		public override void Start() { }

		public override void Update()
		{
			// Edge-detect by hand rather than using IsKeyPressed: KeyboardSDL.OnKeyEvent never
			// checks e.Repeat, so IsKeyPressed re-fires on OS key auto-repeat and holding F3
			// would strobe the mode on and off.
			var toggleDown = Input.IsKeyDown(Keys.F3);
			if (!InputState.TerminalOpen && toggleDown && !toggleWasDown) Toggle();
			toggleWasDown = toggleDown;

			if (!Active) return;
			if (InputState.TerminalOpen)
			{
				if (mouseLocked) Input.UnlockMousePosition();
				mouseLocked = false;
				return;
			}

			if (!mouseLocked)
			{
				Input.LockMousePosition(forceCenter: true);
				Game.IsMouseVisible = false;
				mouseLocked = true;
			}

			Look();
			Move((float)Game.UpdateTime.Elapsed.TotalSeconds);
		}

		private void Toggle()
		{
			Active = !Active;

			if (Active)
			{
				// ADS and sprint both alter the gameplay FOV. Freecam is an inspection view, so it
				// always starts from the configured hip lens rather than inheriting whichever zoom
				// happened to be active on the frame F3 was pressed.
				if (Entity.Get<CameraComponent>() is { } camera
					&& Entity.Get<FirstPersonCameraScript>() is { } firstPerson)
					camera.VerticalFieldOfView = firstPerson.HipFieldOfView;

				// Seed from wherever the follow cam left the camera, so detaching is invisible —
				// the view just stops following. Taken from the forward vector because that
				// inverts exactly the composition Look() applies below.
				var forward = Entity.Transform.Rotation * -Vector3.UnitZ;
				pitch = MathF.Asin(MathUtil.Clamp(forward.Y, -1.0f, 1.0f));
				yaw = MathF.Atan2(-forward.X, -forward.Z);

				Input.LockMousePosition(forceCenter: true);
				Game.IsMouseVisible = false;
				mouseLocked = true;
			}
			else
			{
				Input.UnlockMousePosition();
				mouseLocked = false;
			}
		}

		private void Look()
		{
			// AbsoluteMouseDelta, not MouseDelta: MouseDelta divides X by window width and Y by
			// window height SEPARATELY, so on a non-square window a diagonal move comes out skewed.
			var delta = Input.AbsoluteMouseDelta;
			yaw -= delta.X * LookSensitivity;
			pitch -= delta.Y * LookSensitivity;
			pitch = MathUtil.Clamp(pitch, -PitchLimit, PitchLimit);

			// Rebuilt from the two angles every frame instead of accumulated, so roll can't drift.
			// Stride's quaternion product is Hamilton with the operands swapped — `a * b` means
			// "apply a, THEN b" — so this reads as pitch in the local frame, then yaw about world Y.
			// Written the conventional way round it would roll as soon as you look around while turned.
			Entity.Transform.Rotation = Quaternion.RotationX(pitch) * Quaternion.RotationY(yaw);
		}

		private void Move(float dt)
		{
			var rotation = Entity.Transform.Rotation;
			var forward = rotation * -Vector3.UnitZ;   // Stride cameras look down -Z
			var right = rotation * Vector3.UnitX;

			var direction = Vector3.Zero;
			if (Input.IsKeyDown(Keys.W)) direction += forward;
			if (Input.IsKeyDown(Keys.S)) direction -= forward;
			if (Input.IsKeyDown(Keys.D)) direction += right;
			if (Input.IsKeyDown(Keys.A)) direction -= right;
			if (Input.IsKeyDown(Keys.Space)) direction += Vector3.UnitY;
			if (Input.IsKeyDown(Keys.LeftCtrl)) direction -= Vector3.UnitY;

			if (direction == Vector3.Zero) return;
			direction.Normalize();

			var speed = Input.IsKeyDown(Keys.LeftShift) ? Speed * BoostMultiplier : Speed;
			Entity.Transform.Position += direction * speed * dt;
		}
	}

}
