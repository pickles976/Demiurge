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

namespace MyGame
{

	// Moves the entity it is attached to using WASD keys.
	public class WasdMovementScript : SyncScript
	{
		public float Speed { get; set; } = 5f;

		public override void Update()
		{
			var deltaTime = (float)Game.UpdateTime.Elapsed.TotalSeconds;
			var direction = Vector3.Zero;

			if (Input.IsKeyDown(Keys.W)) direction.Z += 1;
			if (Input.IsKeyDown(Keys.S)) direction.Z -= 1;
			if (Input.IsKeyDown(Keys.A)) direction.X += 1;
			if (Input.IsKeyDown(Keys.D)) direction.X -= 1;

			if (direction != Vector3.Zero)
			{
				direction.Normalize();
				Entity.Transform.Position += direction * Speed * deltaTime;
			}
		}
	}

	public class ThirdPersonCameraScript : SyncScript
	{
		// Camera Offset
		public float Height { get; set; } = 10.0f;

		public float Radius { get; set; } = 5.0f;

		public float Angle { get; set; } = 0.0f; // Rotation angle

		// Aiming Look-ahead
		public required Entity Player {get; set;}
		public Vector3 Target { get; set; } = new Vector3(0, 0, 0);

		public float LookAheadRadiusFar { get; set; } = 500.0f;
		public float LookAheadRadiusNear { get; set; } = 200.0f;

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
			var deltaTime = (float)Game.UpdateTime.Elapsed.TotalSeconds;
			// var screenCenterPosition = new Vector2(Game.Window.ClientBounds.Width / 2.0f, Game.Window.ClientBounds.Height / 2.0f);
			// Center on (0.5, 0.5) so the angle is measured around the screen center,
			// and flip Y so the axis matches Bevy's top-left/Y-down cursor convention.
			var centered = Input.MousePosition - new Vector2(0.5f, 0.5f);
			var mousePos = new Vector2(centered.X, -centered.Y);

			// Calculate angular mouse displacement while middle mouse button held
			if (Input.IsMouseButtonDown(MouseButton.Middle)) {

				var startVector = PreviousMousePosition;
				startVector.Normalize();

				var endVector = mousePos;
				endVector.Normalize();

				var cross = startVector.Wedge(endVector);
				var dot = Vector2.Dot(startVector, endVector);
				var angularDisplacement = MathF.Atan2(dot, cross) - MathF.PI / 2.0f;

				Angle -= angularDisplacement * deltaTime * RotationSpeed;
			}

			// Figure out where to set the look-ahead position
			var lookaheadOffset = mousePos;
			var distance = lookaheadOffset.Length();

			// TODO: player state, aiming vs not aiming
			// 1. Remap
			// 2. step function
			var scale = 1.0f;

			lookaheadOffset.Normalize();
			var norm = lookaheadOffset * scale;

			// Convert 2D mouse offset into 3D target offset
			var offset = new Vector3(norm.X, 0.0f, norm.Y);
			var rigRotation = Quaternion.RotationY(Angle);
			Vector3.Transform(ref offset, ref rigRotation, out var rotatedOffset);
			Target = Player.Transform.Position + rotatedOffset;
			PreviousMousePosition = mousePos;

			// FollowCamera::get_desired_camera_position()
			var forward = Vector3.UnitZ;
			Vector3.Transform(ref forward, ref rigRotation, out var rotatedForward);
			var desiredPosition = Target + rotatedForward * Radius + Vector3.UnitY *
			Height;

			// Transform::from_translation(desired).looking_at(target, Y)
			// LookAtRH gives a view (world->camera) matrix; invert it to get the camera's world rotation
			var lookAt = Matrix.LookAtRH(desiredPosition, Target, Vector3.UnitY);
			lookAt.Invert();
			var desiredRotation = Quaternion.RotationMatrix(lookAt);

			// Position is lerped, rotation is set directly — exactly like the Bevy version
			Entity.Transform.Position = Vector3.Lerp(Entity.Transform.Position,
			desiredPosition, LerpSpeed);
			Entity.Transform.Rotation = desiredRotation;
		}
	}

	// Draws a ring at the cursor position, like Bevy's draw_reticle gizmos.
	public class CursorReticleScript : SyncScript
	{
		public int Size { get; set; } = 32;
		public float Thickness { get; set; } = 3.0f;

		private ImageElement _reticle = null!;

		public override void Start()
		{
			var texture = CreateRingTexture(GraphicsDevice, Size, Thickness);

			_reticle = new ImageElement
			{
				// IsTransparent enables alpha blending; without it the cut-out (alpha 0)
				// pixels render opaque white and you get a box instead of a ring.
				Source = new SpriteFromTexture { Texture = texture, IsTransparent = true },
				Width = Size,
				Height = Size,
			};
			// Canvas defaults to absolute positioning; opt into RelativePosition so the
			// per-frame normalized cursor position is actually honored.
			_reticle.DependencyProperties.Set(Canvas.UseAbsolutePositionPropertyKey, false);
			// Pin the element's center to its position so it sits ON the cursor.
			_reticle.DependencyProperties.Set(Canvas.PinOriginPropertyKey, new Vector3(0.5f, 0.5f, 0f));

			var canvas = new Canvas();
			canvas.Children.Add(_reticle);

			Entity.Add(new UIComponent
			{
				Page = new UIPage { RootElement = canvas },
				RenderGroup = RenderGroup.Group31, // matches AddCleanUIStage()
			});
		}

		public override void Update()
		{
			var mousePos = Input.MousePosition;
			_reticle.DependencyProperties.Set(Canvas.RelativePositionPropertyKey, new Vector3(mousePos.X, mousePos.Y, 0f));
		}

		// White ring on a transparent background, RGBA8
		private static Texture CreateRingTexture(GraphicsDevice device, int size, float thickness)
		{
			var data = new byte[size * size * 4];
			float center = size / 2f;
			float radius = center - 1f;

			for (int y = 0; y < size; y++)
			for (int x = 0; x < size; x++)
			{
				float dx = x + 0.5f - center;
				float dy = y + 0.5f - center;
				float dist = MathF.Sqrt(dx * dx + dy * dy);
				bool ring = dist <= radius && dist >= radius - thickness;

				// Premultiplied alpha: Stride's UI blends src·1 + dst·(1−srcA), so
				// transparent pixels must be (0,0,0,0) — not white-with-zero-alpha, or
				// their RGB gets added and fills the whole quad into a box.
				byte v = (byte)(ring ? 255 : 0);
				int i = (y * size + x) * 4;
				data[i + 0] = v;
				data[i + 1] = v;
				data[i + 2] = v;
				data[i + 3] = v;
			}

			return Texture.New2D(device, size, size, PixelFormat.R8G8B8A8_UNorm_SRgb, data);
		}
	}

}
