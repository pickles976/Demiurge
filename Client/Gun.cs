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
        private float _rateOfFire = 10.0f;
        public int magazineCapacity = 30;
        public float reloadTime = 1.5f;
        public float accuracy = 0.0f;

        public Vector3 barrelEnd = new Vector3(0.0f, 1.5f / 16.0f, 14.75f / 16.0f);

        // -------- Runtime Variables ---------
        public float currentAmmo;
        
        private bool _isReloading = false;
        private float _shotTimer = 0.0f;
        private float _reloadTimer = 0.0f;

        public Entity? PlayerEntity { get; set; }

        public bool IsBetweenShots()
        {
            return _shotTimer < 1.0f / _rateOfFire;
        }

        public bool IsReloadFinished()
        {
            return _reloadTimer > reloadTime;
        }

        private void IncrementShotTimer(float dt)
        {   
            _shotTimer += dt;
        }

        private void IncrementReloadTimer(float dt)
        {
            _reloadTimer +=  dt;
        }

        public void BeginReloading()
        {
            _reloadTimer = 0.0f;
            _isReloading = true;
        }

        public void OnTriggerPull()
        {

            if (currentAmmo == 0 || IsBetweenShots())
            {
                return;
            }

            _shotTimer = 0.0f;
            currentAmmo -= 1;

            // TODO: get barrel position and spawn bullet
        }


        public override void Update()
        {
            var dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            if (Input.IsMouseButtonDown(MouseButton.Left)) OnTriggerPull();
            DrawAimLine();
            UpdateState(dt);
        }

        private void UpdateState(float dt)
        {
            if (currentAmmo == 0 && !_isReloading) BeginReloading();
            if (IsBetweenShots()) IncrementShotTimer(dt);
            if (_isReloading) IncrementReloadTimer(dt);
            if (IsReloadFinished())
            {
                currentAmmo = magazineCapacity;
                _isReloading = false;
            }
        }

        private Vector3 GetBarrelPosition()
        {
            var transform = Entity.Transform;
            transform.UpdateWorldMatrix();
            Matrix world = transform.WorldMatrix;
            return Vector3.TransformCoordinate(barrelEnd, world);
        }

        private void DrawAimLine()
		{

            if (PlayerEntity is null) return;
            if (PlayerEntity.GetComponent<PlayerScript>() is not { } playerScript) return;
            if (playerScript.CameraEntity.GetComponent<CameraComponent>() is not { } camera) return;



			if (playerScript.State.HasFlag(PlayerStateFlags.Aiming))
			{
				var cursorPos = MathExtensions.MousePosToScreenCoords(Input.MousePosition, Game.Window.ClientBounds);
				var barrelScreenPos = MathExtensions.WorldToScreen(GetBarrelPosition(), camera.ViewProjectionMatrix, Game.Window.ClientBounds);
				LineRenderer.DrawLine2D(barrelScreenPos, cursorPos, Color.White);
			}

		}
        
    }

}