using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;
using Stride.CommunityToolkit.Engine;
using Stride.Animations;
using Stride.Rendering;

namespace Demiurge
{

	public class PlayerVisualScript : SyncScript
	{

		public PlayerStateFlags State;

		public const PlayerStateFlags SlowingStates =
			PlayerStateFlags.Crouching | PlayerStateFlags.Aiming |
			PlayerStateFlags.Shooting  | PlayerStateFlags.Reloading;

		public Entity? EquippedWeapon { get; private set; }


		private PlayingAnimation? CurrentAnimation { get; set; }

		public float AimBlendWeight { get; set; } = 1000f;
		private PlayingAnimation? _aimOverlay;

        public override void Update()
        {
			PlayAnimations();
		}

		public void SetPosition(Vector3 position)
		{
			Entity.Transform.Position = position;
		}

		private void UpdateTransform(Vector3 intent, float dt)
		{

			Entity.Transform.Position = Entity.Transform.Position + intent * dt;

			// TODO: access camera target
			// if (EquippedWeapon != null || !State.HasFlag(PlayerStateFlags.Moving))
			// {
			// 	// TODO: fix this
			// 	// Face towards Mouse
			// 	var target = CameraEntity.GetComponent<ThirdPersonCameraScript>().Target;

			// 	var lookDir = target - Entity.Transform.Position;

			// 	float yaw = MathF.Atan2(lookDir.X, lookDir.Z);
			// 	Entity.Transform.Rotation = Quaternion.RotationY(yaw);

			// } 
			// else
			// {
			// 	float yaw = MathF.Atan2(intent.X, intent.Z);
			// 	Entity.Transform.Rotation = Quaternion.RotationY(yaw);
			// }

		}

		private void PlayAnimations()
		{
			var anim = Entity.GetComponent<AnimationComponent>();

			if (anim == null) return;

			// ---- base locomotion layer ----
			// Must stay at index 0 so overlays blend on top of it. We only (re)create
			// it when the clip actually changes, otherwise it would restart every frame
			// and wipe the aiming overlay.
			string baseClip = State.HasFlag(PlayerStateFlags.Crouching) ? 
				(State.HasFlag(PlayerStateFlags.Moving) ? "CrouchWalk" : "Crouch") :
				(State.HasFlag(PlayerStateFlags.Moving) ? "Walk" : "Idle");

			bool baseMissing = CurrentAnimation == null || !anim.PlayingAnimations.Contains(CurrentAnimation);
			if (baseMissing || CurrentAnimation!.Name != baseClip)
			{
				if (CurrentAnimation != null) anim.PlayingAnimations.Remove(CurrentAnimation);

				CurrentAnimation = anim.Blend(baseClip, 1f, TimeSpan.Zero); // Blend(): adds without clearing
				CurrentAnimation.Weight = 1f;
				CurrentAnimation.RepeatMode = AnimationRepeatMode.LoopInfinite;

				// Blend() appends to the end; force the base back to the front.
				anim.PlayingAnimations.Remove(CurrentAnimation);
				anim.PlayingAnimations.Insert(0, CurrentAnimation);
			}

			// ---- aiming overlay ----
			// The Aiming clip only has arm channels. Stride normalizes blend weights per
			// bone, so a heavy AimBlendWeight makes the overlay dominate the arm bones it
			// shares with the base while leaving untouched bones at 100% locomotion.
			// Applied instantly (no fade) - on/off the moment aim state changes.
			if ((EquippedWeapon != null) && (_aimOverlay == null || !anim.PlayingAnimations.Contains(_aimOverlay)))
			{
				_aimOverlay = anim.Blend("Aiming", AimBlendWeight, TimeSpan.Zero);
				_aimOverlay.BlendOperation = AnimationBlendOperation.LinearBlend;
				_aimOverlay.RepeatMode = AnimationRepeatMode.LoopInfinite;
				_aimOverlay.Weight = AimBlendWeight;
			}
			else if (EquippedWeapon == null && _aimOverlay != null)
			{
				anim.PlayingAnimations.Remove(_aimOverlay);
				_aimOverlay = null;
			}

			// ---- playback speed (applies to the locomotion layer) ----
			if ((State & SlowingStates) != 0)
			{
				CurrentAnimation!.TimeFactor = 0.75f;
			} else if (State.HasFlag(PlayerStateFlags.Sprinting))
			{
				CurrentAnimation!.TimeFactor = 2.0f;
			} else
			{
				CurrentAnimation!.TimeFactor = 1.0f;
			}

		}

		private void SpawnGun(Entity owner)
		{
			if (EquippedWeapon != null)
			{
				EquippedWeapon.SetParent(null);
				EquippedWeapon.Scene = null;
				EquippedWeapon = null;
				return;
			}
			; // already holding one

			var ak = new Entity("AK47") { 
				new ModelComponent(Content.Load<Model>("models/ak47")),
				new GunScript { PlayerEntity = Entity }
			};

			// Link the gun to the owner's "right_hand" bone. The ModelNodeLinkProcessor
			// drives the gun's world transform from the bone each frame (after skinning),
			// so it follows the hand animation. Transform.Position/Rotation act as a local
			// offset relative to the bone, not the owner's root.
			var link = ak.GetOrCreate<ModelNodeLinkComponent>();
			link.Target = owner.GetComponent<ModelComponent>();
			link.NodeName = "right_hand";

			// Vec3::new(0.0, 1.5 / 16.0, 14.75 / 16.0) barrel
			// Vec3::new(0.0f, -0.425f / 16.0f, 4.75f / 16.0f)
			// 4.75f / 16.0f

			owner.AddChild(ak); // keeps it in the scene and tied to the owner's lifecycle
			ak.Transform.Position = new Vector3(0.0f, -3.75f / 16.0f, 0.425f / 16.0f); // tweak to seat the grip in the hand
			ak.Transform.Rotation = Quaternion.RotationX(MathF.PI / 2.0f) * Quaternion.RotationZ(MathF.PI);

			EquippedWeapon = ak;
		}


	}

}
