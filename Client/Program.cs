using StbImageSharp;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Engine;
using Stride.CommunityToolkit.Games;
using Stride.CommunityToolkit.Rendering.ProceduralModels;
using Stride.Core.Mathematics;
using Stride.Animations;
using Stride.Engine;
using Stride.Input;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;

using Stride.BepuPhysics;
using Stride.Core.Diagnostics;

using Stride.UI; // This was added
using Stride.UI.Controls; // This was added
using Stride.UI.Panels;
using Stride.CommunityToolkit.Rendering.Compositing;
using Stride.CommunityToolkit.Helpers; // This was added

using Demiurge;
using Stride.BepuPhysics.Definitions.Colliders;
using Riptide.Utils;
using Demiurge.GameClient;
using Silk.NET.OpenXR;
using Microsoft.Win32;

// Init riptide message logging
var Log = GlobalLogger.GetLogger("Program");
RiptideLogger.Initialize(
      msg => Log.Debug(msg),
      msg => Log.Info(msg),
      msg => Log.Warning(msg),
      msg => Log.Error(msg),
      false);


Entity? sphere = null;

Entity? basil = null;

CameraComponent? camera = null;
BepuSimulation? simulation = null;

using var game = new Game();


// how does this work?
var network = new NetworkManager();
var registry = new PlayerRegistry(network);
var objectRegistry = new ObjectRegistry(network);

// Bridge the two registries: objects owned by our client id attach to the local
// player. Sim-to-sim glue lives here in the composition root.
void LinkOwned(LocalPlayer local, NetObject obj)
{
    if (obj.Owner.PlayerId != network.ClientId) return;
    if (obj.Type == Demiurge.ObjectType.EquippedWeapon) local.Equip(obj);
    if (obj.Type == Demiurge.ObjectType.PlayerStatus) local.Status = obj;
}

objectRegistry.ObjectSpawned += obj =>
{
    if (registry.LocalPlayer is {} local) LinkOwned(local, obj);
};
// The server spawns our PlayerStatus object BEFORE announcing our player, so its
// ObjectSpawned fires while LocalPlayer is still null. Backfill on spawn: link any
// owned objects that arrived first.
registry.PlayerJoined += player =>
{
    if (player is not LocalPlayer local) return;
    foreach (var obj in objectRegistry.Objects) LinkOwned(local, obj);
};
objectRegistry.ObjectDespawned += obj =>
{
    if (registry.LocalPlayer is not {} local) return;
    if (obj.Type == Demiurge.ObjectType.EquippedWeapon) local.Unequip(obj);
    if (ReferenceEquals(local.Status, obj)) local.Status = null;
};

game.Services.AddService(network);
game.Services.AddService(registry);

game.Run(start: Start, update: Update);

// NOTE: do NOT set GraphicsDeviceManager.IsFullScreen here (before Run). On the
// SDL/Linux backend that creates an exclusive-fullscreen swapchain whose pixel
// format resolves to None, causing a DivideByZero in InitDefaultRenderTarget.
// Fullscreen is enabled as a borderless window inside Start() instead.


