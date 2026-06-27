using System;
using System.Text;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;
using Silk.NET.SDL;
using Stride.CommunityToolkit.Engine;

// float WedgeProduct(Vector2 first, Vector2 second)
// {
// 	return (first.X * second.Y) - (first.Y * second.X);
// }

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
			var mousePos = Input.MousePosition + new Vector2(0.5f, 0.5f);

			// Calculate angular mouse displacement while middle mouse button held
			if (Input.IsMouseButtonDown(MouseButton.Middle)) {

				Console.WriteLine(mousePos);
				// Console.WriteLine(screenCenterPosition);

				var startVector = PreviousMousePosition;
				startVector.Normalize();

				var endVector = mousePos;
				endVector.Normalize();

				// Console.WriteLine($"Start: {startVector}");
				// Console.WriteLine($"End: {endVector}");


				var cross = (startVector.X * endVector.Y) - (startVector.Y * endVector.X);
				var dot = Vector2.Dot(startVector, endVector);
				var angularDisplacement = MathF.Atan2(dot, cross) - MathF.PI / 2.0f;


				Angle += angularDisplacement * deltaTime * RotationSpeed;
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

}
