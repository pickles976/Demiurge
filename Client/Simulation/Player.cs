
using System.Numerics;
using Demiurge;
using Demiurge.GameClient;

public abstract class Player
{
    public ushort Id {get; init;}
    public Vector3 Position {get; set;}
    public PlayerStateFlags State {get; set;}
    public float Yaw {get; set; }
}

public class RemotePlayer : Player {} // netcode writes, view reads

public class LocalPlayer: Player
{
    private readonly NetworkManager network;
    private uint sequence;

    public LocalPlayer(NetworkManager network) => this.network = network;

    public void Update(Vector3 intent, float dt)
    {
        // Predict locally with the same function the server runs authoritatively.
        Position = PlayerMovement.Step(Position, intent, State, dt);
        network.SendInput(new PlayerInputData { Sequence = sequence++, Intent = intent, State = State, Yaw = Yaw });
    }
}