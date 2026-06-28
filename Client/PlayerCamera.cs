using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Sprites;
using Stride.UI.Controls;
using Stride.UI.Panels;
using Texture = Stride.Graphics.Texture;
using PixelFormat = Stride.Graphics.PixelFormat;
using Stride.CommunityToolkit.Engine;
using SharpGLTF.Schema2;

namespace Demiurge
{

	public class ThirdPersonCameraScript : SyncScript
	{
		// Camera Offset
		public float Height { get; set; } = 10.0f;

		public float Radius { get; set; } = 5.0f;

		public float Angle { get; set; } = 0.0f; // Rotation angle

		// Aiming Look-ahead
		public required Entity PlayerEntity {get; set;}
		public Vector3 Target { get; set; } = new Vector3(0, 0, 0);

		public float LookAheadRadiusFar { get; set; } = AppConstants.LookAheadRadiusFar;
		public float LookAheadRadiusNear { get; set; } = AppConstants.LookAheadRadiusNear;

		public float RotationSpeed { get; set; } = 20.0f;
		public Vector2 PreviousMousePosition { get; set; } = new Vector2(0, 0);

		public float LerpSpeed { get; set; } = 0.05f;

		public override void Start()
		{
			// Input.LockMousePosition(forceCenter: true);
			Game.IsMouseVisible = false;
		}

		public override void Update()
		{
			var dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
			UpdateCameraTransform(dt);
			DrawAimLine();
		}

		private void UpdateCameraTransform(float dt)
		{
			// Center on (0.5, 0.5) so the angle is measured around the screen center,
			// and flip Y so the axis matches Bevy's top-left/Y-down cursor convention.
			var centered = Input.MousePosition - new Vector2(0.5f, 0.5f);
			var mousePos = new Vector2(centered.X, centered.Y);

			// Calculate angular mouse displacement while middle mouse button held
			if (Input.IsMouseButtonDown(MouseButton.Middle)) {

				var startVector = PreviousMousePosition;
				startVector.Normalize();

				var endVector = mousePos;
				endVector.Normalize();

				var cross = MathExtensions.Wedge(startVector, endVector);
				var dot = Vector2.Dot(startVector, endVector);
				var angularDisplacement = MathF.Atan2(dot, cross) - MathF.PI / 2.0f;

				Angle += angularDisplacement * dt * RotationSpeed;
			}

			// Figure out where to set the look-ahead position
			var lookaheadOffset = new Vector2(Game.Window.ClientBounds.Width * mousePos.X, Game.Window.ClientBounds.Height * mousePos.Y);
			var distance = lookaheadOffset.Length();

			// Aiming vs non-aiming lookahead
			var playerScript = PlayerEntity.GetComponent<PlayerScript>();
			var scale = playerScript?.State.HasFlag(PlayerStateFlags.Aiming) switch
			{
				false => 
					MathExtensions.Step(
						MathExtensions.Remap(distance, AppConstants.LookAheadRadiusNear, AppConstants.LookAheadRadiusFar, AppConstants.ShiftNear, AppConstants.ShiftFar), 
						AppConstants.ShiftNear, AppConstants.ShiftFar),
				true => 
					MathExtensions.Step(
						MathExtensions.Remap(distance, AppConstants.LookAheadRadiusNear, AppConstants.LookAheadRadiusFar, AppConstants.AimingShiftNear, AppConstants.AimingShiftFar), 
						AppConstants.AimingShiftNear, AppConstants.AimingShiftFar),
				null => 0.0f
			};

			lookaheadOffset.Normalize();
			var norm = lookaheadOffset * scale;

			// Convert 2D mouse offset into 3D target offset
			var offset = new Vector3(norm.X, 0.0f, norm.Y);
			var rigRotation = Quaternion.RotationY(Angle);
			Vector3.Transform(ref offset, ref rigRotation, out var rotatedOffset);
			Target = PlayerEntity.Transform.Position + rotatedOffset;
			PreviousMousePosition = mousePos;

			// FollowCamera::get_desired_camera_position()
			var forward = Vector3.UnitZ;
			Vector3.Transform(ref forward, ref rigRotation, out var rotatedForward);
			var desiredPosition = Target + rotatedForward * Radius + Vector3.UnitY *
			Height;

			// LookAtRH gives a view (world->camera) matrix; invert it to get the camera's world rotation
			var lookAt = Matrix.LookAtRH(desiredPosition, Target, Vector3.UnitY);
			lookAt.Invert();
			var desiredRotation = Quaternion.RotationMatrix(lookAt);

			// Position is lerped, rotation is set directly — exactly like the Bevy version
			Entity.Transform.Position = Vector3.Lerp(Entity.Transform.Position,
			desiredPosition, LerpSpeed);
			Entity.Transform.Rotation = desiredRotation;

		}

		private void DrawAimLine()
		{

			if (PlayerEntity == null) return;

			if (PlayerEntity.GetComponent<PlayerScript>().State.HasFlag(PlayerStateFlags.Aiming))
			{
				var cursor = MathExtensions.MousePosToScreenCoords(Input.MousePosition, Game.Window.ClientBounds);
				var barrelScreenPos = MathExtensions.WorldToScreen(PlayerEntity.Transform.Position, Entity.GetComponent<CameraComponent>().ViewProjectionMatrix, Game.Window.ClientBounds);
				Console.WriteLine(Input.MousePosition);
				LineRenderer.DrawLine2D(barrelScreenPos, cursor, Color.White);       // draw_aim_line
			}

		}
	}

	// Draws a ring at the cursor position, like Bevy's draw_reticle gizmos.
	public class CursorReticleScript : SyncScript
	{
		public override void Start(){}
		public override void Update()
		{
			LineRenderer.Circle2D(MathExtensions.MousePosToScreenCoords(Input.MousePosition, Game.Window.ClientBounds), 2f, Color.White);
		}
	}

	// Draws the near/far aim look-ahead radii (and a center marker) as screen-space
	// rings, like the Bevy draw_screen_space_aim_radii gizmos. Uses the immediate-mode
	// LineRenderer, so the rings are re-issued every frame.
	public class LookaheadDebugScript : SyncScript
	{
		public override void Start() { }

		public override void Update()
		{
			// 2D space is pixels centered on the screen, matching Bevy's Isometry2d::IDENTITY.
			LineRenderer.Circle2D(Vector2.Zero, AppConstants.LookAheadRadiusNear, Color.White);
			LineRenderer.Circle2D(Vector2.Zero, AppConstants.LookAheadRadiusFar, Color.White);
			LineRenderer.Circle2D(Vector2.Zero, 10f, Color.Red);
		}
	}

}
