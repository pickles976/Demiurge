using System.Numerics;
using Demiurge;
using Demiurge.GameClient;
using Stride.Engine;
using Stride.Input;

public class LocalPlayerController : SyncScript
{
	public required Entity CameraEntity { get; init; }
	public required PlayerRegistry Registry { get; init; }
	public required TerrainState Terrain { get; init; }
	public required WeaponMount Mount { get; init; }
	public required LocalWeaponView WeaponView { get; init; }
	public required ClientInputState InputState { get; init; }
	public required SpawnReadiness Readiness { get; init; }

	private bool primaryWasDown;
	private uint? primedGrenadeId;

	/// <summary>How far down the line of sight to look for something to aim at.</summary>
	public float MaxAimDistance { get; set; } = 200f;

	/// <summary>
	/// Where the camera's line of sight lands — the point shots are aimed AT, and the point the
	/// reticle draws. Owned here rather than in <see cref="ReticleScript"/> so that the two cannot
	/// disagree: the reticle is a picture of this number, not a second opinion about it.
	///
	/// Never null while there is a camera, because a line of sight that meets nothing still has to
	/// aim somewhere — see <see cref="ComputeAimPoint"/>.
	/// </summary>
	public Vector3? AimPoint { get; private set; }

	/// <summary>
	/// The point to shoot at. Terrain if the line of sight meets any, otherwise a point far enough
	/// down it that the shot is near enough parallel to the view.
	///
	/// The fallback matters more than it looks: without it, aiming at the sky would have nothing to
	/// converge on, and the obvious repair — falling back to the camera's direction — is exactly the
	/// parallel-ray bug this method exists to avoid, reappearing only when you aim upward.
	/// </summary>
	private Vector3 ComputeAimPoint(Stride.Core.Mathematics.Vector3 eye, Stride.Core.Mathematics.Vector3 forward)
		=> TerrainRaycast.Cast(Terrain.Map, eye, forward, MaxAimDistance) is { } hit
			? hit.Point
			: (Vector3)(eye + forward * MaxAimDistance);

