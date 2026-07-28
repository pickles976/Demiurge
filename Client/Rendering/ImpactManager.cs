using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Demiurge
{
    /// <summary>
    /// The dust kick where a bullet meets the ground: a handful of short streaks thrown out of the
    /// surface, spreading and fading over a fraction of a second.
    ///
    /// Line segments rather than a particle system, because Stride's particles CRASH on this
    /// platform (stride3d/stride#2496) and are switched off in Program.cs. Reusing
    /// <see cref="LineRenderer"/> means the effect brings no new GPU resources with it — which also
    /// matters here, since terrain already taught us what per-object buffer allocation costs on this
    /// backend.
    ///
    /// Ticked from <see cref="TracerSystem"/> alongside <see cref="TracerManager"/>: same lifetime
    /// model, same immediate-mode redraw every frame.
    /// </summary>
    public static class ImpactManager
    {
        private const int Streaks = 7;

        /// <summary>Short enough to read as a puff rather than a firework.</summary>
        private const float Lifetime = 0.22f;

        /// <summary>How far the debris travels in that time, in metres.</summary>
        private const float Reach = 0.32f;

        /// <summary>
        /// How wide the spray opens away from the surface normal. Fully perpendicular would look like
        /// a flat splash and straight along the normal like a jet; leaning most of the way out gives
        /// the shallow cone a real ricochet throws.
        /// </summary>
        private const float Spread = 0.75f;

        private struct Impact
        {
            public Vector3 Point;
            public Vector3 Normal;
            public float Age;
            public int Seed;
            public Color BaseColor;
        }

        private static readonly List<Impact> Impacts = new();
        private static int nextSeed;

        /// <summary>Kick up debris at a point on a surface, thrown out along its normal.</summary>
        public static void Spawn(Vector3 point, Vector3 normal, Color color)
        {
            if (normal.LengthSquared() < 1e-6f) normal = Vector3.UnitY;
            normal.Normalize();

            Impacts.Add(new Impact { Point = point, Normal = normal, Age = 0f, Seed = nextSeed++, BaseColor = color });
        }

        /// <summary>Advance every puff, redraw it, and drop the finished ones. Once per frame.</summary>
        public static void Update(float dt)
        {
            // Reverse iteration so RemoveAt doesn't disturb pending indices.
            for (int i = Impacts.Count - 1; i >= 0; i--)
            {
                var impact = Impacts[i];
                impact.Age += dt;

                if (impact.Age >= Lifetime)
                {
                    Impacts.RemoveAt(i);
                    continue;
                }

                Draw(impact);
                Impacts[i] = impact;
            }
        }

        private static void Draw(in Impact impact)
        {
            float t = impact.Age / Lifetime;

            // Debris decelerates: most of the travel happens immediately, which is what separates a
            // scatter from a steady expansion.
            float travel = Reach * MathF.Sqrt(t);

            var color = impact.BaseColor;
            color.A = (byte)(MathUtil.Clamp(1f - t, 0f, 1f) * impact.BaseColor.A);

            // Any two axes perpendicular to the normal. Cross with whichever world axis the normal is
            // least aligned to, so the pair never degenerates on a wall or a ceiling.
            var reference = MathF.Abs(impact.Normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            var right = Vector3.Normalize(Vector3.Cross(impact.Normal, reference));
            var up = Vector3.Cross(right, impact.Normal);

            for (int s = 0; s < Streaks; s++)
            {
                // Deterministic per streak, so a puff keeps its shape for its whole life instead of
                // reshuffling every frame — the same reason the seed is stored rather than the
                // directions being re-randomised.
                float angle = Hash(impact.Seed, s) * MathF.PI * 2f;
                float length = 0.55f + 0.45f * Hash(impact.Seed, s + 64);

                var direction = Vector3.Normalize(
                    impact.Normal + Spread * (right * MathF.Cos(angle) + up * MathF.Sin(angle)));

                var start = impact.Point + direction * (travel * 0.35f);
                LineRenderer.DrawLine(start, impact.Point + direction * travel * length, color);
            }
        }

        /// <summary>Cheap deterministic noise in [0,1). Only needs to look unpatterned.</summary>
        private static float Hash(int seed, int index)
        {
            uint h = (uint)(seed * 73856093) ^ (uint)(index * 19349663);
            h ^= h >> 13;
            h *= 0x85EBCA6B;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
