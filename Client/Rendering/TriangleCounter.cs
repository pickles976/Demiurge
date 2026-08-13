using Stride.Core;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Compositing;

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
    /// <summary>
    /// Samples the visible-object counts from inside the DRAW phase, which is the only place they
    /// exist.
    ///
    /// RenderSystem.Views is empty when read from Update — measured, not assumed: the counter
    /// reported `0 views` from there every second. The compositor populates the view list while
    /// drawing and it is gone again by the time scripts run, so a post-cull count cannot be taken
    /// from the session's update at all. A scene renderer runs in the right phase, and the
    /// compositor already carries one for line drawing.
    /// </summary>
    public sealed class GeometryStatsRenderer : SceneRendererBase
    {
        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            long triangles = 0;
            int meshes = 0;
            int views = 0;

            foreach (var view in context.RenderSystem.Views)
            {
                views++;
                foreach (var candidate in view.RenderObjects)
                {
                    if (candidate is not RenderMesh mesh) continue;
                    meshes++;
                    triangles += TriangleCounter.Triangles(mesh.Mesh);
                }
            }

            TriangleCounter.ReportDrawn(triangles, meshes, views);
        }
    }

    public static class TriangleCounter
    {
        // Written on the render thread, read on the main one, and neither cares about tearing: it is
        // a diagnostic printed once a second, and a stale or half-updated triple says the same thing
        // about a frame as a fresh one.
        private static long drawnTriangles;
        private static int drawnMeshes;
        private static int drawnViews;

        public static void ReportDrawn(long triangles, int meshes, int views)
        {
            drawnTriangles = triangles;
            drawnMeshes = meshes;
            drawnViews = views;
        }

        public readonly record struct Frame(
            long SceneTriangles,
            int SceneMeshes,
            long DrawnTriangles,
            int DrawnMeshes,
            int Views)
        {
            public bool HasDrawn => DrawnMeshes > 0;
        }

        public static Frame Sample(Scene scene)
        {
            long sceneTriangles = 0;
            int sceneMeshes = 0;
            Walk(scene, ref sceneTriangles, ref sceneMeshes);

            return new Frame(
                sceneTriangles, sceneMeshes, drawnTriangles, drawnMeshes, drawnViews);
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
        internal static long Triangles(Mesh mesh) => mesh.Draw is { } draw ? draw.DrawCount / 3 : 0;
    }
}
