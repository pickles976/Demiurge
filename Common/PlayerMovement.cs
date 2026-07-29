using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Everything the movement step carries between ticks. Position alone was enough while movement
    /// was flat, but reconciliation REPLAYS pending moves from authoritative state — snap the
    /// position and keep the local velocity and the replay diverges immediately after every
    /// correction (server says you landed, client still thinks it is falling at 12 m/s) and the error
    /// compounds each tick. So velocity and grounded travel with the position, and over the wire.
    /// </summary>
    public struct MoveState
    {
        /// <summary>The FEET, not the centre. Same convention as SurfaceQuery and the view.</summary>
        public Vector3 Position;
        public Vector3 Velocity;
        public bool Grounded;
    }

    public static class PlayerMovement
    {
        public const float WalkSpeed = 4f;
        public const float SprintSpeed = 6f;
        public const float SlowSpeed = 2f;
        public const float CrouchEyeDrop = 0.45f;

        public const PlayerStateFlags SlowingStates =
            PlayerStateFlags.Crouching | PlayerStateFlags.Aiming;

        // ---- Vertical motion ----

        /// <summary>
        /// Well above 9.81: real gravity over a 1.5 m jump gives 1.1 s of hang time, which reads as
        /// floating. This lands in about 0.77 s.
        /// </summary>
        public const float Gravity = 20f;

        /// <summary>Apex of a standing jump, in metres. What you can actually clear onto a ledge.</summary>
        public const float JumpHeight = 1.5f;

        /// <summary>
        /// Derived, never tuned independently. The `sqrt(2 g h)` term is the textbook launch speed;
        /// the second is a discretization correction, and it is not optional.
        ///
        /// Gravity is applied BEFORE the displacement each tick (semi-implicit Euler), so the whole
        /// flight is integrated with velocities half a tick too low. At 30 Hz that turns a nominal
        /// 1.5 m jump into 1.37 m — precisely the difference between clearing a 1.5 m ledge and
        /// bouncing off it, which is the thing this jump exists to do. Adding back half a tick of
        /// gravity makes the DISCRETE apex match JumpHeight, which is the number worth having.
        ///
        /// Assumes the step runs at FixedDt, which both the server tick and the client's prediction
        /// loop do; the correction degrades gracefully rather than breaking if that ever changes.
        /// </summary>
        public static readonly float JumpSpeed =
            MathF.Sqrt(2f * Gravity * JumpHeight) + Gravity * NetworkConfig.FixedDt * 0.5f;

        /// <summary>Caps fall speed, which also caps how far one tick can displace the body.</summary>
        public const float TerminalVelocity = 50f;

        // ---- Collision ----

        public static readonly CapsuleBody Body = new(Radius: 0.4f, Height: 1.8f);

        /// <summary>
        /// Steepest ground that counts as standable. Anything steeper acts as a wall, so mountainsides
        /// can have faces the player reads as terrain but cannot simply walk up. Chunk generation uses
        /// this same angle for exposed stone, making bare rock the visual cue for "find another route".
        /// </summary>
        public const float MaxSlopeDegrees = 55f;
        public static readonly float MaxSlopeCos = MathF.Cos(MaxSlopeDegrees * (MathF.PI / 180f));

        /// <summary>
        /// Below this angle the smoothed collision normal is unambiguously floor-like and wins over
        /// a cell-local derivative that might select the wall side of a CSG corner. Steeper contacts
        /// use the exact local normal so density saturation cannot make a cliff look walkable.
        /// </summary>
        const float PreciseSlopeThresholdDegrees = 45f;
        static readonly float PreciseSlopeThresholdCos =
            MathF.Cos(PreciseSlopeThresholdDegrees * (MathF.PI / 180f));

        /// <summary>Resting gap held between body and surface, so contact is never exactly zero.</summary>
        public const float SkinWidth = 0.02f;

        /// <summary>How far below the feet ground still counts, and how far the body is pulled down onto it.</summary>
        public const float GroundSnapDistance = 0.25f;

        /// <summary>
        /// Longest displacement per collision sub-step. Bounded by the FIELD, not by the body: stored
        /// distance saturates about 2.54 voxels from the surface, so past that band there is no
        /// gradient to collide against. Displace further than the band in one go and the body passes
        /// clean through the ground however wide it is.
        /// </summary>
        const float MaxSubStep = 0.4f;

        /// <summary>Terminal velocity over one tick is 1.67 m, so five covers the worst case.</summary>
        const int MaxSubSteps = 8;

        /// <summary>
        /// Pushout passes per sub-step. More than one because the field is not a true distance
        /// function — a single push along the gradient lands close rather than exact — and because a
        /// corner presents a second contact only after the first is resolved.
        /// </summary>
        const int ResolvePasses = 4;

        /// <summary>
        /// Caps one pushout so escaping a deep burial climbs out over a few ticks instead of
        /// teleporting. Inside the clamped band the depth estimate is meaningless (about 2.5 voxels
        /// however deep you really are), and acting on it at full strength launches the body.
        /// </summary>
        const float MaxPushPerPass = 0.5f;

        /// <summary>
        /// The authoritative movement step: the server runs it, the client predicts with it, and
        /// reconciliation replays it. Same code over the same streamed voxel bytes on both ends, so a
        /// replay reproduces the server exactly.
        /// </summary>
        public static void Step(ChunkMap terrain, ref MoveState state, Vector3 intent, PlayerStateFlags flags, float dt)
        {
            intent.Y = 0f;

            if (intent != Vector3.Zero)
                intent = Vector3.Normalize(intent);

            float speed = (flags & SlowingStates) != 0              ? SlowSpeed
                        : flags.HasFlag(PlayerStateFlags.Sprinting) ? SprintSpeed
                        : WalkSpeed;

            // Horizontal velocity is SET, not accelerated. No momentum model means a long replay
            // cannot drift from the server the way an integrated one would.
            state.Velocity.X = intent.X * speed;
            state.Velocity.Z = intent.Z * speed;

            // A grounded body carries no vertical momentum. Both signs matter: downward would have
            // gravity integrate forever against a pushout that cancels it, and UPWARD is what the
            // slide projection leaves behind after a tick of walking uphill — carried into the next
            // tick that reads as a launch, so the body bunny-hops up every slope and Grounded
            // flickers with it.
            if (state.Grounded)
                state.Velocity.Y = 0f;

            // Before gravity, so the impulse is not shaved by a tick of it on the way up.
            if (flags.HasFlag(PlayerStateFlags.Jumping) && state.Grounded)
            {
                state.Velocity.Y = JumpSpeed;
                state.Grounded = false;
            }

            // Gravity only applies while AIRBORNE. This is what stops a body creeping downhill: a
            // grounded body that sinks a little each tick gets pushed back out PERPENDICULAR to the
            // surface, and on a slope that perpendicular points partly downhill — measured at roughly
            // 0.3 m/s on 30 degrees with no input. Never sinking means never being pushed sideways.
            // The ground probe below keeps the body attached as it walks over bumps.
            if (!state.Grounded)
                state.Velocity.Y = MathF.Max(state.Velocity.Y - Gravity * dt, -TerminalVelocity);

            Move(terrain, ref state, dt);
            ProbeGround(terrain, ref state);
        }

        /// <summary>
        /// A movement state resting on the surface at a world column. Spawn and respawn go through
        /// this rather than a guessed Y — Vector3.Zero is inside the bedrock plane, which is the
        /// buried case on every spawn once collision is live.
        /// </summary>
        public static MoveState SpawnAt(ChunkMap terrain, float worldX, float worldZ)
            => new()
            {
                Position = SurfaceQuery.SurfacePosition(terrain, worldX, worldZ),
                Velocity = Vector3.Zero,
                Grounded = true
            };

        /// <summary>Collide and slide: advance in sub-steps, resolving contact after each.</summary>
        static void Move(ChunkMap terrain, ref MoveState state, float dt)
        {
            // From the pre-step velocity, which contact can only reduce — so this is an upper bound
            // on the distance actually travelled.
            int steps = (int)MathF.Ceiling(state.Velocity.Length() * dt / MaxSubStep);
            steps = Math.Clamp(steps, 1, MaxSubSteps);

            float subDt = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                var candidate = state.Position + state.Velocity * subDt;

                // Unloaded terrain is impassable, which doubles as the world edge: the server's
                // generated region is finite and the client has only what has streamed in. Refuse
                // the move rather than guessing what is there.
                if (!TerrainCollision.TryDeepestContact(terrain, Body, candidate, out _))
                {
                    state.Velocity = Vector3.Zero;
                    return;
                }

                state.Position = candidate;
                Resolve(terrain, ref state);
            }
        }

        /// <summary>
        /// Pushes the body out of the terrain and takes the velocity that was driving into the
        /// surface with it, leaving whatever was tangential to slide.
        /// </summary>
        static void Resolve(ChunkMap terrain, ref MoveState state)
        {
            float rest = Body.Radius + SkinWidth;

            for (int pass = 0; pass < ResolvePasses; pass++)
            {
                if (!TerrainCollision.TryDeepestContact(terrain, Body, state.Position, out var contact))
                    return;

                float depth = rest - contact.Distance;
                if (depth <= 0f) return;

                var normal = contact.Normal;
                bool standable = StandabilityY(contact) >= MaxSlopeCos;

                // Too steep to stand on: flatten the normal so it acts as a wall. Left alone, its
                // vertical component would walk the body straight up the face — the pushout becomes
                // a free climb.
                if (normal.Y > 0f && !standable)
                {
                    normal = normal with { Y = 0f };
                    if (normal.LengthSquared() <= 1e-12f) return;
                    normal = Vector3.Normalize(normal);
                }

                state.Position += normal * MathF.Min(depth, MaxPushPerPass);

                float into = Vector3.Dot(state.Velocity, normal);
                if (into < 0f) state.Velocity -= normal * into;

                // Ground cannot launch you, and it cannot push you sideways either.
                if (standable) state.Velocity.Y = MathF.Min(state.Velocity.Y, 0f);
            }
        }

        /// <summary>
        /// Grounded means there is standable surface within a snap of the feet — and then pulls the
        /// body down onto it. Without the snap, every sub-voxel bump lifts the body clear for a tick:
        /// grounded flickers, and since grounded gates the jump, jumping flickers with it.
        /// </summary>
        static void ProbeGround(ChunkMap terrain, ref MoveState state)
        {
            // Rising: do not probe at all. On the tick a jump starts, the ground is still well inside
            // snapping distance, and snapping to it cancels the jump on the frame it began.
            if (state.Velocity.Y > 0f)
            {
                state.Grounded = false;
                return;
            }

            state.Grounded = false;

            var foot = Body.SampleCenter(state.Position, 0);
            if (!TerrainCollision.TrySample(terrain, foot, out var here)) return;

            float gap = here.Distance - (Body.Radius + SkinWidth);
            if (gap > GroundSnapDistance) return;                  // genuinely airborne

            // Ask about the surface the body would be RESTING on, not one a quarter-metre below the
            // feet, so a standable floor under an unstandable overhang is judged on its own normal.
            var probe = foot with { Y = foot.Y - MathF.Max(gap, 0f) };
            if (!TerrainCollision.TrySample(terrain, probe, out var below)) return;

            if (StandabilityY(below) < MaxSlopeCos) return;        // too steep to stand on

            state.Grounded = true;

            if (gap <= 0f) return;

            state.Position = state.Position with { Y = state.Position.Y - gap };
            Resolve(terrain, ref state);
        }

        static float StandabilityY(in FieldPoint contact)
            => contact.Normal.Y >= PreciseSlopeThresholdCos
                ? contact.Normal.Y
                : contact.SurfaceNormal.Y;
    }
}
