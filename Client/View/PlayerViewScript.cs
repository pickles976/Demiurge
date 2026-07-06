

using Demiurge;
using Stride.Engine;

public class PlayerViewScript : SyncScript {
    public required Player Player {get; init; }
    public override void Update()
    {
        Entity.Transform.Position = Player.Position.ToStride();
        Entity.Get<PlayerVisualScript>().State = Player.State;
    }
}