using Demiurge;
using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;

// Hover-and-spin presenter for weapon pickups. Client-side animation over the
// replicated base position — this script OWNS the entity transform, so the
// factory must not also attach a NetTransformScript (they would fight).
public class PickupBobScript : SyncScript
{
    public required NetObject Object { get; init; }

    private const float FloatHeight = 0.4f;    // resting height above the base position
    private const float BobHeight = 0.15f;
    private const float BobHz = 0.5f;
    private const float SpinDegPerSec = 90f;

    private float age;

    public override void Update()
    {
        age += (float)Game.UpdateTime.Elapsed.TotalSeconds;

        float bob = BobHeight * MathF.Sin(2f * MathF.PI * BobHz * age);
        Entity.Transform.Position = Object.Transform.Position.ToStride()
            + Vector3.UnitY * (FloatHeight + bob);
        Entity.Transform.Rotation = Quaternion.RotationY(MathUtil.DegreesToRadians(SpinDegPerSec) * age);
    }
}

// Attaches a worn item's view to its owner's body — driven by the replicated
// Owner and Attachment components. The wire says WHOSE body and WHICH slot;
// the slot keys the client's socket table (ItemCosmetics): a bone name links
// via ModelNodeLinkComponent, null follows the Player_{id} entity root every
// frame. The owner's view may not exist yet when this spawns (reliable
// messages aren't ordered relative to each other), so it retries every frame
// until the player appears.
//
// The entity stays at the scene root on purpose: ModelNodeLinkComponent drives
// its world transform from the bone regardless of hierarchy, root-following
// composes the owner's transform explicitly, and root-level entities keep
// ObjectViewFactory.DestroyView's find-by-name working.
public class ItemAttachScript : SyncScript
{
    public required NetObject Object { get; init; }
    public required WeaponMount Mount { get; init; }
    public required PlayerRegistry Registry { get; init; }
    public required Entity CameraEntity { get; init; }
    public required LocalWeaponView WeaponView { get; init; }

    private const float ViewModelSharpness = 18f;

    private Entity? owner;
    private bool boneLinked;
    private Vector3 viewGripOffset = WeaponMount.HipGripOffset;
    private bool firstViewFrame = true;

    public override void Update()
    {
        owner ??= Entity.Scene?.Entities.FirstOrDefault(e => e.Name == $"Player_{Object.Owner.PlayerId}");
        if (owner == null) return;

        if (IsLocalHandWeapon())
        {
            UpdateFirstPersonWeapon();
            return;
        }

        Entity.Get<ModelComponent>()!.Enabled = true;
        var socket = ItemCosmetics.GetSocket(Object.Attachment.Slot, Object.Item.Type, Mount);
        if (socket.Node is { } node)
        {
            if (boneLinked) return;   // latch: slot never changes in place (transitions are despawn/respawn)
            if (owner.Get<ModelComponent>() is not { } ownerModel) return;

            Entity.Add(new ModelNodeLinkComponent
            {
                Target = ownerModel,
                NodeName = node,
            });
            Entity.Transform.Position = socket.Seat;
            Entity.Transform.Rotation = socket.Rotation;
            boneLinked = true;
        }
        else
        {
            // Root-worn items follow the owner's root transform every frame —
            // PlayerViewScript drives that entity, this composes the seat on top.
            Entity.Transform.Position = owner.Transform.Position + Vector3.Transform(socket.Seat, owner.Transform.Rotation);
            Entity.Transform.Rotation = owner.Transform.Rotation * socket.Rotation;
        }
    }

    private bool IsLocalHandWeapon()
        => Registry.LocalPlayer is { } local
           && Object.Owner.PlayerId == local.Id
           && Object.Attachment.Slot == EquipSlot.Hand
           && Object.Has.HasFlag(NetComponents.Weapon);

    private void UpdateFirstPersonWeapon()
    {
        var model = Entity.Get<ModelComponent>();
        if (model == null) return;

        if (CameraEntity.Get<DebugFlyCameraScript>()?.Active == true)
        {
            model.Enabled = false;
            WeaponView.Clear();
            return;
        }
        model.Enabled = true;

        if (boneLinked)
        {
            Entity.Remove<ModelNodeLinkComponent>();
            boneLinked = false;
        }

        bool aiming = Registry.LocalPlayer?.State.HasFlag(PlayerStateFlags.Aiming) == true;
        var targetGripOffset = aiming ? WeaponMount.AimGripOffset : WeaponMount.HipGripOffset;

        if (firstViewFrame)
        {
            viewGripOffset = targetGripOffset;
            firstViewFrame = false;
        }
        else
        {
            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            viewGripOffset = Vector3.Lerp(viewGripOffset, targetGripOffset,
                1f - MathF.Exp(-ViewModelSharpness * dt));
        }

        var cameraRotation = CameraEntity.Transform.Rotation;
        var weaponRotation = WeaponMount.FirstPersonRotation;
        var modelOffset = Mount.FirstPersonModelOffset(Object.Item.Type, viewGripOffset).ToStride();
        var muzzleOffset = Mount.FirstPersonMuzzleOffset(Object.Item.Type, viewGripOffset).ToStride();

        Entity.Transform.Scale = new Vector3(WeaponMount.FirstPersonScale);
        Entity.Transform.Position = CameraEntity.Transform.Position + Vector3.Transform(modelOffset, cameraRotation);
        Entity.Transform.Rotation = weaponRotation.ToStride() * cameraRotation;

        WeaponView.MuzzleWorld = (System.Numerics.Vector3)(CameraEntity.Transform.Position + Vector3.Transform(muzzleOffset, cameraRotation));
        WeaponView.Weapon = Object.Item.Type;
        WeaponView.NetworkId = Object.NetworkId;
    }
}
