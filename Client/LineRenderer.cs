using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Buffer = Stride.Graphics.Buffer;

namespace Demiurge
{
    /// <summary>
    /// Immediate-mode debug line drawing, modeled on Bevy's gizmos. Call the
    /// Draw* methods from any script's Update(); the accumulated primitives are
    /// rendered and cleared once per frame by <see cref="LineSceneRenderer"/>.
    ///
    /// Everything reduces to line segments: polylines expand into segments and
    /// points expand into small crosses, so there is a single render path.
    ///
    /// 3D coordinates are world-space and projected through <see cref="Camera"/>.
    /// 2D coordinates are pixels with the origin at the screen center and +Y up,
    /// matching Bevy's Camera2d/Isometry2d convention.
    /// </summary>
    public static class LineRenderer
    {
        /// <summary>World-space camera used to project 3D lines. Set once at startup.</summary>
        public static CameraComponent? Camera;

        /// <summary>Core line thickness in pixels.</summary>
        public static float Width = 2f;

        /// <summary>Antialiasing falloff width in pixels added to each edge.</summary>
        public static float Feather = 1f;

        internal readonly struct Segment2D
        {
            public readonly Vector2 A, B;
            public readonly Color Color;
            public Segment2D(Vector2 a, Vector2 b, Color color) { A = a; B = b; Color = color; }
        }

        internal readonly struct Segment3D
        {
            public readonly Vector3 A, B;
            public readonly Color Color;
            public Segment3D(Vector3 a, Vector3 b, Color color) { A = a; B = b; Color = color; }
        }

        internal static readonly List<Segment2D> Segments2D = new();
        internal static readonly List<Segment3D> Segments3D = new();

        // --- 3D (world space) ---

        public static void DrawLine(Vector3 a, Vector3 b, Color color)
            => Segments3D.Add(new Segment3D(a, b, color));

        public static void DrawPolyline(IReadOnlyList<Vector3> points, Color color, bool closed = false)
        {
            for (int i = 0; i + 1 < points.Count; i++)
                DrawLine(points[i], points[i + 1], color);
            if (closed && points.Count > 2)
                DrawLine(points[points.Count - 1], points[0], color);
        }

        /// <summary>A 3-axis cross marking a world position.</summary>
        public static void DrawPoint(Vector3 p, Color color, float size = 0.1f)
        {
            DrawLine(p - Vector3.UnitX * size, p + Vector3.UnitX * size, color);
            DrawLine(p - Vector3.UnitY * size, p + Vector3.UnitY * size, color);
            DrawLine(p - Vector3.UnitZ * size, p + Vector3.UnitZ * size, color);
        }

        // --- 2D (pixels, origin = screen center, +Y up) ---

        public static void DrawLine2D(Vector2 a, Vector2 b, Color color)
            => Segments2D.Add(new Segment2D(a, b, color));

        public static void DrawPolyline2D(IReadOnlyList<Vector2> points, Color color, bool closed = false)
        {
            for (int i = 0; i + 1 < points.Count; i++)
                DrawLine2D(points[i], points[i + 1], color);
            if (closed && points.Count > 2)
                DrawLine2D(points[points.Count - 1], points[0], color);
        }

        /// <summary>A small "+" cross marking a screen position.</summary>
        public static void DrawPoint2D(Vector2 p, Color color, float size = 5f)
        {
            DrawLine2D(p - Vector2.UnitX * size, p + Vector2.UnitX * size, color);
            DrawLine2D(p - Vector2.UnitY * size, p + Vector2.UnitY * size, color);
        }

