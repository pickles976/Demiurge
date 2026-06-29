using Stride.Audio;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Engine;
using Stride.CommunityToolkit.Engine;
using Stride.CommunityToolkit.Bepu;
using Stride.BepuPhysics;
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

        public float maxRange = 100.0f;
        public float tracerLifetime = 0.1f;

        public Vector3 barrelEnd = new Vector3(0.0f, 1.5f / 16.0f, 14.75f / 16.0f);

        // -------- Runtime Variables ---------
        public float currentAmmo = 30;
        
        private bool _isReloading = false;
        private float _shotTimer = 0.0f;
        private float _reloadTimer = 0.0f;

        public Entity? PlayerEntity { get; set; }

        // Shared state the HUD reads (resolved in Start). The gun never references the HUD.
        private IPlayerStatus _status = null!;

        // Positional shot SFX, played through the shared SoundManager (no emitter on the gun).
        private SoundManager _soundManager = null!;
        private Sound _shotSound = null!;

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

            if (currentAmmo == 0 || IsBetweenShots()) return;

            _shotTimer = 0.0f;
            currentAmmo -= 1;
            PublishAmmo();

            _soundManager.Play(_shotSound);
            SpawnTracer();
        }

        /// <summary>
        /// Emits one client-side tracer streak from the barrel toward where the cursor
        /// is aiming. Purely visual; damage is the server's authoritative raycast.
        /// </summary>
        private void SpawnTracer()
        {
            if (!TryGetPlayerAndCamera(out _, out var camera)) return;

            var barrel = GetBarrelPosition();
            var mouse = Input.MousePosition;

            Vector3 endpoint;
            if (camera.Raycast(mouse, maxRange, out HitInfo hit))
            {
                endpoint = hit.Point;
            }
            else
            {
                // Miss: shoot to a far point along the cursor ray.
                var ray = camera.ScreenToWorldRaySegment(mouse);
                var dir = ray.End - ray.Start;
                dir.Normalize();
                endpoint = ray.Start + dir * maxRange;
            }

            TracerManager.Spawn(barrel, endpoint, Color.Yellow, tracerLifetime);
        }

        public override void Start()
        {
            base.Start();
            // Resolve shared state and seed it from this gun's config.
            _status = Services.GetSafeServiceAs<IPlayerStatus>();
            _status.WeaponEquipped = true;
            _status.MagazineCapacity = magazineCapacity;

            // Positional shot audio (compiled from assets/sfx/ak47_shot.ogg).
            _soundManager = Services.GetSafeServiceAs<SoundManager>();
            _shotSound = Content.Load<Sound>("sfx/ak47_shot");

            PublishAmmo();
            GameEvents.WeaponEquipped.Broadcast();
        }

        /// <summary>
        /// Writes the current ammo into shared state (B) and broadcasts the change
        /// signal (A) so the HUD re-reads. Call after any change to currentAmmo.
        /// </summary>
        private void PublishAmmo()
        {
            _status.CurrentAmmo = (int)currentAmmo;
            GameEvents.AmmoChanged.Broadcast();
        }


        public override void Update()
        {
            var dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            if (Input.IsMouseButtonDown(MouseButton.Left)) OnTriggerPull();
            DrawAimLine();
            UpdateState(dt);
        }

        public override void Cancel()
        {
            base.Cancel();
            _status.WeaponEquipped = false;
            GameEvents.WeaponEquipped.Broadcast();
        }

        private void UpdateState(float dt)
        {
            if (currentAmmo == 0 && !_isReloading) BeginReloading();
            if (IsBetweenShots()) IncrementShotTimer(dt);
            if (_isReloading) {
                IncrementReloadTimer(dt);
                if (IsReloadFinished())
                {
                    currentAmmo = magazineCapacity;
                    _isReloading = false;
                    PublishAmmo();
                }
            }
        }

        private Vector3 GetBarrelPosition()
        {
            var transform = Entity.Transform;
            transform.UpdateWorldMatrix();
            Matrix world = transform.WorldMatrix;
            return Vector3.TransformCoordinate(barrelEnd, world);
        }

        /// <summary>
        /// Resolves the owning player's script and active camera via
        /// PlayerEntity -> PlayerScript -> CameraEntity -> CameraComponent.
        /// Returns false (with nulls) if any link is missing.
        /// </summary>
        private bool TryGetPlayerAndCamera(out PlayerScript playerScript, out CameraComponent camera)
        {
            playerScript = null!;
            camera = null!;
            if (PlayerEntity is null) return false;
            if (PlayerEntity.GetComponent<PlayerScript>() is not { } ps) return false;
            if (ps.CameraEntity.GetComponent<CameraComponent>() is not { } cam) return false;
            playerScript = ps;
            camera = cam;
            return true;
        }

        private void DrawAimLine()
		{

            if (!TryGetPlayerAndCamera(out var playerScript, out var camera)) return;

			if (playerScript.State.HasFlag(PlayerStateFlags.Aiming))
			{
				var cursorPos = MathExtensions.MousePosToScreenCoords(Input.MousePosition, Game.Window.ClientBounds);
				var barrelScreenPos = MathExtensions.WorldToScreen(GetBarrelPosition(), camera.ViewProjectionMatrix, Game.Window.ClientBounds);
				LineRenderer.DrawLine2D(barrelScreenPos, cursorPos, new Color(1.0f, 1.0f, 1.0f, 0.25f));
			}

		}
        
    }

}