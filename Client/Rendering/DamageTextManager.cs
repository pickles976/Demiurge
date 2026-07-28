using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Demiurge
{
    /// <summary>
    /// Floating world-space damage numbers, drawn as camera-facing line glyphs. This deliberately uses
    /// LineRenderer rather than Stride's FastTextRenderer, which is disabled elsewhere for Vulkan.
    /// </summary>
    public static class DamageTextManager
    {
        private const float Lifetime = 0.85f;
        private const float RiseSpeed = 1.0f;
        private const float Scale = 0.42f;
        private const float DigitWidth = 0.55f;
        private const float DigitGap = 0.16f;

        private readonly struct DamageText
        {
            public readonly Vector3 Position;
            public readonly string Text;
            public readonly Color Color;
            public readonly float Age;

            public DamageText(Vector3 position, string text, Color color, float age)
            {
                Position = position;
                Text = text;
                Color = color;
                Age = age;
            }
        }

        private static readonly List<DamageText> Texts = new();

        public static void Spawn(Vector3 position, ushort damage, Color color)
        {
            if (!IsFinite(position) || damage == 0) return;
            Texts.Add(new DamageText(position, damage.ToString(), color, 0f));
        }

        public static void Update(float dt)
        {
            var camera = LineRenderer.Camera?.Entity;
            if (camera == null) return;

            var right = camera.Transform.Rotation * Vector3.UnitX;
            var up = camera.Transform.Rotation * Vector3.UnitY;

            for (int i = Texts.Count - 1; i >= 0; i--)
            {
                var text = Texts[i];
                float age = text.Age + dt;
                if (age >= Lifetime)
                {
                    Texts.RemoveAt(i);
                    continue;
                }

                float t = age / Lifetime;
                var color = text.Color;
                color.A = (byte)(MathUtil.Clamp(1f - t, 0f, 1f) * text.Color.A);
                Draw(text.Position + Vector3.UnitY * (age * RiseSpeed), text.Text, color, right, up);

                Texts[i] = new DamageText(text.Position, text.Text, text.Color, age);
            }
        }

        private static void Draw(Vector3 origin, string text, Color color, Vector3 right, Vector3 up)
        {
            float total = text.Length * DigitWidth + MathF.Max(0, text.Length - 1) * DigitGap;
            var cursor = origin - right * (total * Scale * 0.5f);

            foreach (char c in text)
            {
                DrawDigit(cursor, c, color, right, up);
                cursor += right * ((DigitWidth + DigitGap) * Scale);
            }
        }

        private static void DrawDigit(Vector3 origin, char digit, Color color, Vector3 right, Vector3 up)
        {
            ReadOnlySpan<byte> segments = digit switch
            {
                '0' => [1, 1, 1, 0, 1, 1, 1],
                '1' => [0, 0, 1, 0, 0, 1, 0],
                '2' => [1, 0, 1, 1, 1, 0, 1],
                '3' => [1, 0, 1, 1, 0, 1, 1],
                '4' => [0, 1, 1, 1, 0, 1, 0],
                '5' => [1, 1, 0, 1, 0, 1, 1],
                '6' => [1, 1, 0, 1, 1, 1, 1],
                '7' => [1, 0, 1, 0, 0, 1, 0],
                '8' => [1, 1, 1, 1, 1, 1, 1],
                '9' => [1, 1, 1, 1, 0, 1, 1],
                _ => [],
            };
            if (segments.Length == 0) return;

            Segment(segments, 0, origin, color, right, up, 0f, 1f, 1f, 1f);     // top
            Segment(segments, 1, origin, color, right, up, 0f, 0.5f, 0f, 1f);   // upper-left
            Segment(segments, 2, origin, color, right, up, 1f, 0.5f, 1f, 1f);   // upper-right
            Segment(segments, 3, origin, color, right, up, 0f, 0.5f, 1f, 0.5f); // middle
            Segment(segments, 4, origin, color, right, up, 0f, 0f, 0f, 0.5f);   // lower-left
            Segment(segments, 5, origin, color, right, up, 1f, 0f, 1f, 0.5f);   // lower-right
            Segment(segments, 6, origin, color, right, up, 0f, 0f, 1f, 0f);     // bottom
        }

        private static void Segment(ReadOnlySpan<byte> segments, int index, Vector3 origin, Color color,
                                    Vector3 right, Vector3 up, float ax, float ay, float bx, float by)
        {
            if (segments[index] == 0) return;

            var a = origin + (right * (ax * DigitWidth) + up * ay) * Scale;
            var b = origin + (right * (bx * DigitWidth) + up * by) * Scale;
            LineRenderer.DrawLine(a, b, color);
        }

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
