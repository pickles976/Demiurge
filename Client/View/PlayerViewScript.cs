

using Demiurge;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.CommunityToolkit.Engine;
using Stride.Animations;


public class PlayerViewScript : SyncScript
{
    public required PlayerRegistry Registry {get; init;}
    public required Player Player { get; init; }

    public PlayerStateFlags State;

    private PlayingAnimation? CurrentAnimation { get; set; }

    public float AimBlendWeight { get; set; } = 1000f;
    private PlayingAnimation? _aimOverlay;


    public override void Update()
    {

        switch (Player)
        {
            case RemotePlayer remote:
                Entity.Transform.Position = 
                    remote.Snapshots.GetInterpolated(Registry.RenderTick, remote.Position).ToStride();
                break;
            case LocalPlayer local:
                // Sim position moves in 30Hz steps and jumps on reconciliation corrections;
                // ease the visual toward it, but snap if the error is too big to glide.
                var target = local.Position.ToStride();
                var current = Entity.Transform.Position;
                float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;

                if ((target - current).LengthSquared() > 2f * 2f)
                    Entity.Transform.Position = target;
                else
                    Entity.Transform.Position = Vector3.Lerp(current, target, 1f - MathF.Exp(-20f * dt));
                break;

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