void Start(Scene rootScene)
{
    var compositor = game.AddGraphicsCompositor();
    compositor.AddCleanUIStage();
    compositor.AddSceneRenderer(new LineSceneRenderer());
    // SSR (LocalReflections) is enabled by default and allocates R11G11B10_Float
    // buffers, a format missing from Stride's Vulkan backend (throws on the first
    // frame). We don't use screen-space reflections; turn them off.
    if (((ForwardRenderer)compositor.SingleView).PostEffects is PostProcessingEffects postFx)
        postFx.LocalReflections.Enabled = false;
    // AddGraphicsCompositor() does NOT include particle rendering; add it explicitly
    // or ParticleSystemComponents simulate but never draw.
    // DISABLED: ParticleEmitterRenderFeature crashes with an AccessViolationException
    // on the Vulkan backend (known engine bug, see stride3d/stride#2496 — the official
    // particle samples crash the same way on Linux). Re-enable once Stride fixes it
    // or if we return to OpenGL.
    // game.AddParticleRenderer();

    // Shared player state read by the HUD and written by the gun (resolved via Services).
    game.Services.AddService<IPlayerStatus>(new PlayerStatus());

    // Borderless fullscreen (safe on SDL/Linux; keeps the windowed backbuffer format).
    // game.Window.FullscreenIsBorderlessWindow = true;
    // game.GraphicsDeviceManager.IsFullScreen = true;
    game.GraphicsDeviceManager.PreferredBackBufferWidth = 800;
    game.GraphicsDeviceManager.PreferredBackBufferHeight = 600;
    game.GraphicsDeviceManager.ApplyChanges();
    // game.AddDirectionalLight();

    // Apply custom shader
    var ground = game.Add3DGround();
    var groundMaterial = Material.New(game.GraphicsDevice, new MaterialDescriptor
    {
        Attributes =
        {
            Diffuse = new MaterialDiffuseMapFeature(new ComputeShaderClassColor
            {
                MixinReference = "TestShader"
            }),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
        }
    });

    ground.Get<ModelComponent>().Materials[0] = groundMaterial;

    // DISABLED: the profiler overlay draws through FastTextRenderer, which crashes on
    // Vulkan (use-after-unmap bug: FastTextRenderer.Initialize reads back a mapped
    // pointer after UnmapSubresource; still broken in Stride master). DebugTextSystem
    // uses the same renderer — see the disabled Print call in Update(). Use the UI/HUD
    // (TextBlock) for on-screen text instead; it renders through SpriteBatch, which is fine.
    // game.AddProfiler();
    game.AddGroundGizmo(position: new Vector3(-5, 0.1f, -5), showAxisName: true);

    // Disabled along with AddParticleRenderer above (Vulkan particle crash); without
    // the render feature this would simulate invisibly and waste CPU.
    // ParticleExample.CreateAtOrigin().Scene = rootScene;

    var directionalLight = CreateDirectionalLight("DirectionalLight");
    directionalLight.Scene = rootScene;

    var ambientLight = CreateAmbientLight();
    ambientLight.Scene = rootScene;


    // Texture.Load uses System.Drawing.Common which is Windows-only; decode via StbImageSharp instead
    ImageResult img;
    using (var stream = File.OpenRead("assets/prototype/textures/Green/texture_01.png"))
        img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    var texture = Texture.New2D(game.GraphicsDevice, img.Width, img.Height,
        PixelFormat.R8G8B8A8_UNorm_SRgb, img.Data);

    // Build a lit material with the texture as its diffuse map
    var material = Material.New(game.GraphicsDevice, new MaterialDescriptor
    {
        Attributes = new MaterialAttributes
        {
            Diffuse = new MaterialDiffuseMapFeature(new ComputeTextureColor(texture)),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
        }
    });

    sphere = game.Create3DPrimitive(PrimitiveModelType.Sphere, new() { IncludeCollider = false });
    sphere.Transform.Position = new Vector3(0, 0.5f, 0);

    var cube = game.Create3DPrimitive(PrimitiveModelType.Cube, new Primitive3DEntityOptions {
        Material = material,
    });

    // var player = CreatePlayer();
    // player.Scene = rootScene;

    var dummy = CreateDummy();
    dummy.Scene = rootScene;

    // TODO: update
    var uiEntity = HUD.CreateUI(game);
    uiEntity.Scene = rootScene;

    // Entity/FPS counters, top-left (UI-based replacement for the Vulkan-broken
    // profiler overlay and DebugTextSystem — see the disabled calls above/below).
    HUD.CreateDebugStats(game).Scene = rootScene;


    // Drives bullet-tracer fade/expiry once per frame (see TracerManager).
    var tracerSystem = new Entity("TracerSystem") { new TracerSystem() };
    tracerSystem.Scene = rootScene;

    // var grassField = GrassField.Create(game, center: Vector3.Zero, sizeX: 50f, sizeZ: 50f, cellSize: 0.5f);
    // grassField.Scene = rootScene;

    camera = rootScene.GetCamera();
    simulation = camera?.Entity.GetSimulation();

    // OpenAL audio (see SoundManager); the camera is the 3D listener.
    game.Services.AddService(new SoundManager(camera?.Entity));

    if (simulation != null)
    {
        Console.WriteLine("Simulation Started");
    }

    var viewFactory = new PlayerViewFactory(game, rootScene, registry);
    var ObjectViewFactory = new ObjectViewFactory(game, rootScene, objectRegistry);

    var cameraEntity = game.Add3DCamera();
    LineRenderer.Camera = cameraEntity.Get<CameraComponent>();
    cameraEntity.Add(new LocalPlayerController {CameraEntity = cameraEntity, Registry = registry});
    cameraEntity.Add(new ThirdPersonCameraScript{Registry = registry});
    cameraEntity.Add(new CursorReticleScript());
    cameraEntity.Add(new AimLineScript { Registry = registry });
    cameraEntity.Add(new ShotEffectsScript { Registry = registry, Objects = objectRegistry, Network = network });

    network.Connect();


}

