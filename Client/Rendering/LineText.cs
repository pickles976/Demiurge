using Stride.Core.Mathematics;

namespace Demiurge;

/// <summary>
/// Short world-space labels, drawn as camera-facing line segments through <see cref="LineRenderer"/>.
///
/// Lines rather than a font because Stride's FastTextRenderer crashes on this platform, so the UI
/// path is the only text Stride will draw — and a UI label anchored to a moving NPC has to be
/// projected through the camera's view-projection matrix, which a script that runs before the camera
/// is posed reads one frame stale. A billboard needs the camera's ORIENTATION and nothing else, so
/// there is no matrix to be stale.
///
/// The glyphs are a sixteen-segment cell, which is the cheapest shape that renders letters legibly
/// rather than approximately. Unknown characters draw nothing, so a label is never a box of noise.
///
/// <see cref="DamageTextManager"/> keeps its own seven-segment digits: those are a tuned combat
/// effect rather than a label, and its proportions are part of how the numbers read. Worth unifying
/// if a third caller ever wants glyphs.
/// </summary>
public static class LineText
{
    /// <summary>Cell width as a fraction of cell height, and the gap between cells.</summary>
    private const float GlyphWidth = 0.62f;
    private const float GlyphGap = 0.22f;

    /// <summary>
    /// Where a segment index lands on the unit cell: bottom-left origin, one unit tall.
    /// Order is the conventional sixteen-segment one — the two top halves, the right verticals,
    /// the two bottom halves, the left verticals, the two middle halves, then the four diagonals
    /// and the two centre verticals that letters need and a calculator display does not.
    /// </summary>
    private static readonly (float ax, float ay, float bx, float by)[] Segments =
    [
        (0f, 1f, 0.5f, 1f),       // 0  top left
        (0.5f, 1f, 1f, 1f),       // 1  top right
        (1f, 1f, 1f, 0.5f),       // 2  upper right
        (1f, 0.5f, 1f, 0f),       // 3  lower right
        (1f, 0f, 0.5f, 0f),       // 4  bottom right
        (0.5f, 0f, 0f, 0f),       // 5  bottom left
        (0f, 0f, 0f, 0.5f),       // 6  lower left
        (0f, 0.5f, 0f, 1f),       // 7  upper left
        (0f, 0.5f, 0.5f, 0.5f),   // 8  middle left
        (0.5f, 0.5f, 1f, 0.5f),   // 9  middle right
        (0f, 1f, 0.5f, 0.5f),     // 10 upper-left diagonal
        (0.5f, 1f, 0.5f, 0.5f),   // 11 upper centre
        (1f, 1f, 0.5f, 0.5f),     // 12 upper-right diagonal
        (0f, 0f, 0.5f, 0.5f),     // 13 lower-left diagonal
        (0.5f, 0f, 0.5f, 0.5f),   // 14 lower centre
        (1f, 0f, 0.5f, 0.5f),     // 15 lower-right diagonal
    ];

    private static ushort Mask(params int[] segments)
    {
        ushort mask = 0;
        foreach (int segment in segments) mask |= (ushort)(1 << segment);
        return mask;
    }

    private static readonly ushort[] Digits =
    [
        Mask(0, 1, 2, 3, 4, 5, 6, 7, 12, 13),   // 0, slashed so it cannot read as O
        Mask(2, 3, 10),
        Mask(0, 1, 2, 8, 9, 6, 5, 4),
        Mask(0, 1, 2, 3, 9, 5, 4),
        Mask(7, 8, 9, 2, 3),
        Mask(0, 1, 7, 8, 9, 3, 5, 4),
        Mask(0, 1, 7, 6, 8, 9, 3, 5, 4),
        Mask(0, 1, 2, 3),
        Mask(0, 1, 2, 3, 4, 5, 6, 7, 8, 9),
        Mask(0, 1, 7, 2, 3, 8, 9, 5, 4),
    ];

    private static readonly ushort[] Letters =
    [
        Mask(0, 1, 2, 3, 6, 7, 8, 9),           // A
        Mask(0, 1, 2, 3, 4, 5, 9, 11, 14),      // B
        Mask(0, 1, 7, 6, 5, 4),                 // C
        Mask(0, 1, 2, 3, 4, 5, 11, 14),         // D
        Mask(0, 1, 7, 6, 8, 9, 5, 4),           // E
        Mask(0, 1, 7, 6, 8),                    // F
        Mask(0, 1, 7, 6, 5, 4, 3, 9),           // G
        Mask(7, 6, 2, 3, 8, 9),                 // H
        Mask(0, 1, 5, 4, 11, 14),               // I
        Mask(2, 3, 5, 4, 6),                    // J
        Mask(7, 6, 8, 12, 15),                  // K
        Mask(7, 6, 5, 4),                       // L
        Mask(7, 6, 10, 12, 2, 3),               // M
        Mask(7, 6, 10, 15, 2, 3),               // N
        Mask(0, 1, 2, 3, 4, 5, 6, 7),           // O
        Mask(0, 1, 2, 7, 6, 8, 9),              // P
        Mask(0, 1, 2, 3, 4, 5, 6, 7, 15),       // Q
        Mask(0, 1, 2, 7, 6, 8, 9, 15),          // R
        Mask(0, 1, 7, 8, 9, 3, 5, 4),           // S
        Mask(0, 1, 11, 14),                     // T
        Mask(7, 6, 5, 4, 3, 2),                 // U
        Mask(7, 6, 13, 12),                     // V
        Mask(7, 6, 13, 15, 2, 3),               // W
        Mask(10, 12, 13, 15),                   // X
        Mask(10, 12, 14),                       // Y
        Mask(0, 1, 12, 13, 5, 4),               // Z
    ];

    /// <summary>
    /// Draws <paramref name="text"/> centred on <paramref name="origin"/> and facing the camera,
    /// with <paramref name="height"/> as the cell height in metres. Silently draws nothing when
    /// there is no camera, which is the state during a session transition.
    /// </summary>
    public static void Draw(Vector3 origin, string text, Color color, float height, bool depthTested = false)
    {
        var camera = LineRenderer.Camera?.Entity;
        if (camera == null || string.IsNullOrEmpty(text)) return;

        var right = camera.Transform.Rotation * Vector3.UnitX * height;
        var up = camera.Transform.Rotation * Vector3.UnitY * height;

        float advance = GlyphWidth + GlyphGap;
        float total = text.Length * GlyphWidth + MathF.Max(0, text.Length - 1) * GlyphGap;
        var cursor = origin - right * (total * 0.5f);

        foreach (char c in text)
        {
            DrawGlyph(cursor, c, color, right, up, depthTested);
            cursor += right * advance;
        }
    }

    private static void DrawGlyph(Vector3 origin, char c, Color color, Vector3 right, Vector3 up, bool depthTested)
    {
        ushort mask = c switch
        {
            >= '0' and <= '9' => Digits[c - '0'],
            >= 'A' and <= 'Z' => Letters[c - 'A'],
            >= 'a' and <= 'z' => Letters[c - 'a'],
            '-' => Mask(8, 9),
            _ => 0,
        };
        if (mask == 0) return;

        for (int i = 0; i < Segments.Length; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            var (ax, ay, bx, by) = Segments[i];
            var a = origin + right * (ax * GlyphWidth) + up * ay;
            var b = origin + right * (bx * GlyphWidth) + up * by;
            if (depthTested) LineRenderer.DrawDepthTestedLine(a, b, color);
            else LineRenderer.DrawLine(a, b, color);
        }
    }
}