        /// <summary>A screen-space circle outline, built from a closed polyline.</summary>
        public static void Circle2D(Vector2 center, float radius, Color color, int segments = 64)
        {
            var points = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)segments * MathUtil.TwoPi;
                points[i] = center + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
            }
            DrawPolyline2D(points, color, closed: true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LineVertex
    {
        public Vector4 Position;
        public Color Color;
        // x = signed distance from centerline (px), y = core half-width (px), z = feather (px)
        public Vector3 Edge;

        public LineVertex(Vector4 position, Color color, Vector3 edge)
        {
            Position = position;
            Color = color;
            Edge = edge;
        }

        public static readonly VertexDeclaration Layout = new VertexDeclaration(
            new VertexElement("POSITION", PixelFormat.R32G32B32A32_Float),
            new VertexElement("COLOR", PixelFormat.R8G8B8A8_UNorm),
            new VertexElement("TEXCOORD", PixelFormat.R32G32B32_Float));
    }

    /// <summary>
    /// Consumes the segments queued on <see cref="LineRenderer"/> each frame and
    /// draws them as a single LineList. Added to the graphics compositor after the
    /// scene + UI so debug lines render on top. Immediate-mode: the queues are
    /// cleared after drawing.
    /// </summary>
    public class LineSceneRenderer : SceneRendererBase
    {
        private EffectInstance _effect = null!;
        private MutablePipelineState _pipelineState = null!;
        private Buffer? _vertexBuffer;
        private LineVertex[] _vertices = new LineVertex[256];

        protected override void InitializeCore()
        {
            base.InitializeCore();

            var effectSystem = Services.GetSafeServiceAs<EffectSystem>();
            _effect = new EffectInstance(effectSystem.LoadEffect("LineColorShader").WaitForResult());
            _pipelineState = new MutablePipelineState(GraphicsDevice);
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            var segments2D = LineRenderer.Segments2D;
            var segments3D = LineRenderer.Segments3D;
            // Each segment becomes a quad: 2 triangles = 6 vertices.
            int maxVertices = (segments2D.Count + segments3D.Count) * 6;
            if (maxVertices == 0)
                return;

            var commandList = drawContext.CommandList;
            var viewport = commandList.Viewport;
            float vpW = viewport.Width;
            float vpH = viewport.Height;

            if (_vertices.Length < maxVertices)
                _vertices = new LineVertex[maxVertices];

            float halfWidth = LineRenderer.Width * 0.5f;
            float feather = LineRenderer.Feather;
            int v = 0;

            // 2D: pixels (center origin, +Y up) -> clip space (w = 1).
            float scaleX = 2f / vpW;
            float scaleY = 2f / vpH;
            foreach (var s in segments2D)
            {
                var c0 = new Vector4(s.A.X * scaleX, s.A.Y * scaleY, 0f, 1f);
                var c1 = new Vector4(s.B.X * scaleX, s.B.Y * scaleY, 0f, 1f);
                EmitSegment(c0, c1, s.Color, halfWidth, feather, vpW, vpH, ref v);
            }

            // 3D: world -> clip via the camera ViewProjection (skipped if no camera).
            if (segments3D.Count > 0 && LineRenderer.Camera != null)
            {
                var viewProjection = LineRenderer.Camera.ViewProjectionMatrix;
                foreach (var s in segments3D)
                {
                    var c0 = Vector4.Transform(new Vector4(s.A, 1f), viewProjection);
                    var c1 = Vector4.Transform(new Vector4(s.B, 1f), viewProjection);
                    // Skip segments touching/behind the camera plane (perspective divide blows up).
                    if (c0.W <= 1e-4f || c1.W <= 1e-4f)
                        continue;
                    EmitSegment(c0, c1, s.Color, halfWidth, feather, vpW, vpH, ref v);
                }
            }

            int drawnVertices = v;

            // Immediate-mode: drop everything we are about to draw (or skipped).
            segments2D.Clear();
            segments3D.Clear();

            if (drawnVertices == 0)
                return;

            // Upload. Grow (and reallocate) only when the queue outgrows the GPU buffer.
            if (_vertexBuffer == null || _vertexBuffer.ElementCount < _vertices.Length)
            {
                _vertexBuffer?.Dispose();
                _vertexBuffer = Buffer.Vertex.New(GraphicsDevice, _vertices, GraphicsResourceUsage.Dynamic);
            }
            else
            {
                _vertexBuffer.SetData(commandList, _vertices);
            }

            _effect.UpdateEffect(GraphicsDevice);

            _pipelineState.State.SetDefaults();
            _pipelineState.State.RootSignature = _effect.RootSignature;
            _pipelineState.State.EffectBytecode = _effect.Effect.Bytecode;
            _pipelineState.State.PrimitiveType = PrimitiveType.TriangleList;
            _pipelineState.State.InputElements = LineVertex.Layout.CreateInputElements();
            _pipelineState.State.RasterizerState = RasterizerStates.CullNone;
            _pipelineState.State.BlendState = BlendStates.NonPremultiplied;
            _pipelineState.State.DepthStencilState = DepthStencilStates.None;
            _pipelineState.State.Output.CaptureState(commandList);
            _pipelineState.Update();

            commandList.SetPipelineState(_pipelineState.CurrentState);
            _effect.Apply(drawContext.GraphicsContext);
            commandList.SetVertexBuffer(0, _vertexBuffer, 0, LineVertex.Layout.VertexStride);
            commandList.Draw(drawnVertices);
        }

        // Expands one clip-space segment into a screen-space-thick, feathered quad
        // (two triangles). The perpendicular offset is computed in screen space and
        // pushed back into clip space (scaled by w) so the thickness stays constant
        // in pixels regardless of perspective.
        private void EmitSegment(Vector4 c0, Vector4 c1, Color color, float halfWidth, float feather, float vpW, float vpH, ref int v)
        {
            float ext = halfWidth + feather;

            // Screen-space positions (pixels). Only magnitude/direction matter here.
            var s0 = new Vector2(c0.X / c0.W * 0.5f * vpW, c0.Y / c0.W * 0.5f * vpH);
            var s1 = new Vector2(c1.X / c1.W * 0.5f * vpW, c1.Y / c1.W * 0.5f * vpH);

            var dir = s1 - s0;
            if (dir.LengthSquared() < 1e-8f)
                dir = new Vector2(1f, 0f);
            dir.Normalize();
            var normal = new Vector2(-dir.Y, dir.X);

            // Perpendicular offset expressed in NDC (applied per-endpoint, scaled by w).
            var offsetNdc = new Vector2(normal.X * ext * 2f / vpW, normal.Y * ext * 2f / vpH);

            var a0 = OffsetClip(c0, offsetNdc, -1f);
            var b0 = OffsetClip(c0, offsetNdc, +1f);
            var a1 = OffsetClip(c1, offsetNdc, -1f);
            var b1 = OffsetClip(c1, offsetNdc, +1f);

            var edgeNeg = new Vector3(-ext, halfWidth, feather);
            var edgePos = new Vector3(+ext, halfWidth, feather);

            _vertices[v++] = new LineVertex(a0, color, edgeNeg);
            _vertices[v++] = new LineVertex(b0, color, edgePos);
            _vertices[v++] = new LineVertex(a1, color, edgeNeg);
            _vertices[v++] = new LineVertex(b0, color, edgePos);
            _vertices[v++] = new LineVertex(b1, color, edgePos);
            _vertices[v++] = new LineVertex(a1, color, edgeNeg);
        }

        private static Vector4 OffsetClip(Vector4 c, Vector2 offsetNdc, float side)
            => new Vector4(c.X + side * offsetNdc.X * c.W, c.Y + side * offsetNdc.Y * c.W, c.Z, c.W);
    }
}
