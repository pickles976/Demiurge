using Demiurge;
using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;
using SVector3 = Stride.Core.Mathematics.Vector3;

/// <summary>
/// Creates view-only death bodies from the existing character model. The simulation deliberately
/// uses the streamed voxel field rather than server physics: ragdolls never affect hit detection,
/// movement, networking, or the authoritative world.
/// </summary>
public sealed class RagdollViewFactory : IDisposable
{
    // The cat rig's torso/pelvis node is 0.53125 m above its model origin.
    private const float PelvisHeight = 0.53f;

    private readonly Game game;
    private readonly Scene scene;
    private readonly PlayerRegistry players;
    private readonly ObjectRegistry objects;
    private readonly TerrainState terrain;
    private uint serial;

    public RagdollViewFactory(
        Game game,
        Scene scene,
        PlayerRegistry players,
        ObjectRegistry objects,
        TerrainState terrain)
    {
        this.game = game;
        this.scene = scene;
        this.players = players;
        this.objects = objects;
        this.terrain = terrain;
        objects.HealthDepleted += OnHealthDepleted;
    }

    private void OnHealthDepleted(NetObject status)
    {
        if (!status.Has.HasFlag(NetComponents.Owner)
            || !players.TryGet(status.Owner.PlayerId, out var player))
            return;

        // Use the rendered position when available. Remote simulation is deliberately ahead of its
        // interpolated view, and spawning from the simulation point would make the corpse pop.
        var playerEntity = scene.Entities.FirstOrDefault(
            entity => entity.Name == $"Player_{player.Id}");
        NVector3 feet = playerEntity?.Transform.Position.ToNumerics() ?? player.Position;
        NVector3 velocity = player switch
        {
            LocalPlayer local => local.Move.Velocity,
            RemotePlayer remote => remote.Velocity,
            _ => NVector3.Zero,
        };

        // The corpse wears what the man wore — a body that changed colour on death would read as
        // the wrong team's casualty. Same shared coat material the living body uses.
        var corpse = new ModelComponent(
            GLTFLoader.LoadModel(game, Demiurge.GameClient.PlayerCosmetics.Model));
        corpse.Materials[0] = Demiurge.GameClient.PlayerCosmetics.Coat(game, player.Team);

        var modelEntity = new Entity($"RagdollModel_{player.Id}_{serial}") { corpse };
        modelEntity.Transform.Position = new SVector3(0f, -PelvisHeight, 0f);

        var root = new Entity($"Ragdoll_{player.Id}_{serial++}");
        root.Transform.Position = (feet + NVector3.UnitY * PelvisHeight).ToStride();
        root.Transform.Rotation = SQuaternion.RotationY(player.Yaw);
        root.Transform.Children.Add(modelEntity.Transform);
        root.Add(new ClientRagdollScript
        {
            Terrain = terrain,
            ModelEntity = modelEntity,
            InitialVelocity = velocity,
            // The killing blow's shove, which arrived in the same bundle as the health that reached
            // zero — see ImpulseState. Zero for a death nothing directional caused.
            KillingBlow = status.Impulse.Velocity,
            PlayerId = player.Id,
            InitialYaw = player.Yaw,
        });
        root.Scene = scene;
    }

    public void Dispose()
    {
        objects.HealthDepleted -= OnHealthDepleted;
        foreach (var entity in scene.Entities
                     .Where(entity => entity.Name.StartsWith("Ragdoll_", StringComparison.Ordinal))
                     .ToArray())
            entity.Scene = null;
    }
}

/// <summary>
/// A deliberately small cosmetic ragdoll: several body contacts tumble the skinned model against
/// the terrain while constrained limb joints carry independent angular momentum. It is cheap
/// enough for mass NPC deaths and, unlike Bepu bodies, collides with the game's custom voxel field
/// directly.
/// </summary>
public sealed class ClientRagdollScript : SyncScript
{
    public required TerrainState Terrain { get; init; }
    public required Entity ModelEntity { get; init; }
    public required NVector3 InitialVelocity { get; init; }

    /// <summary>
    /// The shove the lethal blow imparted, in m/s, already scaled by damage on the server
    /// (<see cref="RagdollImpulse"/>). Zero means nobody told us what killed this body, and the
    /// deterministic topple in <see cref="Start"/> stands in for it.
    /// </summary>
    public required NVector3 KillingBlow { get; init; }
    public required ushort PlayerId { get; init; }
    public required float InitialYaw { get; init; }

    public const float FreezeAfterSeconds = 10f;
    public const float DespawnAfterSeconds = 60f;

