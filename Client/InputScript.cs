using System.Numerics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge.GameClient
{
    
    public class InputScript : SyncScript
    {

		public required Entity CameraEntity { get; set; }

        public override void Start()
        {
        }

        public override void Update()
        {
            GenerateMovementIntent();
        }

        private void GenerateMovementIntent()
		{
			var camRot = CameraEntity.Transform.Rotation;
			var forward = MathExtensions.FlattenY(camRot * -Vector3.UnitZ); // Stride camera looks down -Z
			var right   = MathExtensions.FlattenY(camRot *  Vector3.UnitX);

			var direction = Vector3.Zero;
			if (Input.IsKeyDown(Keys.W)) direction += forward;
			if (Input.IsKeyDown(Keys.S)) direction -= forward;
			if (Input.IsKeyDown(Keys.D)) direction += right;
			if (Input.IsKeyDown(Keys.A)) direction -= right;

			GameEvents.PlayerInput.Broadcast(direction);

		}
    }


}