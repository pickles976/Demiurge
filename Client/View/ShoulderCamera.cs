using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge
{
	/// <summary>
	/// Over-the-shoulder third-person camera, the Gears/DayZ arrangement: close behind the player,
	/// looking where they look, offset to one side so the body does not cover the middle of the screen.
	/// Aiming pulls in tighter and closer to the shoulder.
	///
	/// Replaces <see cref="ThirdPersonCameraScript"/>, which is kept as dead code. That one is a
	/// different game: high and far back, aimed with the mouse CURSOR against a look-ahead point, so the
	/// mouse stays visible and the camera never rotates with the player. The two cannot share code
	/// meaningfully — this one owns the player's facing rather than following it.
	/// </summary>
	public class ShoulderCameraScript : SyncScript
	{
		/// <summary>Radians per pixel of mouse movement.</summary>
		public float LookSensitivity { get; set; } = 0.003f;

		/// <summary>Just short of straight up or down, so the up vector never degenerates.</summary>
		const float PitchLimit = 1.45f;

		/// <summary>Pivot height above the player's FEET — roughly the head.</summary>
		public float PivotHeight { get; set; } = 1.55f;

		// Hip-fire: far enough back to see the character and some surroundings.
		public float Distance { get; set; } = 3.4f;
		public float ShoulderOffset { get; set; } = 0.65f;

		// Aiming: closer and tighter in, which is what makes aiming feel like leaning into the shot.
		public float AimDistance { get; set; } = 1.7f;
		public float AimShoulderOffset { get; set; } = 0.5f;

		/// <summary>
		/// How quickly the camera settles into a changed distance. Position is smoothed but ROTATION is
		/// not: a camera that lags the mouse feels broken, whereas one that eases its distance while
		/// zooming feels intentional.
		/// </summary>
		public float ZoomSpeed { get; set; } = 12f;

		/// <summary>
		/// How sharply the pivot chases the player. MUST match PlayerViewScript's smoothing of the local
		/// player's model, and that is not a nicety.
		///
		/// Prediction advances in 30 Hz steps while rendering runs at whatever the frame rate is. The
		/// model already eases between those steps; a camera reading the raw predicted position instead
		/// snaps to each one, so the model oscillates AGAINST the camera every frame and the character
		/// looks like it is vibrating. Following with the same curve makes the two move as one — any
		/// residual lag is then shared, and shared lag is invisible.
		/// </summary>
		public float FollowSharpness { get; set; } = 20f;

		public required PlayerRegistry Registry { get; init; }

		/// <summary>
		/// Where the camera is looking, on the ground plane, in the PLAYER's yaw convention — the one
		/// <c>atan2(direction.X, direction.Z)</c> produces, where zero faces +Z.
		///
		/// Not the same as the camera's own orbit angle, and the difference is 180 degrees: camera space
		/// looks down -Z, so an orbit of zero points the camera at -Z while a player yaw of zero faces
		/// +Z. Handing the raw orbit angle to the player turns them to face the camera. Derived from the
		/// forward VECTOR rather than by adding pi, so it stays correct if the rotation composition
		/// changes.
		/// </summary>
		public float Yaw { get; private set; }

		float orbit;
		float pitch;
		Vector3 followed;
		bool following;
		float distance;
		float shoulder;
		bool mouseLocked;

		public override void Start()
		{
			distance = Distance;
			shoulder = ShoulderOffset;
		}

		public override void Update()
		{
			// The debug fly camera owns the transform while it is detached; do not fight it for the
			// entity, and do not hold the mouse captive either.
			if (Entity.Get<DebugFlyCameraScript>()?.Active == true)
			{
				mouseLocked = false;
				return;
			}

			if (Registry.LocalPlayer is not { } local) return;

			if (!mouseLocked)
			{
				Input.LockMousePosition(forceCenter: true);
				Game.IsMouseVisible = false;
				mouseLocked = true;
			}

			// AbsoluteMouseDelta, not MouseDelta: MouseDelta divides X by window width and Y by window
			// height separately, so a square hand movement comes out as a rectangle.
			var look = Input.AbsoluteMouseDelta;
			orbit -= look.X * LookSensitivity;
			pitch = MathUtil.Clamp(pitch - look.Y * LookSensitivity, -PitchLimit, PitchLimit);

			bool aiming = local.State.HasFlag(PlayerStateFlags.Aiming);
			float step = MathUtil.Clamp((float)Game.UpdateTime.Elapsed.TotalSeconds * ZoomSpeed, 0f, 1f);

			distance = MathUtil.Lerp(distance, aiming ? AimDistance : Distance, step);
			shoulder = MathUtil.Lerp(shoulder, aiming ? AimShoulderOffset : ShoulderOffset, step);

			// Stride multiplies as "apply a, THEN b", so this is pitch in the local frame followed by
			// yaw about world Y. The other order picks up roll as soon as you look up while turning.
			var rotation = Quaternion.RotationX(pitch) * Quaternion.RotationY(orbit);

			// Camera space looks down -Z, so the rig sits at +Z to end up BEHIND the pivot, pushed to
			// +X for the shoulder.
			var offset = new Vector3(shoulder, 0f, distance);
			Vector3.Transform(ref offset, ref rotation, out var rotated);

			var feet = local.Position.ToStride();

			// Snap on the first frame and after a teleport, ease otherwise: easing in from wherever the
			// camera happened to be would sweep it across the world on spawn.
			if (!following || Vector3.Distance(followed, feet) > 8f)
			{
				followed = feet;
				following = true;
			}
			else
			{
				followed = Vector3.Lerp(followed, feet,
					1f - MathF.Exp(-FollowSharpness * (float)Game.UpdateTime.Elapsed.TotalSeconds));
			}

			var pivot = followed + Vector3.UnitY * PivotHeight;

			Entity.Transform.Position = pivot + rotated;
			Entity.Transform.Rotation = rotation;

			var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
			Yaw = MathF.Atan2(forward.X, forward.Z);
		}
	}
}