void Update(Scene scene, GameTime time)
{
    network.Update();

    // DISABLED: DebugTextSystem draws through FastTextRenderer, which crashes on Vulkan
    // (see the AddProfiler comment in Start()). Replaced by HUD.CreateDebugStats.
    // game.DebugTextSystem.Print($"Entities: {scene.Entities.Count}", new Int2(50, 50));

    if (camera == null || simulation == null || !game.Input.HasMouse) return;

    if (game.Input.IsMouseButtonPressed(MouseButton.Left))
    {
        // Physics
        // Check for collisions with physics-based entities using raycasting
        var hitResult = camera.Raycast(game.Input.MousePosition, 100f, out HitInfo hitInfo);

        if (hitResult)
        {
            var message = $"Hit: {hitInfo.Collidable.Entity.Name}";
            Log.Debug(message);

            var rigidBody = hitInfo.Collidable.Entity.Get<BodyComponent>();

            if (rigidBody != null)
            {
                var direction = new Vector3(0, 3, 0); // Apply impulse upward
                rigidBody.Awake = true;
                rigidBody.ApplyImpulse(direction, Vector3.Zero);
            }
        }
        else
        {
            Console.WriteLine("No hit detected.");
        }

        // Check for intersections with non-physical entities using ray picking
        var ray = camera.GetPickRay(game.Input.MousePosition);

        if (basil?.Get<ModelComponent>().BoundingBox.Intersects(ref ray) ?? false)
        {
            Console.WriteLine("Basil hit!");
        }
    }

}

Entity CreateAmbientLight()
{
    return new Entity("Ambient Light") { new LightComponent { Intensity = 1.0f, Type = new LightAmbient() } };
}

Entity CreateDirectionalLight(string? entityName = "Directional Light")
{
    var entity = new Entity(entityName)
    {
        new LightComponent
        {
            Intensity =  20.0f,
            Type = new LightDirectional
            {
                Color = new ColorRgbProvider(Color.White),
                Shadow =
                {
                    Enabled = true,
                    Size = LightShadowMapSize.Large,
                    Filter = new LightShadowMapFilterTypePcf { FilterSize = LightShadowMapFilterTypePcfSize.Filter5x5 },
                    PartitionMode = new LightDirectionalShadowMap.PartitionLogarithmic(),
                    ComputeTransmittance = false
                }
            }
        }
    };

    entity.Transform.Position = new Vector3(0, 2.0f, 0);
    entity.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-30.0f)) * Quaternion.RotationY(MathUtil.DegreesToRadians(-180.0f));

    return entity;
}

Entity CreateDummy()
{

    var dummy = new Entity("DUMMY") { 
        new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")),
        new BodyComponent
        {
            Collider = new CompoundCollider
            {
                Colliders =
                {
                    // Roughly humanoid; entity origin is at the feet, so lift the
                    // box's center to half its height.
                    new BoxCollider { Size = new Vector3(0.6f, 1.8f, 0.6f),
                                    PositionLocal = new Vector3(0, 0.9f, 0) }
                }
            }
        }
    };
    dummy.Transform.Position = new Vector3(1.0f, 0.0f, 0);

    return dummy;
}