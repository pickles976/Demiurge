using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Client-side bullet tracers. One streak per shot: a world-space line from the
    /// barrel to the shot endpoint that fades to transparent over a short lifetime.
    ///
    /// Drawn through <see cref="LineRenderer"/>, but unlike that immediate-mode queue
    /// tracers persist across frames here and are re-submitted (faded) every frame
    /// until they expire. Tick once per frame from <see cref="TracerSystem"/>.
    /// </summary>
    public static class TracerManager
    {
        private struct Tracer
        {
            public Vector3 Start;
            public Vector3 End;
            public float Age;
            public float Lifetime;
            public Color BaseColor;
        }

        private static readonly List<Tracer> Tracers = new();

        /// <summary>Queue a tracer streak from start to end that fades over lifetime seconds.</summary>
        public static void Spawn(Vector3 start, Vector3 end, Color color, float lifetime)
        {
            Tracers.Add(new Tracer { Start = start, End = end, Age = 0f, Lifetime = lifetime, BaseColor = color });
        }

        /// <summary>Advance all tracers, re-draw them faded, and drop expired ones. Call once per frame.</summary>
        public static void Update(float dt)
        {
            // Reverse iteration so RemoveAt doesn't disturb pending indices.
            for (int i = Tracers.Count - 1; i >= 0; i--)
            {
                var t = Tracers[i];
                t.Age += dt;
                if (t.Age >= t.Lifetime)
                {
                    Tracers.RemoveAt(i);
                    continue;
                }

                // Linear alpha fade over the lifetime. Width is global to LineRenderer,
                // so only the colour's alpha animates here.
                float alpha = 1f - t.Age / t.Lifetime;
                var color = t.BaseColor;
                color.A = (byte)(MathUtil.Clamp(alpha, 0f, 1f) * t.BaseColor.A);
                LineRenderer.DrawLine(t.Start, t.End, color);

                Tracers[i] = t;
            }
        }
    }

    /// <summary>
    /// Drives <see cref="TracerManager"/> once per frame. Add a single instance of this
    /// to the scene; guns just call <see cref="TracerManager.Spawn"/>.
    /// </summary>
    public class TracerSystem : SyncScript
    {
        public override void Update()
        {
            TracerManager.Update((float)Game.UpdateTime.Elapsed.TotalSeconds);
        }
    }
}
