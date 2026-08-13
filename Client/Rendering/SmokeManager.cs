using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Demiurge
{
    /// <summary>
    /// Smoke, as a small pool of rising puffs.
    ///
    /// Ours rather than Stride's, for the reason recorded in Program.cs: the engine's particle
    /// renderer crashes on Vulkan here (stride3d/stride#2496) and is switched off. That is no great
    /// loss at this scale — a puff is one model, one transform and one colour, and a few dozen of
    /// them is a list and a loop.
    ///
    /// Modelled on the launch smoke in the Godot project: emitted from a small sphere, thrown
    /// upward in a tight cone, given a gentle POSITIVE gravity so it keeps rising as it slows,
    /// growing from about a quarter of its size to full over its life, and holding opacity until
    /// two thirds through before fading out. Those choices are what make it read as smoke rather
    /// than as debris, and they are all it takes.
    ///
    /// Entities are pooled and never destroyed. A puff that dies is hidden and handed back, because
    /// the alternative at this rate is adding and removing scene entities several times a second.
    /// </summary>
    public sealed class SmokeManager
    {
        private const string ModelPath = "assets/models/smoke.gltf";

        /// <summary>Ceiling on live puffs. Past this the oldest is recycled rather than the pool
        /// grown, so a runaway emitter costs a constant amount.</summary>
        private const int MaxPuffs = 440;

        /// <summary>
        /// Long, because height comes from TIME under buoyancy rather than from being thrown hard.
        /// A fast puff reads as debris; a slow one that keeps going reads as smoke.
        ///
        /// Twenty seconds is what a column reaching a couple of hundred metres costs. It is also why
        /// the smoke changes the MAP rather than decorating one explosion: a puff outlives the bang
        /// by a factor of thirty, so an hour into a match the sky over the contested ground is full
        /// of everything that has happened on it.
        /// </summary>
        private const float Lifetime = 45f;

        private const float EmissionRadius = 0.3f;
        private const float SpreadDegrees = 7f;
        private const float MinSpeed = 5f;
        private const float MaxSpeed = 6f;

        /// <summary>How far above the crater the column starts. Smoke off a blast is already clear
        /// of the ground by the time it is smoke, and starting it in the hole buries the first
        /// second of it in the terrain that made it.</summary>
        private const float SpawnRise = 3f;
        /// <summary>
        /// The rise is CONSTANT: whatever a puff is launched at, it holds for its whole life.
        ///
        /// Two models have been tried here and both were wrong in the same direction. A constant
        /// upward acceleration got faster the higher it went, which is the reverse of hot gas. An
        /// exponential decay toward a terminal drift fixed that, but it also collapsed the launch
        /// cone within the first few seconds, so the column left the ground as a spray and then rose
        /// as a rope.
        ///
        /// Holding the launch velocity keeps the cone: the sideways component persists as long as
        /// the upward one, so the plume widens steadily the whole way up instead of pinching in.
        /// Height is then simply speed times life, which is the easiest thing in this file to aim.
        /// </summary>

        private const float MinScale = 0.75f;
        private const float MaxScale = 1.1f;

        /// <summary>
        /// What a puff swells to by the end, as a multiple of its birth size.
        ///
        /// It has to be large. A column a hundred and fifty metres tall built from one-metre puffs
        /// is a dotted line; smoke broadens as it climbs because it is cooling and mixing, and the
        /// widening is most of what makes a column read as one object rather than as a stream of
        /// separate balls.
        /// </summary>
        private const float EndScale = 24f;

        /// <summary>
        /// Where the smoke goes. One direction for the whole map, because a column that leans the
        /// same way as every other column is what makes it read as WEATHER rather than as an effect
        /// attached to each explosion.
        ///
        /// Applied as a velocity rather than an acceleration, so displacement grows with age: a new
        /// puff is over its crater and an old one is a long way downwind, and the column bends the
        /// further up it goes.
        /// </summary>
        private static readonly Vector3 Wind = new(4.2f, 0f, 1.6f);

        /// <summary>
        /// Per-puff wander, on top of the wind. Without it every puff follows the same path from the
        /// same emitter and the column is a rope; with it the edges fray, which is most of what
        /// distinguishes smoke from a solid shape moving through the air.
        /// </summary>
        private const float DriftSpeed = 1.1f;

        /// <summary>Fraction of its full size a puff starts at, and where in its life it begins to
        /// fade. Both taken from the reference: it billows first and thins later.</summary>
        private const float BirthScale = 1f;

        /// <summary>
        /// Where in its life a puff starts thinning. Early, so the fade tracks the GROWTH: a puff
        /// swells twenty-four fold, and one that stayed solid until two thirds through would spend
        /// most of its life as an opaque ten-metre ball. Thinning as it spreads is what smoke does
        /// and what keeps a column translucent rather than a stack of grey boulders.
        /// </summary>
        private const float FadeStart = 0.3f;

        /// <summary>Dark. The reference was rocket exhaust against a lunar sky; this is what comes
        /// off a shell hole, and it wants to read as dirty rather than as steam.</summary>
        private static readonly Color4 SmokeColor = new(0.26f, 0.25f, 0.24f, 1f);

        private sealed class Puff
        {
            public required Entity Entity { get; init; }
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 Drift;
            public float Age;
            public float Scale;
            public float Spin;
            public bool Live;
        }

        /// <summary>
        /// A source of smoke, alive for a while and throwing puffs the whole time.
        ///
        /// Separate from the puffs because they are not the same thing on different timescales: a
        /// puff lasts seconds and a source lasts minutes, and what a burning thing looks like is
        /// mostly a question of how long it goes on for.
        /// </summary>
        private sealed class Emitter
        {
            public Vector3 Position;
            public float Remaining;
            public float Pending;
            public float Age;
        }

        /// <summary>Two minutes of burning. The default rather than the rule — Emit takes its own
        /// duration.</summary>
        public const float DefaultEmitterSeconds = 120f;

        /// <summary>Puffs a second from one source. Lifetime times rate is how many are alive at
        /// once, so this and <see cref="Lifetime"/> together set the density of a column.</summary>
        private const float PuffsPerSecond = 1.5f;

        /// <summary>
        /// Frames spent showing one puff of every opacity so their shaders compile NOW.
        ///
        /// Each dither level is its own material and therefore its own effect permutation, and Stride
        /// compiles a permutation the first time something using it is about to be drawn — blocking
        /// the render thread while it does. Left alone, that is twelve hitches scattered through the
        /// first column's life, which is to say through the first firefight. Drawing all twelve once
        /// at startup moves the whole cost to a moment where a pause is expected and nobody is being
        /// shot at.
        ///
        /// Two frames because one is not reliably enough: the puffs must be submitted, culled in,
        /// and drawn, and the frame that discovers the effect is missing is not the frame that has
        /// it.
        /// </summary>
        /// <summary>
        /// Least time between one source being retired and the next.
        ///
        /// Without it, retirement CASCADES and takes every column at once. The pool cannot free a
        /// puff in the middle of a frame — they only die of age — so once it is full, every emitter
        /// in the list fails its spawn on that same frame, and each failure retired a different
        /// source. Ten columns became none, instantly, from one frame of pressure.
        ///
        /// So retirement is rate-limited rather than demand-driven: at most one source goes every
        /// few seconds, however many are asking. A burst of new fires thins everything slightly for
        /// a moment instead of wiping the map's history, and only sustained pressure actually closes
        /// anything down.
        /// </summary>
        private const float RetireCooldownSeconds = 4f;
        private float retireCooldown;

        private int warmFrames = 3;
        private readonly List<Entity> warmEntities = [];

        private readonly Game game;
        private readonly Scene scene;
        private readonly List<Puff> puffs = [];
        private readonly List<Emitter> emitters = [];
        private readonly Random random = new();

        public SmokeManager(Game game, Scene scene)
        {
            this.game = game;
            this.scene = scene;
        }

        /// <summary>Starts a column at a point. Purely local: smoke is something the client draws,
        /// not something the world has to agree about, so none of this touches the wire.</summary>
        public void Emit(Vector3 position, float seconds = DefaultEmitterSeconds)
            => emitters.Add(new Emitter { Position = position, Remaining = seconds });

        public int LivePuffs
        {
            get
            {
                int live = 0;
                foreach (var puff in puffs) if (puff.Live) live++;
                return live;
            }
        }

        /// <summary>
        /// One puff, thrown up out of <paramref name="origin"/>. Call it repeatedly to make a
        /// column; the scatter is per puff, so nothing has to track an emitter.
        /// </summary>
        public bool Spawn(Vector3 origin)
        {
            if (TryTake() is not { } puff) return false;

            // Emitted from a small ball rather than a point, so a column has width from the start
            // instead of a stream of puffs leaving the same spot.
            puff.Position = origin + Vector3.UnitY * SpawnRise + InSphere() * EmissionRadius;

            // A tight cone about straight up. Wide spread reads as an explosion; this reads as a
            // plume.
            float spread = SpreadDegrees * MathF.PI / 180f * MathF.Sqrt(Next());
            float around = Next() * MathF.Tau;
            var direction = new Vector3(
                MathF.Sin(spread) * MathF.Cos(around),
                MathF.Cos(spread),
                MathF.Sin(spread) * MathF.Sin(around));

            puff.Velocity = direction * (MinSpeed + Next() * (MaxSpeed - MinSpeed));

            // Its own share of the wander, fixed for life. Re-rolling it every frame would average
            // out to nothing; a puff that commits to a direction is what actually spreads a column.
            puff.Drift = InSphere() * DriftSpeed;
            puff.Scale = MinScale + Next() * (MaxScale - MinScale);
            puff.Spin = Next() * MathF.Tau;
            puff.Age = 0f;
            puff.Live = true;
            Apply(puff);
            return true;
        }

        public void Clear()
        {
            emitters.Clear();
            foreach (var puff in puffs)
            {
                puff.Live = false;
                puff.Entity.Get<ModelComponent>().Enabled = false;
            }
        }

        public void Dispose()
        {
            foreach (var puff in puffs) puff.Entity.Scene = null;
            puffs.Clear();
            foreach (var entity in warmEntities) entity.Scene = null;
            warmEntities.Clear();
        }

        public void Update(float dt)
        {
            if (warmFrames > 0) Warm();

            retireCooldown -= dt;

            for (int i = emitters.Count - 1; i >= 0; i--)
            {
                var emitter = emitters[i];
                emitter.Remaining -= dt;
                emitter.Age += dt;

                // Accumulated rather than one-per-frame: the rate is a rate, so a slow frame emits
                // what it owes instead of quietly thinning the column whenever the machine is busy.
                emitter.Pending += dt * PuffsPerSecond;
                while (emitter.Pending >= 1f)
                {
                    emitter.Pending -= 1f;
                    if (Spawn(emitter.Position)) continue;

                    // Out of puffs. Retire the OLDEST SOURCE rather than recycling the oldest puff,
                    // which is what this used to do and why columns never topped out: the oldest
                    // puff is the highest one, so stealing it decapitated every column at whatever
                    // height the pool happened to run dry. Closing a source instead costs one old
                    // column its tail and lets every puff already in the air finish its climb.
                    //
                    // Rate-limited, because every emitter fails on the same frame the pool fills —
                    // see RetireCooldownSeconds. Outside the cooldown a starved emitter simply drops
                    // this puff and stays in business.
                    if (retireCooldown <= 0f)
                    {
                        RetireOldestEmitter();
                        retireCooldown = RetireCooldownSeconds;
                    }
                    break;
                }

                if (emitter.Remaining <= 0f && emitters.Contains(emitter)) emitters.Remove(emitter);
            }

            foreach (var puff in puffs)
            {
                if (!puff.Live) continue;

                puff.Age += dt;
                if (puff.Age >= Lifetime)
                {
                    puff.Live = false;
                    puff.Entity.Get<ModelComponent>().Enabled = false;
                    continue;
                }

                // Velocity is never touched after launch. Wind and the puff's own wander are added
                // here rather than folded into it, so they stay separable: one is the map's weather
                // and the other is this puff's character.
                puff.Position += (puff.Velocity + Wind + puff.Drift) * dt;
                Apply(puff);
            }
        }

        /// <summary>Where a puff is, how big it has grown, and how much of it is left.</summary>
        private void Apply(Puff puff)
        {
            float life = puff.Age / Lifetime;
            var transform = puff.Entity.Transform;

            transform.Position = puff.Position;
            transform.Scale = new Vector3(puff.Scale * MathUtil.Lerp(BirthScale, EndScale, life));

            // Spun about the vertical so neighbouring puffs are not the same box at the same angle.
            // It costs nothing here and there is nothing else breaking up the silhouette.
            transform.Rotation = Quaternion.RotationY(puff.Spin);

            var model = puff.Entity.Get<ModelComponent>();
            model.Enabled = true;

            // Held solid, then thinned. Fading from birth makes smoke that was never there.
            float alpha = life <= FadeStart
                ? 1f
                : 1f - (life - FadeStart) / (1f - FadeStart);
            model.Materials[0] = Tint(alpha);
        }

        /// <summary>
        /// Puts one puff of each opacity in front of the camera, too small to see, so that every
        /// dither level's shader is compiled during startup rather than mid-match.
        ///
        /// In front of the CAMERA specifically because an effect is only compiled for something
        /// about to be drawn — a puff parked out in the world would be culled and warm nothing.
        /// </summary>
        private void Warm()
        {
            warmFrames--;

            if (LineRenderer.Camera?.Entity is not { } camera)
            {
                // No camera yet, so nothing can be drawn and nothing would compile. Wait rather
                // than spend the budget on a frame that cannot do the job.
                warmFrames++;
                return;
            }

            var ahead = camera.Transform.WorldMatrix.TranslationVector
                + camera.Transform.WorldMatrix.Forward * 2f;

            // Entities of its own, NOT puffs from the pool. Taking them from the pool is what broke
            // this the first time: a warm puff is deliberately never marked live, so every pass of
            // this loop was handed back the same free puff and only the last material of the twelve
            // ever reached the renderer — one permutation warmed, eleven left to stutter in later.
            if (warmEntities.Count == 0)
            {
                for (int step = 0; step < AlphaSteps; step++)
                {
                    var entity = new Entity($"SmokeWarm{step}")
                    {
                        new ModelComponent(GLTFLoader.LoadModel(game, ModelPath))
                        {
                            Materials = { [0] = Tint(step / (float)(AlphaSteps - 1)) },
                        },
                    };
                    entity.Transform.Scale = new Vector3(0.002f);
                    entity.Scene = scene;
                    warmEntities.Add(entity);
                }
            }

            foreach (var entity in warmEntities)
            {
                entity.Transform.Position = ahead;
                entity.Get<ModelComponent>().Enabled = true;
            }

            // Torn down only once every one of them has had a frame in view with its own material.
            if (warmFrames > 0) return;
            foreach (var entity in warmEntities) entity.Scene = null;
            warmEntities.Clear();
        }

        /// <summary>Stops the longest-running source. Its puffs are untouched and rise as normal;
        /// it simply stops adding to them.</summary>
        private void RetireOldestEmitter()
        {
            if (emitters.Count == 0) return;

            var oldest = emitters[0];
            foreach (var emitter in emitters) if (emitter.Age > oldest.Age) oldest = emitter;
            emitters.Remove(oldest);
        }

        /// <summary>A free puff, or null when the pool is spent. Never recycles a live one — see
        /// the caller for why that matters more than it sounds.</summary>
        private Puff? TryTake()
        {
            foreach (var puff in puffs)
                if (!puff.Live) return puff;

            if (puffs.Count >= MaxPuffs) return null;

            var entity = new Entity("Smoke")
            {
                new ModelComponent(GLTFLoader.LoadModel(game, ModelPath)) { Enabled = false },
            };
            entity.Scene = scene;

            var created = new Puff { Entity = entity };
            puffs.Add(created);
            return created;
        }

        /// <summary>
        /// The puff material at a given opacity, from a small cache of fixed steps.
        ///
        /// Opacity has to live in the MATERIAL: the model comes from the asset pipeline with no
        /// colour channel to write a per-instance alpha into, and every puff shares one mesh. So the
        /// fade is quantised — <see cref="AlphaSteps"/> materials built once and handed round, rather
        /// than a material per puff per frame, which would compile shaders during a firefight.
        ///
        /// Twelve steps across three seconds is a change every quarter second on a thing whose whole
        /// business is being indistinct. Blended rather than cut out, because smoke thinning to
        /// nothing is the effect and a cutout can only pop.
        /// </summary>
        private const int AlphaSteps = 12;
        private readonly Dictionary<int, Material> alphaMaterials = [];

        private Material Tint(float alpha)
        {
            int step = Math.Clamp((int)MathF.Round(alpha * (AlphaSteps - 1)), 0, AlphaSteps - 1);
            if (alphaMaterials.TryGetValue(step, out var cached)) return cached;

            float quantised = step / (float)(AlphaSteps - 1);
            var built = Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    // One hull, front faces only. Nothing is gained by drawing the inside of a puff
                    // and it doubles the pixels.
                    CullMode = CullMode.Back,

                    // Fade and normal together — and NO Transparency feature, which is the point.
                    // The dither discards pixels, so a puff is ordinary opaque geometry: it writes
                    // depth, it occludes, it needs no sorting against the hundred others in the
                    // column, and one hidden behind another costs nothing.
                    Surface = new MaterialSmokeSurfaceFeature(quantised),
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(SmokeColor)),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                },
            });

            alphaMaterials[step] = built;
            return built;
        }

        private Vector3 InSphere()
        {
            float y = Next() * 2f - 1f;
            float around = Next() * MathF.Tau;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y)) * MathF.Cbrt(Next());
            return new Vector3(r * MathF.Cos(around), y * MathF.Cbrt(Next()), r * MathF.Sin(around));
        }

        private float Next() => (float)random.NextDouble();
    }
}
