 using System;
 using System.Text;
 using System.Threading.Tasks;
 using Stride.Core.Mathematics;
 using Stride.Input;
 using Stride.Engine;

 namespace MyGame
 {
 	public class SampleSyncScript : SyncScript
 	{			
 		public override void Update()
 		{
 			if (Game.IsRunning)
 			{
 				Console.WriteLine("Test!");
 			}
 		}
 	}

 	// Moves the entity it is attached to using WASD keys.
 	public class WasdMovementScript : SyncScript
 	{
 		public float Speed { get; set; } = 5f;

 		public override void Update()
 		{
 			var deltaTime = (float)Game.UpdateTime.Elapsed.TotalSeconds;
 			var direction = Vector3.Zero;

 			if (Input.IsKeyDown(Keys.W)) direction.Z -= 1;
 			if (Input.IsKeyDown(Keys.S)) direction.Z += 1;
 			if (Input.IsKeyDown(Keys.A)) direction.X -= 1;
 			if (Input.IsKeyDown(Keys.D)) direction.X += 1;

 			if (direction != Vector3.Zero)
 			{
 				direction.Normalize();
 				Entity.Transform.Position += direction * Speed * deltaTime;
 			}
 		}
 	}
 }