	public override void Update()
	{
		var local = Registry.LocalPlayer;
		if (local == null) return;   // not spawned yet
		if (local.IsDead)
		{
			primaryWasDown = false;
			primedGrenadeId = null;
			AimPoint = null;
			local.EnterDeath();
			return;
		}
		local.LeaveDeath();

		// Three reasons to stand the player down: the terminal has the keyboard, the fly camera has
		// the input, or the ground under the spawn has not finished meshing and walking would mean
		// walking through a world that is not there yet.
		//
		// All three keep SENDING a zero-intent move rather than going silent: when the server's move
		// queue starves it re-steps with the last intent it saw, forever (GameWorld.Tick), so a
		// player frozen mid-sprint would keep running server-side while the client stopped
		// predicting.
		if (InputState.TerminalOpen
			|| !Readiness.Ready
			|| CameraEntity.Get<DebugFlyCameraScript>()?.Active == true)
		{
			primaryWasDown = false;
			primedGrenadeId = null;
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
		HandleHotbarInput(local);

		// State. A reload takes the sight picture away whether or not the button is still held —
		// both hands are on the magazine. Working a bolt does NOT: the rifle stays on the shoulder
		// and the eye stays behind the sights, which is the whole reason a marksman can watch what
		// he just shot at.
		// Right-click means PLACE while the shovel is out (DigScript), so it must not also mean aim:
		// the tool has no sights to come up to, and the shared aim flag would still narrow the field
		// of view and halve the walk speed of somebody who was only building a wall.
		bool aiming = Input.IsMouseButtonDown(MouseButton.Right)
			&& !local.IsReloading
			&& local.Hotbar != HotbarSlot.Shovel;
		bool primaryDown = Input.IsMouseButtonDown(MouseButton.Left);

		// Shooting means "actuating the held item", which is why digging sets it too: it is the
		// replicated signal every other client's view reads to swing the shovel, and it is
		// cosmetic on the server (nothing gates on it), so widening it costs nothing.
		bool usingItem = primaryDown && (local.IsArmed || local.Hotbar == HotbarSlot.Shovel);

		local.State = local.State
			.With(PlayerStateFlags.Moving, intent != Vector3.Zero)
			.With(PlayerStateFlags.Sprinting, Input.IsKeyDown(Keys.LeftShift))
			.With(PlayerStateFlags.Aiming, aiming)
			.With(PlayerStateFlags.Crouching, Input.IsKeyDown(Keys.LeftCtrl))
			// Level-triggered on purpose: the shared step only acts on Jumping while grounded, so
			// holding Space jumps again the moment you land. It also sidesteps IsKeyPressed, which
			// re-fires on OS auto-repeat and is not a reliable one-shot for a held key.
			.With(PlayerStateFlags.Jumping, Input.IsKeyDown(Keys.Space))
			.With(PlayerStateFlags.Shooting, usingItem)
			.With(PlayerStateFlags.Reloading, local.IsReloading);

		// Rotation. The active look camera owns facing — you look where the camera looks. Turning
		// only while aiming or standing still, as the old cursor-aimed camera did, reads as the
		// character ignoring the mouse.
		var firstPerson = CameraEntity.Get<FirstPersonCameraScript>();
		if (firstPerson != null)
		{
			local.Yaw = firstPerson.Yaw;
			local.Pitch = firstPerson.Pitch;
		}
		else if (CameraEntity.Get<ShoulderCameraScript>() is { } shoulder)
		{
			local.Yaw = shoulder.Yaw;
			local.Pitch = shoulder.Pitch;
		}
		else if (intent != Vector3.Zero)
		{
			// Face movement direction; keep the old yaw when intent is zero
			local.Yaw = MathF.Atan2(intent.X, intent.Z);
		}

		local.Update(intent, (float)Game.UpdateTime.Elapsed.TotalSeconds);

		// Where the line of sight lands, resolved AFTER the rotation block so hip fire and ADS
		// both use this frame's camera rather than last frame's.
		var cameraTransform = CameraEntity.Transform;
		AimPoint = ComputeAimPoint(
			cameraTransform.Position,
			Stride.Core.Mathematics.Vector3.Transform(-Stride.Core.Mathematics.Vector3.UnitZ, cameraTransform.Rotation));

		// Automatic weapons remain level-triggered and let TryFire's cooldown produce their cadence;
		// a SEMI-AUTOMATIC one wants the trigger PRESS, so holding the button pays out exactly one
		// round. That distinction lives in WeaponConfig rather than here — see FireMode for why the
		// server enforces cadence and not the trigger edge. A grenade is primed while LMB is held
		// (the view uses Shooting for its pullback) and is thrown exactly once on release, provided
		// the same grenade is still equipped.
		//
		// A POINT, not a direction. The muzzle is not the camera, so a direction copied from the
		// camera would send the bullet parallel to the line of sight and never onto the reticle —
		// see TryFire. Hip fire uses this same centre point; its lower accuracy comes from spread.
		bool grenadeEquipped = local.Weapon?.Item.Type == ItemType.Grenade;
		if (grenadeEquipped)
		{
			if (primaryDown)
				primedGrenadeId ??= local.Weapon!.NetworkId;
			else if (primaryWasDown
			         && primedGrenadeId == local.Weapon!.NetworkId
			         && AimPoint is { } grenadeTarget)
				local.TryFire(grenadeTarget, Registry.RenderTick, FireOrigin(local));
		}
		else if (primedGrenadeId == null
		         && local.IsArmed
		         && TriggerPulled(local, primaryDown)
		         && AimPoint is { } weaponTarget)
		{
			local.TryFire(weaponTarget, Registry.RenderTick, FireOrigin(local));
		}

		if (!primaryDown) primedGrenadeId = null;
		primaryWasDown = primaryDown;

		if (Input.IsKeyPressed(Keys.R))
			local.TryReload();

		if (Input.IsKeyPressed(Keys.E))
			local.TryInteract();

	}

	/// <summary>
	/// Whether the trigger is asking for a shot this frame: held for an automatic, freshly pressed
	/// for a semi-automatic. The cooldown in TryFire still caps how fast presses pay out, so a
	/// player clicking faster than the weapon cycles gets the weapon's rate and nothing more.
	/// </summary>
	private bool TriggerPulled(LocalPlayer local, bool primaryDown)
		=> local.Stats.FireMode == FireMode.SemiAutomatic
			? primaryDown && !primaryWasDown
			: primaryDown;

	private void HandleHotbarInput(LocalPlayer local)
	{
		if (Input.IsKeyPressed(Keys.D1) || Input.IsKeyPressed(Keys.NumPad1))
			local.SelectHotbar(HotbarSlot.Primary);
		else if (Input.IsKeyPressed(Keys.D2) || Input.IsKeyPressed(Keys.NumPad2))
			local.SelectHotbar(HotbarSlot.Shovel);
		else if (Input.IsKeyPressed(Keys.D3) || Input.IsKeyPressed(Keys.NumPad3))
			local.SelectHotbar(HotbarSlot.Grenade);

		float wheel = Input.MouseWheelDelta;
		if (wheel > 0f)
			local.SelectHotbar(HotbarConfig.Scroll(local.Hotbar, -1));
		else if (wheel < 0f)
			local.SelectHotbar(HotbarConfig.Scroll(local.Hotbar, 1));
	}

	private Vector3 FireOrigin(LocalPlayer local)
	{
		if (WeaponView.MuzzleWorld is { } muzzle && WeaponView.NetworkId == local.Weapon?.NetworkId)
			return muzzle;

		var cameraTransform = CameraEntity.Transform;
		var type = local.Weapon!.Item.Type;
		float scale = ItemCosmetics.FirstPersonScale(type);
		var grip = type == ItemType.Grenade
			? WeaponMount.GrenadePullbackGripOffset
			: Mount.FirstPersonGripOffset(type, local.State.HasFlag(PlayerStateFlags.Aiming), scale);
		var offset = Mount.FirstPersonMuzzleOffset(type, grip, scale).ToStride();

		return (Vector3)(cameraTransform.Position
			+ Stride.Core.Mathematics.Vector3.Transform(offset, cameraTransform.Rotation));
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
