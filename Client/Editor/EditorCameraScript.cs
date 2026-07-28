using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge;

public sealed class EditorCameraScript : SyncScript
{
    public required ClientInputState InputState { get; init; }
    public float Speed { get; set; } = 15f;
    public float BoostMultiplier { get; set; } = 4f;
    public float LookSensitivity { get; set; } = 0.004f;

    private float yaw;
    private float pitch = -0.35f;
    private bool mouseLocked;
    private const float PitchLimit = MathUtil.PiOverTwo - 0.01f;

    public override void Start()
    {
        Entity.Transform.Rotation = Quaternion.RotationX(pitch) * Quaternion.RotationY(yaw);
    }

    public override void Update()
    {
        if (InputState.TerminalOpen)
        {
            if (mouseLocked) Input.UnlockMousePosition();
            mouseLocked = false;
            return;
        }

        if (!mouseLocked)
        {
            Input.LockMousePosition(forceCenter: true);
            Game.IsMouseVisible = false;
            mouseLocked = true;
        }

        var delta = Input.AbsoluteMouseDelta;
        yaw -= delta.X * LookSensitivity;
        pitch = MathUtil.Clamp(pitch - delta.Y * LookSensitivity, -PitchLimit, PitchLimit);
        Entity.Transform.Rotation = Quaternion.RotationX(pitch) * Quaternion.RotationY(yaw);

        var rotation = Entity.Transform.Rotation;
        var direction = Vector3.Zero;
        if (Input.IsKeyDown(Keys.W)) direction += rotation * -Vector3.UnitZ;
        if (Input.IsKeyDown(Keys.S)) direction -= rotation * -Vector3.UnitZ;
        if (Input.IsKeyDown(Keys.D)) direction += rotation * Vector3.UnitX;
        if (Input.IsKeyDown(Keys.A)) direction -= rotation * Vector3.UnitX;
        if (Input.IsKeyDown(Keys.Space)) direction += Vector3.UnitY;
        if (Input.IsKeyDown(Keys.LeftCtrl)) direction -= Vector3.UnitY;
        if (direction.LengthSquared() < 1e-8f) return;

        direction.Normalize();
        float speed = Input.IsKeyDown(Keys.LeftShift) ? Speed * BoostMultiplier : Speed;
        Entity.Transform.Position += direction * speed * (float)Game.UpdateTime.Elapsed.TotalSeconds;
    }
}
