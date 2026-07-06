
using System.Numerics;
using Demiurge;
using Demiurge.GameClient;

public abstract class Player
{
    public ushort Id {get; init;}
    public Vector3 Position {get; set;}
    public PlayerStateFlags State {get; set;}
}

public class RemotePlayer : Player {} // netcode writes, view reads

public class LocalPlayer: Player
{
    private readonly NetworkManager network;
    public LocalPlayer(NetworkManager network) => this.network = network;

    public void Update(Vector3 intent, float dt)
    {
        Position = PlayerMovement.Step(Position, intent, State, dt);
        network.SendPosition(Position);
    }
}