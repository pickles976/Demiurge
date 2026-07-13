using Demiurge;
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

// Parents an EquippedWeapon's view to its owner's hand bone — the old AttachGun
// logic, driven by the replicated Owner component. The owner's view entity may
// not exist yet when this spawns (reliable messages aren't ordered relative to
// each other), so it retries every frame until the player appears.
//
// The entity stays at the scene root on purpose: ModelNodeLinkComponent drives
// its world transform from the bone regardless of hierarchy, and root-level
// entities keep ObjectViewFactory.DestroyView's find-by-name working.
public class WeaponAttachScript : SyncScript
{
    public required NetObject Object { get; init; }

    private bool attached;

    public override void Update()
    {
        if (attached) return;

        var owner = Entity.Scene?.Entities.FirstOrDefault(e => e.Name == $"Player_{Object.Owner.PlayerId}");
        if (owner?.Get<ModelComponent>() is not { } ownerModel) return;

        Entity.Add(new ModelNodeLinkComponent
        {
            Target = ownerModel,
            NodeName = "right_hand",
        });

        // Bone-relative seat offsets, carried over from the old AttachGun.
        Entity.Transform.Position = new Vector3(0.0f, -3.75f / 16.0f, 0.425f / 16.0f);
        Entity.Transform.Rotation = Quaternion.RotationX(MathF.PI / 2.0f) * Quaternion.RotationZ(MathF.PI);
        attached = true;
    }
}
