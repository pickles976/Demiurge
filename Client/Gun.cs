using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;
using Stride.CommunityToolkit.Engine;
using Stride.Animations;
using Stride.Rendering;

namespace Demiurge
{

    public class GunScript: SyncScript
    {

        // -------- Config Variables ---------
        private float rateOfFire = 10.0f;
        public int magazineCapacity = 30;
        public float reloadTime = 1.5f;
        public float accuracy = 0.0f;

        public Vector3 barrelEnd = new Vector3(0.0f, 1.5f / 16.0f, 14.75f / 16.0f);

        // -------- Runtime Variables ---------
        public float currentAmmo;
        
        private bool isReloading = false;
        private float shotTimer = 0.0f;
        private float reloadTimer = 0.0f;

        public Entity? PlayerEntity { get; set; }

        public bool isBetweenShots()
        {
            return shotTimer < 1.0f / rateOfFire;
        }

        public bool isReloadFinished()
        {
            return reloadTimer > reloadTime;
        }

        private void incrementShotTimer(float dt)
        {   
            shotTimer += dt;
        }

        private void incrementReloadTimer(float dt)
        {
            reloadTimer +=  dt;
        }

        public void beginReloading()
        {
            reloadTimer = 0.0f;
            isReloading = true;
        }

        public void onTriggerPull()
        {
            if (currentAmmo == 0 || isBetweenShots())
            {
                return;
            }

            shotTimer = 0.0f;
            currentAmmo -= 1;

            // TODO: get barrel position and spawn bullet
        }


        public override void Update()
        {
            DrawAimLine();
        }

        private void DrawAimLine()
		{

			if (PlayerEntity == null) return;

            // TODO: how do I clean this up?
			var playerScript = PlayerEntity.GetComponent<PlayerScript>();

            if (playerScript == null) return;

            // TODO: get barrel position

			if (playerScript.State.HasFlag(PlayerStateFlags.Aiming))
			{
				var cursor = MathExtensions.MousePosToScreenCoords(Input.MousePosition, Game.Window.ClientBounds);
				var barrelScreenPos = MathExtensions.WorldToScreen(PlayerEntity.Transform.Position, playerScript.CameraEntity.GetComponent<CameraComponent>().ViewProjectionMatrix, Game.Window.ClientBounds);
				LineRenderer.DrawLine2D(barrelScreenPos, cursor, Color.White);       // draw_aim_line
			}

		}
        
    }

}