using Stride.Core;
using Stride.Engine;
using Stride.Rendering;

namespace Demiurge
{
    /// <summary>
    /// How many triangles the renderer was actually asked to draw last frame.
    ///
    /// Counted off <see cref="RenderView.RenderObjects"/>, which is the set that SURVIVED CULLING —
    /// not everything in the scene. That distinction is the whole value of the number: the scene
    /// total tells you how much geometry exists, which you already know from the code, while this
    /// tells you what the frame is paying for, which is the thing that moves when an LOD threshold
    /// or a view distance changes.
    ///
    /// Summed across every render view, because a view is a camera OR a shadow pass, and geometry
    /// drawn twice costs twice. The view count is reported alongside so a number that doubles
    /// because a second view appeared cannot be mistaken for one that doubled because the world got
    /// denser.
    /// </summary>
    public static class TriangleCounter
    {
        public readonly record struct Frame(long Triangles, int Meshes, int Views);

        public static Frame Sample(IServiceRegistry services)
        {
            var render = services.GetService<SceneSystem>()?.GraphicsCompositor?.RenderSystem;
            if (render is null) return default;

            long triangles = 0;
            int meshes = 0;
            int views = 0;

            foreach (var view in render.Views)
            {
                views++;
                foreach (var candidate in view.RenderObjects)
                {
                    // Meshes only. A render object may be a sprite, a light or a background, and
                    // none of those has a triangle count worth adding to this one.
                    if (candidate is not RenderMesh mesh) continue;

                    meshes++;

                    // DrawCount is INDICES for an indexed draw and vertices otherwise, and either way
                    // three of them make a triangle — the primitive type is a triangle list
                    // everywhere in this project.
                    triangles += mesh.Mesh.Draw.DrawCount / 3;
                }
            }

            return new Frame(triangles, meshes, views);
        }
    }
}
