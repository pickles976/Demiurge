using Stride.Core.Mathematics;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Shaders;

namespace Demiurge;

/// <summary>
/// A second, orthographic camera which renders only terrain into the minimap texture.
///
/// The camera is heading-up: its screen-up vector follows the local player's facing. The view's
/// screen-right vector is therefore the same one used by <see cref="MinimapScript"/> to place
/// markers, so terrain and symbols cannot disagree about handedness.
/// </summary>
public sealed class MinimapTerrain : IDisposable
{
    public const RenderGroup TerrainGroup = RenderGroup.Group30;

    private const int TextureSize = 256;
    private const float CameraPadding = 16f;

    private readonly GraphicsCompositor compositor;
    private readonly SceneRendererCollection rendererRoot;
    private readonly SceneCameraSlot cameraSlot;
    private readonly Entity cameraEntity;
    private readonly CameraComponent camera;
    private readonly RenderTextureSceneRenderer textureRenderer;
    private readonly SceneCameraRenderer cameraRenderer;
    private readonly ForwardRenderer forwardRenderer;
    private readonly MinimapCircleMaskRenderer circleMaskRenderer;
    private readonly SceneRendererCollection textureChildren;
    private bool disposed;

    public MinimapTerrain(IGame game, Scene scene, float worldRadius)
    {
        compositor = game.Services.GetSafeServiceAs<SceneSystem>().GraphicsCompositor
            ?? throw new InvalidOperationException("The minimap requires a graphics compositor.");
        rendererRoot = compositor.Game as SceneRendererCollection
            ?? throw new InvalidOperationException(
                "The minimap requires the game's scene renderers to be a collection.");

        Texture = Texture.New2D(
            game.GraphicsDevice,
            TextureSize,
            TextureSize,
            PixelFormat.R8G8B8A8_UNorm,
            TextureFlags.ShaderResource | TextureFlags.RenderTarget);

        cameraSlot = new SceneCameraSlot { Name = "Minimap" };
        compositor.Cameras.Add(cameraSlot);

        camera = new CameraComponent
        {
            Projection = CameraProjectionMode.Orthographic,
            OrthographicSize = worldRadius * 2f,
            NearClipPlane = 0.1f,
            FarClipPlane = ChunkConstants.WorldMaxY - ChunkConstants.WorldMinY
                + CameraPadding * 2f,
            UseCustomAspectRatio = true,
            AspectRatio = 1f,
            UseCustomViewMatrix = true,
            Slot = cameraSlot.ToSlotId(),
        };
        cameraEntity = new Entity("MinimapCamera") { camera };
        cameraEntity.Scene = scene;

        // Reuse the compositor's stages and render features, but not its renderer instance. This
        // gives the map its own transparent clear and avoids running the main camera's post effects.
        var mainForward = (ForwardRenderer)compositor.SingleView;
        forwardRenderer = new ForwardRenderer
        {
            Clear = new ClearRenderer { Color = new Color4(0f, 0f, 0f, 0f) },
            OpaqueRenderStage = mainForward.OpaqueRenderStage,
            TransparentRenderStage = mainForward.TransparentRenderStage,
            PostEffects = null,
            LightProbes = false,
            BindDepthAsResourceDuringTransparentRendering = false,
        };
        cameraRenderer = new SceneCameraRenderer
        {
            Camera = cameraSlot,
            RenderMask = RenderGroupMask.Group30,
            Child = forwardRenderer,
        };
        circleMaskRenderer = new MinimapCircleMaskRenderer();
        textureChildren = new SceneRendererCollection();
        textureChildren.Add(cameraRenderer);
        textureChildren.Add(circleMaskRenderer);
        textureRenderer = new RenderTextureSceneRenderer
        {
            RenderTexture = Texture,
            Child = textureChildren,
        };

        // The UI samples this texture later in the same compositor pass, so render it first rather
        // than displaying the previous frame (or uninitialised memory on the first frame).
        rendererRoot.Children.Insert(0, textureRenderer);
    }

