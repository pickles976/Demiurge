

using Demiurge;
using Stride.Core.Mathematics;
using Stride.Engine;

public class PlayerViewScript : SyncScript {
    public required Player Player {get; init; }
    public override void Update()
    {
        Entity.Transform.Position = Player.Position.ToStride();
        Entity.Transform.Rotation = Quaternion.RotationY(Player.Yaw);
        Entity.Get<PlayerVisualScript>().State = Player.State;
    }
}