    private const float Gravity = 20f;
    private const float MaxSubstepDistance = 0.25f;
    private const int MaxSubsteps = 8;
    private const int ContactPasses = 4;
    private const float Restitution = 0.02f;
    private const float Friction = 0.72f;
    private const float InverseInertia = 2.4f;

    /// <summary>
    /// Where a killing blow is taken to land, relative to the pelvis the body rotates about: chest
    /// height, which is both where most rounds go and high enough that the resulting torque topples
    /// rather than merely nudges. One constant instead of the real impact point, because the wire
    /// carries a direction and not a place — see ImpulseState.
    /// </summary>
    private static readonly NVector3 BlowLever = new(0f, 0.3f, 0f);

    /// <summary>Ceiling on the spin a blow can impart, in rad/s. Held in proportion to
    /// RagdollImpulse.MaxSpeed, since spin here is derived from the shove: past about this a corpse
    /// reads as a prop being thrown rather than a body falling.</summary>
    private const float MaxBlowSpin = 4f;

    // Bind-pose offsets from the pelvis/root, measured from the cat rig. Keep the radii close to
    // the visible body thickness: oversized contacts are stable but make the mesh visibly hover.
    private readonly record struct BodyContact(NVector3 LocalCenter, float Radius);
    private static readonly BodyContact[] BodyContacts =
    [
        new(new NVector3(0f, 0f, 0f), 0.14f),           // pelvis
        new(new NVector3(0f, 0.15f, 0f), 0.145f),       // torso
        new(new NVector3(0f, 0.31f, 0f), 0.14f),        // upper chest
        new(new NVector3(0f, 0.56f, 0f), 0.13f),        // head
        new(new NVector3(0.087f, -0.51f, 0.03f), 0.025f),
        new(new NVector3(-0.087f, -0.51f, 0.03f), 0.025f),
        new(new NVector3(0.267f, 0.04f, 0f), 0.045f),
        new(new NVector3(-0.267f, 0.04f, 0f), 0.045f),
    ];

    private sealed class Joint
    {
        public required int Node;
        public required SVector3 Axis;
        public required float Min;
        public required float Max;
        public required float Angle;
        public required float Velocity;
        public required NVector3 RestDirection;
        public required float GravityResponse;
        public SQuaternion BaseRotation;
    }

    private readonly List<Joint> joints = [];
    private NVector3 position;
    private NVector3 velocity;
    private NVector3 angularVelocity;
    private NQuaternion orientation;
    private float elapsed;
    private bool skeletonReady;
    private bool frozen;

    public override void Start()
    {
        position = Entity.Transform.Position.ToNumerics();
        velocity = InitialVelocity;
        orientation = NQuaternion.CreateFromAxisAngle(NVector3.UnitY, InitialYaw);

        if (KillingBlow.LengthSquared() > 1e-6f)
        {
            velocity += KillingBlow;

            // Spin comes out of the same relation the contact solver uses — lever cross impulse,
            // scaled by the inverse inertia — rather than a second made-up rule, so a shove that
            // lands above the pelvis rotates the body away from it exactly as a floor contact under
            // the pelvis rotates it the other way. That is what makes a man fall over backwards when
            // shot in the chest instead of sliding away upright.
            var spin = NVector3.Cross(BlowLever, KillingBlow) * InverseInertia;
            float spinSpeed = spin.Length();
            angularVelocity = spinSpeed > MaxBlowSpin ? spin * (MaxBlowSpin / spinSpeed) : spin;
            return;
        }

        // Nothing directional killed this one. A small deterministic nudge still makes a stationary
        // victim topple. It is intentionally subdued: the old impulse launched the body upward and
        // gave it enough angular momentum to spin for several seconds.
        float side = (PlayerId & 1) == 0 ? 1f : -1f;
        var lateral = new NVector3(MathF.Cos(InitialYaw), 0f, -MathF.Sin(InitialYaw));
        velocity += lateral * (0.25f * side);
        angularVelocity = new NVector3(0.9f * side, 0.08f, 0.55f);
    }

    public override void Update()
    {
        float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
        if (!float.IsFinite(dt) || dt <= 0f) return;

        elapsed += dt;
        if (elapsed >= DespawnAfterSeconds)
        {
            Entity.Scene = null;
            return;
        }

        InitializeSkeleton();
        if (frozen || elapsed >= FreezeAfterSeconds)
        {
            frozen = true;
            return;
        }

        dt = MathF.Min(dt, 0.1f);
        SimulateBody(dt);
        SimulateJoints(dt);

        Entity.Transform.Position = position.ToStride();
        Entity.Transform.Rotation = orientation.ToStride();
    }