    public Texture Texture { get; }

    /// <summary>Moves and rotates the top-down view with the player.</summary>
    public void Update(System.Numerics.Vector3 centre, float yaw, bool visible)
    {
        textureRenderer.Enabled = visible;
        if (!visible) return;

        float sin = MathF.Sin(yaw);
        float cos = MathF.Cos(yaw);
        var eye = new Vector3(centre.X, ChunkConstants.WorldMaxY + CameraPadding, centre.Z);
        var down = eye - Vector3.UnitY;
        var heading = new Vector3(sin, 0f, cos);
        camera.ViewMatrix = Matrix.LookAtRH(eye, down, heading);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        rendererRoot.Children.Remove(textureRenderer);
        camera.Enabled = false;
        cameraEntity.Scene = null;
        compositor.Cameras.Remove(cameraSlot);

        textureRenderer.Dispose();
        textureChildren.Dispose();
        cameraRenderer.Dispose();
        circleMaskRenderer.Dispose();
        forwardRenderer.Dispose();
        Texture.Dispose();
    }
}

/// <summary>Clears render-texture pixels outside the minimap disc after terrain is drawn.</summary>
internal sealed class MinimapCircleMaskRenderer : SceneRendererBase
{
    // Also gives Vulkan a resource binding; a zero-binding shader cannot create a pipeline layout.
    private static readonly ValueParameterKey<float> RadiusKey =
        ParameterKeys.NewValue(1f, "MinimapCircleMaskShader.Radius");

    private EffectInstance effect = null!;
    private MutablePipelineState pipeline = null!;
    private Stride.Graphics.Buffer vertices = null!;

    protected override void InitializeCore()
    {
        base.InitializeCore();

        var effectSystem = Services.GetSafeServiceAs<EffectSystem>();
        effect = new EffectInstance(
            effectSystem.LoadEffect("MinimapCircleMaskShader").WaitForResult());
        effect.Parameters.Set(RadiusKey, 1f);
        pipeline = new MutablePipelineState(GraphicsDevice);
        vertices = Stride.Graphics.Buffer.Vertex.New(
            GraphicsDevice,
            new VertexPositionTexture[]
            {
                new(new Vector3(-1f, -1f, 0f), new Vector2(0f, 1f)),
                new(new Vector3(-1f,  1f, 0f), new Vector2(0f, 0f)),
                new(new Vector3( 1f,  1f, 0f), new Vector2(1f, 0f)),
                new(new Vector3(-1f, -1f, 0f), new Vector2(0f, 1f)),
                new(new Vector3( 1f,  1f, 0f), new Vector2(1f, 0f)),
                new(new Vector3( 1f, -1f, 0f), new Vector2(1f, 1f)),
            });
    }

    protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
    {
        var commandList = drawContext.CommandList;
        effect.UpdateEffect(GraphicsDevice);

        pipeline.State.SetDefaults();
        pipeline.State.RootSignature = effect.RootSignature;
        pipeline.State.EffectBytecode = effect.Effect.Bytecode;
        pipeline.State.PrimitiveType = PrimitiveType.TriangleList;
        pipeline.State.InputElements = VertexPositionTexture.Layout.CreateInputElements();
        pipeline.State.RasterizerState = RasterizerStates.CullNone;
        pipeline.State.BlendState = BlendStates.Opaque;
        pipeline.State.DepthStencilState = DepthStencilStates.None;
        pipeline.State.Output.CaptureState(commandList);
        pipeline.Update();

        commandList.SetVertexBuffer(0, vertices, 0, VertexPositionTexture.Layout.VertexStride);
        commandList.SetPipelineState(pipeline.CurrentState);
        effect.Apply(drawContext.GraphicsContext);
        commandList.Draw(6);
    }

    protected override void Unload()
    {
        vertices?.Dispose();
        base.Unload();
    }
}
