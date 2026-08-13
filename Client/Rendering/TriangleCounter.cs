using Stride.Core;
using Stride.Engine;
using Stride.Rendering;

namespace Demiurge
{
    /// <summary>
    /// How much geometry the scene is carrying, and how much of it survived culling.
    ///
    /// Two numbers because they answer different questions and the first is the only one that can be
    /// trusted from here. The SCENE total is walked off the entity tree, which is stable whenever we
    /// look at it. The DRAWN total comes from each render view's visible-object collector, which is
    /// filled during Draw — sampling it from Update reads whatever the last frame left behind, and
    /// early on reads nothing at all, which is why the first version of this reported zero.
    ///
    /// So drawn is reported only when it has something in it, and labelled separately. A missing
    /// drawn figure means the sample landed before the renderer had filled anything, not that
    /// nothing is on screen.
    /// </summary>
    public static class TriangleCounter
    {
        public readonly record struct Frame(
            long SceneTriangles,
            int SceneMeshes,
            long DrawnTriangles,
            int DrawnMeshes,
            int Views)
        {
            public bool HasDrawn => DrawnMeshes > 0;
        }

        public static Frame Sample(IServiceRegistry services, Scene scene)
        {
            long sceneTriangles = 0;
            int sceneMeshes = 0;
            Walk(scene, ref sceneTriangles, ref sceneMeshes);

            long drawnTriangles = 0;
            int drawnMeshes = 0;
            int views = 0;

            if (services.GetService<SceneSystem>()?.GraphicsCompositor?.RenderSystem is { } render)
            {
                foreach (var view in render.Views)
                {
                    views++;
                    foreach (var candidate in view.RenderObjects)
                    {
                        // Meshes only. A render object may be a sprite, a light or a background, and
                        // none of those has a triangle count worth adding to this one.
                        if (candidate is not RenderMesh mesh) continue;

                        drawnMeshes++;
                        drawnTriangles += Triangles(mesh.Mesh);
                    }
                }
            }

            return new Frame(sceneTriangles, sceneMeshes, drawnTriangles, drawnMeshes, views);
        }

        /// <summary>
        /// Every enabled model component under a scene, children included — the transform hierarchy
        /// is where tree canopies and leaf cards live, and a walk of scene.Entities alone would miss
        /// every one of them.
        /// </summary>
        private static void Walk(Scene scene, ref long triangles, ref int meshes)
        {
            foreach (var entity in scene.Entities) Walk(entity, ref triangles, ref meshes);
            foreach (var child in scene.Children) Walk(child, ref triangles, ref meshes);
        }

        private static void Walk(Entity entity, ref long triangles, ref int meshes)
        {
            if (entity.Get<ModelComponent>() is { Enabled: true, Model: { } model })
            {
                foreach (var mesh in model.Meshes)
                {
                    meshes++;
                    triangles += Triangles(mesh);
                }
            }

            foreach (var child in entity.Transform.Children)
                Walk(child.Entity, ref triangles, ref meshes);
        }

        /// <summary>DrawCount is INDICES for an indexed draw and vertices otherwise, and either way
        /// three of them make a triangle — every primitive in this project is a triangle list.</summary>
        private static long Triangles(Mesh mesh) => mesh.Draw is { } draw ? draw.DrawCount / 3 : 0;
    }
}
