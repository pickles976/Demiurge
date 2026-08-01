using Demiurge.GameClient;
using Stride.Core;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Sounds for actions that are visible in replicated STATE rather than announced by an event of
    /// their own — reloading, which is a flag on the movement stream, and digging, which is an edit
    /// applied to the field.
    ///
    /// Together because that is the thing they have in common and it is the thing that shapes the
    /// code: with no event to subscribe to, both are found by watching for a change and both have to
    /// be careful about what counts as one. They are NOT here because they are related in gameplay.
    ///
    /// Everyone's actions, not just the local player's: a reload is a tell worth hearing from the
    /// man you are fighting, and an NPC entrenching nearby should be audible.
    /// </summary>
    public sealed class ActionSoundsScript : SyncScript
    {
        public required PlayerRegistry Registry { get; init; }
        public required ObjectRegistry Objects { get; init; }
        public required IClientTerrainSource Terrain { get; init; }

        private const string DigSound = "assets/sfx/dig.wav";

        /// <summary>
        /// Eight variants, because a footstep repeats more often than any other sound in the game
        /// and one sample on a half-second cadence turns into a metronome within seconds.
        /// </summary>
        private static readonly string[] FootstepSounds =
        [
            "assets/sfx/footsteps_grass_1.wav", "assets/sfx/footsteps_grass_2.wav",
            "assets/sfx/footsteps_grass_3.wav", "assets/sfx/footsteps_grass_4.wav",
            "assets/sfx/footsteps_grass_5.wav", "assets/sfx/footsteps_grass_6.wav",
            "assets/sfx/footsteps_grass_7.wav", "assets/sfx/footsteps_grass_8.wav",
        ];

        /// <summary>
        /// Metres of ground covered per step. Cadence comes from DISTANCE rather than a timer, which
        /// is what makes sprinting sound like sprinting for free: at 6 m/s the same stride lands 1.5x
        /// as often as at 4 m/s, and a player slowed to a crouch quietens down without a second rule.
        ///
        /// Long for a human stride, deliberately. The samples are 0.8 s each, so a shorter one would
        /// pile steps on top of each other the way the heartbeat did.
        /// </summary>
        private const float StrideMetres = 2.2f;

        /// <summary>Crouching is how you move without being heard; it should not be free, but it
        /// should be worth doing.</summary>
        private const float CrouchVolume = 0.35f;
        private const float WalkVolume = 0.9f;

        /// <summary>
        /// Largest edit still treated as a shovel bite. A dig's affected box is about 7 voxels
        /// across; a grenade crater is far bigger and already has an explosion to announce it, so
        /// without this every blast would also thud like a spade.
        /// </summary>
        private const float MaximumDigRegionSize = 12f;

        private SoundManager sound = null!;
        private readonly Dictionary<ushort, bool> wasReloading = [];
        private readonly Dictionary<ushort, (System.Numerics.Vector3 Position, float Distance)> strides = [];
        private System.Action<System.Numerics.Vector3, System.Numerics.Vector3>? onRegionEdited;

        public override void Start()
        {
            sound = Services.GetSafeServiceAs<SoundManager>();
            onRegionEdited = OnRegionEdited;
            Terrain.RegionEdited += onRegionEdited;
        }

        public override void Cancel()
        {
            if (onRegionEdited is not null) Terrain.RegionEdited -= onRegionEdited;
            onRegionEdited = null;
        }

        public override void Update()
        {
            foreach (var player in Registry.Players)
            {
                // The local player's reload is PREDICTED, so its flag turns over a round trip before
                // the replicated one would — which is the whole reason the two are read differently.
                bool reloading = player is LocalPlayer local
                    ? local.IsReloading
                    : player.State.HasFlag(PlayerStateFlags.Reloading);

                bool previously = wasReloading.TryGetValue(player.Id, out bool prior) && prior;
                wasReloading[player.Id] = reloading;

                if (reloading && !previously && !player.IsDead)
                    PlayReload(player);

                UpdateFootsteps(player);
            }
        }

        /// <summary>
        /// One step per <see cref="StrideMetres"/> of ground actually covered.
        ///
        /// Measured displacement rather than the Moving flag, because the flag says what the player
        /// ASKED for while the distance says what the terrain allowed — walking into a wall sets
        /// Moving and covers no ground, and it should be silent.
        /// </summary>
        private void UpdateFootsteps(Player player)
        {
            var position = player.Position;
            if (!strides.TryGetValue(player.Id, out var stride))
            {
                strides[player.Id] = (position, 0f);
                return;
            }

            var step = position - stride.Position;
            step.Y = 0f;
            float travelled = step.Length();

            // A respawn or a correction teleports the actor; that is not walking.
            if (travelled > StrideMetres * 4f) travelled = 0f;

            bool onFoot = !player.IsDead && player.Grounded;
            float accumulated = onFoot ? stride.Distance + travelled : 0f;

            if (accumulated >= StrideMetres)
            {
                accumulated -= StrideMetres;
                sound.PlayOneShotSpatial(
                    FootstepSounds[Random.Shared.Next(FootstepSounds.Length)],
                    position.ToStride(),
                    player.State.HasFlag(PlayerStateFlags.Crouching) ? CrouchVolume : WalkVolume,
                    SoundFalloff.Footstep);
            }

            strides[player.Id] = (position, accumulated);
        }

        private void PlayReload(Player player)
        {
            if (ReloadSoundFor(player) is not { } path) return;

            // Your own reload is not a thing happening somewhere in the world, it is a thing
            // happening in your hands — so it plays flat rather than positioned, and stays audible
            // however muffled the world has become.
            if (player is LocalPlayer)
                sound.PlayOneShot(path);
            else
                sound.PlayOneShotSpatial(
                    path, Digging.Eye(player.Position).ToStride(), falloff: SoundFalloff.Reload);
        }

        /// <summary>
        /// The reload sound for whatever this player is holding. The local player knows its own
        /// weapon directly; for anyone else it has to be looked up from the replicated items, which
        /// is a scan — affordable because it runs once per reload, not once per frame.
        /// </summary>
        private string? ReloadSoundFor(Player player)
        {
            if (player is LocalPlayer local)
                return local.Weapon is { } weapon ? WeaponFx.Get(weapon.Item.Type).ReloadSoundPath : null;

            foreach (var obj in Objects.Objects)
            {
                if (!obj.Has.HasFlag(NetComponents.Item | NetComponents.Owner | NetComponents.Weapon)
                    || obj.Owner.PlayerId != player.Id)
                    continue;
                if (HotbarConfig.TryFromStorageSlot(obj.Attachment.Slot, out var slot)
                    && player.Hotbar != slot)
                    continue;
                return WeaponFx.Get(obj.Item.Type).ReloadSoundPath;
            }

            return null;
        }

        private void OnRegionEdited(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
        {
            if (System.Numerics.Vector3.Distance(min, max) > MaximumDigRegionSize) return;
            sound.PlayOneShotSpatial(
                DigSound, ((min + max) * 0.5f).ToStride(), falloff: SoundFalloff.Dig);
        }
    }
}
