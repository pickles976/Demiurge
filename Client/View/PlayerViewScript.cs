

using Demiurge;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.CommunityToolkit.Engine;
using Stride.Animations;


public class PlayerViewScript : SyncScript
{
    public required Player Player { get; init; }

    public PlayerStateFlags State;

    private PlayingAnimation? CurrentAnimation { get; set; }

    public float AimBlendWeight { get; set; } = 1000f;
    private PlayingAnimation? _aimOverlay;


    public override void Update()
    {

        if (Player is RemotePlayer remote)
        {
            double renderTick = remote.NewestTick + remote.SecondsSinceNewestSnapshot * NetworkConfig.TickRate - 3.0;
            Entity.Transform.Position = remote.GetInterpolatedPosition(renderTick).ToStride();
        }
        else
        {
            Entity.Transform.Position = Player.Position.ToStride();
        }

        Entity.Transform.Rotation = Quaternion.RotationY(Player.Yaw);
        State = Player.State;

        PlayAnimations();
    }

    private void PlayAnimations()
    {
        var anim = Entity.GetComponent<AnimationComponent>();

        if (anim == null) return;

        // ---- base locomotion layer ----
        // Must stay at index 0 so overlays blend on top of it. We only (re)create
        // it when the clip actually changes, otherwise it would restart every frame
        // and wipe the aiming overlay.
        string baseClip = State.HasFlag(PlayerStateFlags.Crouching) ?
            (State.HasFlag(PlayerStateFlags.Moving) ? "CrouchWalk" : "Crouch") :
            (State.HasFlag(PlayerStateFlags.Moving) ? "Walk" : "Idle");

        bool baseMissing = CurrentAnimation == null || !anim.PlayingAnimations.Contains(CurrentAnimation);
        if (baseMissing || CurrentAnimation!.Name != baseClip)
        {
            if (CurrentAnimation != null) anim.PlayingAnimations.Remove(CurrentAnimation);

            CurrentAnimation = anim.Blend(baseClip, 1f, TimeSpan.Zero); // Blend(): adds without clearing
            CurrentAnimation.Weight = 1f;
            CurrentAnimation.RepeatMode = AnimationRepeatMode.LoopInfinite;

            // Blend() appends to the end; force the base back to the front.
            anim.PlayingAnimations.Remove(CurrentAnimation);
            anim.PlayingAnimations.Insert(0, CurrentAnimation);
        }

        // ---- aiming overlay ----
        // The Aiming clip only has arm channels. Stride normalizes blend weights per
        // bone, so a heavy AimBlendWeight makes the overlay dominate the arm bones it
        // shares with the base while leaving untouched bones at 100% locomotion.
        // Applied instantly (no fade) - on/off the moment aim state changes.
        // State-driven (not weapon-driven) so it also plays for remote players.
        bool aiming = State.HasFlag(PlayerStateFlags.Aiming);
        if (aiming && (_aimOverlay == null || !anim.PlayingAnimations.Contains(_aimOverlay)))
        {
            _aimOverlay = anim.Blend("Aiming", AimBlendWeight, TimeSpan.Zero);
            _aimOverlay.BlendOperation = AnimationBlendOperation.LinearBlend;
            _aimOverlay.RepeatMode = AnimationRepeatMode.LoopInfinite;
            _aimOverlay.Weight = AimBlendWeight;
        }
        else if (!aiming && _aimOverlay != null)
        {
            anim.PlayingAnimations.Remove(_aimOverlay);
            _aimOverlay = null;
        }

        // ---- playback speed (applies to the locomotion layer) ----
        if ((State & PlayerMovement.SlowingStates) != 0)
        {
            CurrentAnimation!.TimeFactor = 0.75f;
        }
        else if (State.HasFlag(PlayerStateFlags.Sprinting))
        {
            CurrentAnimation!.TimeFactor = 2.0f;
        }
        else
        {
            CurrentAnimation!.TimeFactor = 1.0f;
        }

    }
}