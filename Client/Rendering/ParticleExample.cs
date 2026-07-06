using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles;
using Stride.Particles.Components;
using Stride.Particles.Initializers;
using Stride.Particles.Materials;
using Stride.Particles.Modules;
using Stride.Particles.ShapeBuilders;
using Stride.Particles.Spawners;
using Stride.Rendering.Materials.ComputeColors;

namespace Demiurge
{
    /// <summary>
    /// REFERENCE ONLY — a fully code-built Stride particle system (no .sdpart asset).
    ///
    /// Stride's docs only cover authoring particles in Game Studio; this is the
    /// equivalent runtime API, annotated. Call <see cref="CreateAtOrigin"/> and drop the
    /// returned entity into a scene:
    ///
    ///     var fx = ParticleExample.CreateAtOrigin();
    ///     fx.Scene = rootScene;
    ///
    /// IMPORTANT — rendering prerequisite:
    /// Particles only draw if the graphics compositor includes the particle render stage
    /// (ParticleEmitterRenderFeature). A code-first CommunityToolkit setup does NOT get
    /// this from AddGraphicsCompositor() — you must call game.AddParticleRenderer() once
    /// after building the compositor (or compositor.AddParticleStagesAndFeatures()).
    /// If a ParticleSystemComponent simulates but nothing appears, this is why.
    ///
    /// For production VFX you'd normally author a .sdpart asset in Game Studio and load it
    /// (it bundles the material, textures and curves). Code-building like this is mainly
    /// useful for procedural/programmatic effects — but it's the clearest map of the API.
    /// </summary>
    public static class ParticleExample
    {
        public static Entity CreateAtOrigin()
        {
            // 1. The emitter owns the particle pool plus the modules that spawn,
            //    initialize, update, and draw its particles.
            var emitter = new ParticleEmitter
            {
                // (MaxParticles is computed from lifetime x spawn rate; use
                //  MaxParticlesOverride only if you need a hard cap.)
                // Per-particle lifetime range in seconds (X = min, Y = max).
                ParticleLifetime = new Vector2(1.0f, 2.0f),
                // World: once spawned, particles live in world space and ignore later
                // emitter movement (good for sparks/smoke). Local: particles stay relative
                // to the emitter transform (good for an attached aura/trail).
                SimulationSpace = EmitterSimulationSpace.World,
                // How each particle becomes geometry. Billboard = camera-facing quad.
                // Alternatives: ShapeBuilderRibbon / ShapeBuilderTrail for streaks.
                ShapeBuilder = new ShapeBuilderBillboard(),
                // Flat-coloured material (no texture). For real sprites, give the material
                // a texture map and a UVBuilder (e.g. UVBuilderFlipbook for animation).
                Material = new ParticleMaterialComputeColor
                {
                    ComputeColor = new ComputeColor(new Color4(1f, 0.6f, 0.1f, 1f)),
                },
            };

            // 2. Spawner — how many particles appear and when. Continuous stream here.
            //    For a single puff/one-shot, use a SpawnerBurst with its LoopCondition set
            //    to SpawnerLoopCondition.OneShot and re-arm it with ParticleSystem.ResetSimulation().
            emitter.Spawners.Add(new SpawnerPerSecond { SpawnCount = 60f });

            // 3. Initializers — set each particle's starting state at birth.
            //    Small spawn spread around the emitter origin.
            emitter.Initializers.Add(new InitialPositionSeed
            {
                PositionMin = new Vector3(-0.1f, 0f, -0.1f),
                PositionMax = new Vector3(0.1f, 0f, 0.1f),
            });
            //    Initial velocity range — mostly upward with a slight horizontal spread.
            emitter.Initializers.Add(new InitialVelocitySeed
            {
                VelocityMin = new Vector3(-0.5f, 2.0f, -0.5f),
                VelocityMax = new Vector3(0.5f, 3.0f, 0.5f),
            });
            //    Initial size range (X = min, Y = max) in world units.
            emitter.Initializers.Add(new InitialSizeSeed
            {
                RandomSize = new Vector2(0.05f, 0.15f),
            });
            //    Initial colour tint range (lerped per particle between min and max).
            emitter.Initializers.Add(new InitialColorSeed
            {
                ColorMin = new Color4(1f, 0.4f, 0.0f, 1f),
                ColorMax = new Color4(1f, 0.9f, 0.3f, 1f),
            });

            // 4. Updaters — modify particles every frame while alive. Light downward pull;
            //    use Vector3.Zero for floaty smoke. See also UpdaterColorOverTime /
            //    UpdaterSizeOverTime for curve-driven fades.
            emitter.Updaters.Add(new UpdaterGravity
            {
                GravitationalAcceleration = new Vector3(0f, -1.5f, 0f),
            });

            // 5. The component wraps a ParticleSystem (auto-created) holding the emitters.
            var component = new ParticleSystemComponent();
            component.ParticleSystem.Emitters.Add(emitter);

            // 6. Put it on an entity at the world origin.
            var entity = new Entity("ParticleExample") { component };
            entity.Transform.Position = Vector3.Zero;
            return entity;
        }
    }
}