    private void SimulateBody(float dt)
    {
        velocity.Y = MathF.Max(velocity.Y - Gravity * dt, -PlayerMovement.TerminalVelocity);
        int steps = Math.Clamp(
            (int)MathF.Ceiling(velocity.Length() * dt / MaxSubstepDistance),
            1,
            MaxSubsteps);
        float stepDt = dt / steps;

        for (int step = 0; step < steps; step++)
        {
            position += velocity * stepDt;
            IntegrateOrientation(stepDt);

            float impactSpeed = ResolveContacts(stepDt);
            if (impactSpeed > 0f) KickJoints(impactSpeed);
        }

        angularVelocity *= MathF.Exp(-0.35f * dt);
    }

    private float ResolveContacts(float dt)
    {
        bool touching = false;
        float largestImpact = 0f;

        // Resolve the deepest contact, update the pose, then search again. Handling all contacts
        // from the old pose at once over-corrects corners and injects spin.
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            if (!TryDeepestContact(out var localCenter, out var normal, out float penetration))
                break;

            touching = true;
            position += normal * penetration;

            var lever = NVector3.Transform(localCenter, orientation);
            var pointVelocity = velocity + NVector3.Cross(angularVelocity, lever);
            float intoSurface = NVector3.Dot(pointVelocity, normal);
            if (intoSurface >= 0f) continue;

            float impact = -intoSurface;
            largestImpact = MathF.Max(largestImpact, impact);

            var leverCrossNormal = NVector3.Cross(lever, normal);
            float denominator = 1f + InverseInertia * leverCrossNormal.LengthSquared();
            float normalImpulseMagnitude = (1f + Restitution) * impact / denominator;
            var normalImpulse = normal * normalImpulseMagnitude;
            velocity += normalImpulse;
            angularVelocity += NVector3.Cross(lever, normalImpulse) * InverseInertia;

            // Coulomb friction at the actual contact point damps both sliding and spinning. A
            // blanket angular multiplier alone cannot distinguish a body resting on the floor from
            // one legitimately tumbling through the air.
            pointVelocity = velocity + NVector3.Cross(angularVelocity, lever);
            var tangent = pointVelocity - normal * NVector3.Dot(pointVelocity, normal);
            float tangentSpeed = tangent.Length();
            if (tangentSpeed > 1e-5f)
            {
                var tangentDirection = tangent / tangentSpeed;
                var leverCrossTangent = NVector3.Cross(lever, tangentDirection);
                float tangentDenominator =
                    1f + InverseInertia * leverCrossTangent.LengthSquared();
                float frictionMagnitude = MathF.Min(
                    tangentSpeed / tangentDenominator,
                    Friction * normalImpulseMagnitude);
                var frictionImpulse = -tangentDirection * frictionMagnitude;
                velocity += frictionImpulse;
                angularVelocity += NVector3.Cross(lever, frictionImpulse) * InverseInertia;
            }
        }

        if (touching)
        {
            angularVelocity *= MathF.Exp(-4.5f * dt);
            if (velocity.LengthSquared() < 0.0025f) velocity = NVector3.Zero;
            if (angularVelocity.LengthSquared() < 0.01f) angularVelocity = NVector3.Zero;
        }

