using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Demiurge
{
    /// <summary>
    /// Scatters many copies of the grass model over a rectangle and renders them
    /// all in a single draw call via GPU instancing (InstancingComponent +
    /// InstancingUserArray).
    ///
    /// Instancing is purely visual: physics and per-entity scripts know nothing
    /// about the copies. The instance matrices are absolute world transforms
    /// (ModelTransformUsage.Ignore, the default), so the entity's own transform
    /// is irrelevant — position the field via <paramref name="center"/>.
    ///
    /// The whole field is one render object with one merged bounding box, so it
    /// frustum-culls all-or-nothing. If the field grows large, create several
    /// smaller fields (chunks) instead of one big one.
    /// </summary>

    public static class GrassField
    {
        public static Entity Create(
            Game game,
            Vector3 center,
            float sizeX = 50f,
            float sizeZ = 50f,
            float cellSize = 0.5f,
            int seed = 12345
        )
        {
            EnsureInstancingRenderFeature(game);

            var entity = new Entity("GrassField")
            {
                new ModelComponent(game.Content.Load<Model>("models/grass")),
            };

            entity.Get<ModelComponent>().Materials[0] = CreateMaterial(game);
            var instancing = new InstancingUserArray();
            instancing.UpdateWorldMatrices(ScatterMatrices(center, sizeX, sizeZ, cellSize, seed));
            entity.Add(new InstancingComponent { Type = instancing });

            return entity;
        }

        /// <summary>
        /// Double-sided cutout material: both faces of the quads render, and
        /// texels below the alpha threshold are discarded in the color pass AND
        /// the shadow pass (the cutoff feature enables the pixel shader during
        /// depth-only rendering).
        /// </summary>
        private static Material CreateMaterial(Game game)
        {
            var texture = game.Content.Load<Texture>("models/grass_tex0");

            return Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                  {
                      CullMode = CullMode.None,
                      Diffuse = new MaterialDiffuseMapFeature(
                          new ComputeTextureColor(texture) { Filtering = TextureFilter.Point }),
                      DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                      Transparency = new MaterialTransparencyCutoffFeature
                      {
                          Alpha = new ComputeFloat(0.05f),
                      },
                  },
            });
        }

        /// <summary>
        /// One instance per grid cell, jittered inside its cell so no rows or
        /// columns read at a glance, with random yaw and slight scale variation
        /// so the copies don't look stamped. Deterministic for a given seed.
        /// </summary>
        private static Matrix[] ScatterMatrices(Vector3 center, float sizeX, float sizeZ, float cellSize, int seed)
        {
            int nx = Math.Max(1, (int)(sizeX / cellSize));
            int nz = Math.Max(1, (int)(sizeZ / cellSize));
            var rng = new Random(seed);
            var matrices = new Matrix[nx * nz];

            float startX = center.X - sizeX * 0.5f;
            float startZ = center.Z - sizeZ * 0.5f;
            int i = 0;

            for (int ix = 0; ix < nx; ix++)
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    var position = new Vector3(
                        startX + (ix + rng.NextSingle()) * cellSize,
                        center.Y,
                        startZ + (iz + rng.NextSingle()) * cellSize);

                    float yaw = rng.NextSingle() * MathUtil.TwoPi;
                    float scale = 0.8f + rng.NextSingle() * 0.4f;

                    matrices[i++] = Matrix.Scaling(scale)
                                  * Matrix.RotationY(yaw)
                                  * Matrix.Translation(position);
                }
            }

            return matrices;
        }

        /// <summary>
        /// The default compositor has no InstancingRenderFeature, so instanced
        /// entities silently render a single copy without this. Idempotent;
        /// must run after the compositor exists (game.AddGraphicsCompositor()).
        /// </summary>
        private static void EnsureInstancingRenderFeature(Game game)
        {
            var meshRenderFeature = game.SceneSystem.GraphicsCompositor.RenderFeatures
                .OfType<MeshRenderFeature>()
                .First();

            if (!meshRenderFeature.RenderFeatures.Any(f => f is InstancingRenderFeature))
                meshRenderFeature.RenderFeatures.Add(new InstancingRenderFeature());
        }

    }

}