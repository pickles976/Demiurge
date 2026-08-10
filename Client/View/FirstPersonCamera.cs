using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge
{
	/// <summary>
	/// Snappy first-person camera: locked mouse, immediate yaw/pitch, and only a tight positional
	/// chase to hide the 30 Hz prediction steps under the render frame rate.
	/// </summary>
	public class FirstPersonCameraScript : SyncScript
	{
		/// <summary>Radians per pixel of mouse movement.</summary>
		public float LookSensitivity { get; set; } = 0.003f;

		/// <summary>ADS mouse multiplier, lower like the CoD family without adding camera lag.</summary>
		public float AimSensitivityMultiplier { get; set; } = 0.72f;

		/// <summary>Just short of straight up or down, so the up vector never degenerates.</summary>
		const float PitchLimit = MathUtil.PiOverTwo - 0.01f;

		/// <summary>Eye height above the player's feet. Matches the shared digging reach origin.</summary>
		public float EyeHeight { get; set; } = Digging.EyeHeight;

		/// <summary>How much the view drops while crouching.</summary>
		public float CrouchEyeDrop { get; set; } = PlayerMovement.CrouchEyeDrop;

		/// <summary>How much the view drops while prone.</summary>
		public float ProneEyeDrop { get; set; } = PlayerMovement.ProneEyeDrop;

		/// <summary>
		/// The camera follows predicted movement with this sharpness. Rotation stays unsmoothed; this
		/// is only to keep fixed-tick movement from reading as visible frame stepping.
		/// </summary>
		public float FollowSharpness { get; set; } = 35f;

		/// <summary>How quickly crouch eye height and ADS FOV settle.</summary>
		public float StateSharpness { get; set; } = 22f;

		public float HipFieldOfView { get; set; } = 74f;
		public float AimFieldOfView { get; set; } = 56f;
		public float SprintFieldOfView { get; set; } = 78f;

		public required PlayerRegistry Registry { get; init; }
		public required ClientInputState InputState { get; init; }

		/// <summary>
		/// Player yaw convention: zero faces +Z, produced from the actual camera forward vector.
		/// </summary>
		public float Yaw { get; private set; }

		/// <summary>Look angle above the horizon, radians, positive is up.</summary>
		public float Pitch => pitch;

		float orbit;
		float pitch;
		float eyeHeight;
		Vector3 followed;
		bool following;
		bool seededRotation;
		bool mouseLocked;
		CameraComponent? camera;

		public override void Start()
		{
			camera = Entity.Get<CameraComponent>();
			eyeHeight = EyeHeight;

			if (camera != null)
			{
				camera.VerticalFieldOfView = HipFieldOfView;
				camera.NearClipPlane = 0.03f;
			}
		}

		public override void Update()
		{
			// Every path below can bail out before the camera is posed; trauma has to keep bleeding
			// regardless, or a grenade that went off while the terminal was open is still waiting at
			// full strength when it closes.
			CameraTrauma.Decay((float)Game.UpdateTime.Elapsed.TotalSeconds);

			if (InputState.TerminalOpen)
			{
				if (mouseLocked) Input.UnlockMousePosition();
				Game.IsMouseVisible = true;
				mouseLocked = false;
				return;
			}

			if (Entity.Get<DebugFlyCameraScript>()?.Active == true)
			{
				mouseLocked = false;
				return;
			}

			if (Registry.LocalPlayer is not { } local) return;
			if (local.IsDead)
			{
				// Hand the pointer back for the same reason the mortar does below: the killcam is
				// not aimed, and the loadout picker in front of it is clicked. Without this the
				// cursor stays locked to the centre from the moment of death and the cards can only
				// be reached with the number keys.
				if (mouseLocked) Input.UnlockMousePosition();
				Game.IsMouseVisible = true;
				mouseLocked = false;
				following = false;
				return;
			}

			// On an emplacement, MortarControlScript owns the view. Hand back the pointer with it:
			// that camera is aimed with the cursor, so a locked mouse would leave the gunner unable
			// to lay the weapon he is standing at.
			if (local.IsOperating)
			{
				if (mouseLocked) Input.UnlockMousePosition();
				Game.IsMouseVisible = true;
				mouseLocked = false;
				following = false;
				return;
			}

			if (!seededRotation)
			{
				orbit = local.Yaw + MathF.PI;
				pitch = local.Pitch;
				seededRotation = true;
			}

			if (!mouseLocked)
			{
				Input.LockMousePosition(forceCenter: true);
				Game.IsMouseVisible = false;
				mouseLocked = true;
			}

			// Not while the shovel is out: there are no sights on a spade to bring up. Same
			// exclusion LocalPlayerController applies to the replicated Aiming flag.
			bool aiming = Input.IsMouseButtonDown(MouseButton.Right) && local.Hotbar != HotbarSlot.Shovel;
			float sensitivity = LookSensitivity * (aiming ? AimSensitivityMultiplier : 1f);

			var look = Input.AbsoluteMouseDelta;
			orbit -= look.X * sensitivity;
			pitch = MathUtil.Clamp(pitch - look.Y * sensitivity, -PitchLimit, PitchLimit);

			// Shake is applied to the RENDERED rotation only. Yaw/Pitch below are read back by the
			// controller as where the player is aiming, and a shot that landed where the shake threw
			// the camera rather than where the player pointed it would feel like the game cheating.
			var aim = Quaternion.RotationX(pitch) * Quaternion.RotationY(orbit);
			var rotation = CameraTrauma.Update((float)Game.UpdateTime.Elapsed.TotalSeconds) * aim;

			var feet = local.Position.ToStride();
			float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;

			if (!following || Vector3.Distance(followed, feet) > 8f)
			{
				followed = feet;
				following = true;
			}
			else
			{
				followed = Vector3.Lerp(followed, feet, SharpStep(FollowSharpness, dt));
			}

			float targetEye = EyeHeight - (local.State.HasFlag(PlayerStateFlags.Prone)
				? ProneEyeDrop
				: local.State.HasFlag(PlayerStateFlags.Crouching) ? CrouchEyeDrop : 0f);
			eyeHeight = MathUtil.Lerp(eyeHeight, targetEye, SharpStep(StateSharpness, dt));

			Entity.Transform.Position = followed + Vector3.UnitY * eyeHeight;
			Entity.Transform.Rotation = rotation;

			var forward = Vector3.Transform(-Vector3.UnitZ, aim);
			Yaw = MathF.Atan2(forward.X, forward.Z);

			if (camera != null)
			{
				float targetFov =
					aiming ? AimFieldOfViewFor(local.Weapon?.Item.Type) :
					local.State.HasFlag(PlayerStateFlags.Sprinting) ? SprintFieldOfView :
					HipFieldOfView;
				// Paced by the held weapon, unlike the crouch blend above: coming up to the sights is
				// a property of what is in your hands, and this rate has to match the view model's in
				// ItemAttachScript or the gun arrives at the sights before the view does.
				float aimSharpness = StateSharpness
					* (local.Weapon?.Item.Type is { } held ? ItemCosmetics.AimSpeedScale(held) : 1f);
				camera.VerticalFieldOfView = MathUtil.Lerp(camera.VerticalFieldOfView, targetFov, SharpStep(aimSharpness, dt));
			}
		}

		/// <summary>
		/// The ADS field of view for the weapon in hand, narrowed by whatever optic it carries.
		///
		/// Magnification divides the TANGENT, not the angle: halving 56 degrees would be 2x only for
		/// a narrow view, and the error grows with the field. Doing it properly is what makes "2x"
		/// mean a target subtends twice the screen height, which is the thing a player can check.
		/// </summary>
		float AimFieldOfViewFor(ItemType? held)
		{
			float magnification = held is { } type ? ItemCosmetics.AimMagnification(type) : 1f;
			if (magnification <= 1f) return AimFieldOfView;

			float halfTangent = MathF.Tan(MathUtil.DegreesToRadians(AimFieldOfView) * 0.5f);
			return MathUtil.RadiansToDegrees(2f * MathF.Atan(halfTangent / magnification));
		}

		static float SharpStep(float sharpness, float dt)
			=> 1f - MathF.Exp(-sharpness * dt);
	}
}