        return largestImpact;
    }

    private bool TryDeepestContact(
        out NVector3 localCenter,
        out NVector3 normal,
        out float penetration)
    {
        localCenter = default;
        normal = NVector3.UnitY;
        penetration = 0f;

        foreach (var bodyContact in BodyContacts)
        {
            var worldCenter =
                position + NVector3.Transform(bodyContact.LocalCenter, orientation);
            if (!TerrainCollision.TrySample(Terrain.Map, worldCenter, out var terrainContact))
                continue;

            float candidate = bodyContact.Radius - terrainContact.Distance;
            if (candidate <= penetration) continue;
            penetration = candidate;
            localCenter = bodyContact.LocalCenter;
            normal = terrainContact.Normal;
        }

        return penetration > 0f;
    }

    private void IntegrateOrientation(float dt)
    {
        float speed = angularVelocity.Length();
        if (speed < 1e-5f) return;
        var delta = NQuaternion.CreateFromAxisAngle(angularVelocity / speed, speed * dt);
        orientation = NQuaternion.Normalize(delta * orientation);
    }

    private void InitializeSkeleton()
    {
        if (skeletonReady) return;
        var skeleton = ModelEntity.Get<ModelComponent>()?.Skeleton;
        if (skeleton == null) return;

        AddJoint(skeleton, "head", SVector3.UnitX, -0.65f, 0.65f, 0.5f,
            NVector3.UnitY, gravityResponse: 5f);
        AddJoint(skeleton, "left_arm", SVector3.UnitZ, -1.45f, 1.45f, 1.6f,
            NVector3.Normalize(new NVector3(0.46f, -0.89f, 0f)), gravityResponse: 8f);
        AddJoint(skeleton, "right_arm", SVector3.UnitZ, -1.45f, 1.45f, -1.5f,
            NVector3.Normalize(new NVector3(-0.46f, -0.89f, 0f)), gravityResponse: 8f);
        AddJoint(skeleton, "left_forearm", SVector3.UnitX, -1.5f, 0.45f, -2.1f,
            -NVector3.UnitY, gravityResponse: 6f);
        AddJoint(skeleton, "right_forearm", SVector3.UnitX, -1.5f, 0.45f, 2f,
            -NVector3.UnitY, gravityResponse: 6f);
        AddJoint(skeleton, "left_thigh", SVector3.UnitX, -1.2f, 1.2f, 1.35f,
            -NVector3.UnitY, gravityResponse: 9f);
        AddJoint(skeleton, "right_thigh", SVector3.UnitX, -1.2f, 1.2f, -1.3f,
            -NVector3.UnitY, gravityResponse: 9f);
        AddJoint(skeleton, "left_calf", SVector3.UnitX, -0.25f, 1.5f, 1.8f,
            -NVector3.UnitY, gravityResponse: 7f);
        AddJoint(skeleton, "right_calf", SVector3.UnitX, -0.25f, 1.5f, -1.7f,
            -NVector3.UnitY, gravityResponse: 7f);
        skeletonReady = true;
    }

    private void AddJoint(
        SkeletonUpdater skeleton,
        string nodeName,
        SVector3 axis,
        float min,
        float max,
        float initialVelocity,
        NVector3 restDirection,
        float gravityResponse)
    {
        int node = Array.FindIndex(skeleton.Nodes, candidate => candidate.Name == nodeName);
        if (node < 0) return;
        joints.Add(new Joint
        {
            Node = node,
            Axis = axis,
            Min = min,
            Max = max,
            Angle = 0f,
            Velocity = initialVelocity,
            RestDirection = restDirection,
            GravityResponse = gravityResponse,
            BaseRotation = skeleton.NodeTransformations[node].Transform.Rotation,
        });
    }

    private void SimulateJoints(float dt)
    {
        var skeleton = ModelEntity.Get<ModelComponent>()?.Skeleton;
        if (skeleton == null) return;

        foreach (var joint in joints)
        {
            // Let gravity pull each segment down in world space as the torso turns. The weak bind
            // spring only keeps joints from parking permanently at a limit; gravity, not the bind
            // pose, dominates their motion.
            var axis = joint.Axis.ToNumerics();
            var jointRotation = NQuaternion.CreateFromAxisAngle(axis, joint.Angle);
            var segmentInModel = NVector3.Transform(joint.RestDirection, jointRotation);
            var segmentInWorld = NVector3.Transform(segmentInModel, orientation);
            var axisInWorld = NVector3.Transform(axis, orientation);
            float gravityTorque = NVector3.Dot(
                axisInWorld,
                NVector3.Cross(segmentInWorld, -NVector3.UnitY));

            joint.Velocity += gravityTorque * joint.GravityResponse * dt;
            joint.Velocity += -joint.Angle * 0.4f * dt;
            joint.Velocity *= MathF.Exp(-0.52f * dt);
            joint.Angle += joint.Velocity * dt;
            if (joint.Angle < joint.Min)
            {
                joint.Angle = joint.Min;
                joint.Velocity = MathF.Abs(joint.Velocity) * 0.08f;
            }
            else if (joint.Angle > joint.Max)
            {
                joint.Angle = joint.Max;
                joint.Velocity = -MathF.Abs(joint.Velocity) * 0.08f;
            }

            ref var transform = ref skeleton.NodeTransformations[joint.Node];
            transform.Transform.Rotation =
                joint.BaseRotation * SQuaternion.RotationAxis(joint.Axis, joint.Angle);
        }
    }

    private void KickJoints(float impactSpeed)
    {
        if (impactSpeed <= 0.1f) return;
        float impulse = MathF.Min(impactSpeed, 10f) * 0.055f;
        for (int i = 0; i < joints.Count; i++)
        {
            float sign = ((PlayerId + i) & 1) == 0 ? 1f : -1f;
            joints[i].Velocity += impulse * sign;
        }
    }
}
