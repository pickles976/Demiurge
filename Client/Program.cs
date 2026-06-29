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

SpriteFont? font = null; // This was added


Entity? sphere = null;

Entity? basil = null;

CameraComponent? camera = null;
BepuSimulation? simulation = null;

using var game = new Game();

// NOTE: do NOT set GraphicsDeviceManager.IsFullScreen here (before Run). On the
// SDL/Linux backend that creates an exclusive-fullscreen swapchain whose pixel
// format resolves to None, causing a DivideByZero in InitDefaultRenderTarget.
// Fullscreen is enabled as a borderless window inside Start() instead.

game.Run(start: Start, update: Update);

void Start(Scene rootScene)
{
    var compositor = game.AddGraphicsCompositor();
    compositor.AddCleanUIStage();
    compositor.AddSceneRenderer(new LineSceneRenderer());
    // AddGraphicsCompositor() does NOT include particle rendering; add it explicitly
    // or ParticleSystemComponents simulate but never draw.
    game.AddParticleRenderer();

    // Borderless fullscreen (safe on SDL/Linux; keeps the windowed backbuffer format).
    game.Window.FullscreenIsBorderlessWindow = true;
    game.GraphicsDeviceManager.IsFullScreen = true;
    game.GraphicsDeviceManager.PreferredBackBufferWidth = 1920;
    game.GraphicsDeviceManager.PreferredBackBufferHeight = 1080;
    game.GraphicsDeviceManager.ApplyChanges();
    // game.Add3DCamera().Add3DCameraController();
    // game.AddDirectionalLight();
    game.Add3DGround();
    game.AddProfiler();
    game.AddGroundGizmo(position: new Vector3(-5, 0.1f, -5), showAxisName: true);

    ParticleExample.CreateAtOrigin().Scene = rootScene;

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

    var player = CreatePlayer();

    var uiEntity = CreateUI();
    uiEntity.Scene = rootScene;

    player.Scene = rootScene;

    // Drives bullet-tracer fade/expiry once per frame (see TracerManager).
    var tracerSystem = new Entity("TracerSystem") { new TracerSystem() };
    tracerSystem.Scene = rootScene;


    camera = rootScene.GetCamera();
    simulation = camera?.Entity.GetSimulation();

    if (simulation != null)
    {
        Console.WriteLine("Simulation Started");
    }
    


}

void Update(Scene scene, GameTime time)
{

    game.DebugTextSystem.Print($"Entities: {scene.Entities.Count}", new Int2(50, 50));

    if (camera == null || simulation == null || !game.Input.HasMouse) return;

    if (game.Input.IsMouseButtonPressed(MouseButton.Left))
    {
        // Physics
        // Check for collisions with physics-based entities using raycasting
        var hitResult = camera.Raycast(game.Input.MousePosition, 100f, out HitInfo hitInfo);

        if (hitResult)
        {
            var message = $"Hit: {hitInfo.Collidable.Entity.Name}";
            Console.WriteLine(message);

            GlobalLogger.GetLogger("Program.cs").Info(message); // This was added

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

Model LoadModel(string gltfPath)
{
    var contentPath = Path.ChangeExtension(Path.GetRelativePath("assets", gltfPath), null);
    return game.Content.Load<Model>(contentPath);
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

Entity CreateUI()
{
    // This below was added: Create and display a UI text block
    font = game.Content.Load<SpriteFont>("StrideDefaultFont");
    var canvas = new Canvas
    {
        Width = 300,
        Height = 100,
        BackgroundColor = new Color(248, 177, 149, 100),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    canvas.Children.Add(new TextBlock
    {
        Text = "Hello, Stride!",
        TextColor = Color.White,
        Font = font,
        TextSize = 24,
        Margin = new Thickness(3, 3, 3, 0),
    });

    var uiEntity = new Entity
    {
        new UIComponent
        {
            Page = new UIPage { RootElement = canvas },
            RenderGroup = RenderGroup.Group31 // Used to render AddCleanUIStage()
        }
    };

    return uiEntity;

}

Entity CreatePlayer()
{

    // Add Animations
    var playerAnimations = new AnimationComponent();
    playerAnimations.Animations.Add("Walk", game.Content.Load<AnimationClip>("models/cat_orange_anim_Walk"));
    playerAnimations.Animations.Add("Idle", game.Content.Load<AnimationClip>("models/cat_orange_anim_Idle"));
    playerAnimations.Animations.Add("Aiming", game.Content.Load<AnimationClip>("models/cat_orange_anim_Aiming"));

    // Add Camera
    var cameraEntity = game.Add3DCamera();
    LineRenderer.Camera = cameraEntity.Get<CameraComponent>();

    var player = new Entity("CAT") { 
        new ModelComponent(LoadModel("assets/models/cat_orange.gltf")),
        new PlayerScript {CameraEntity = cameraEntity},
        playerAnimations
    };
    player.Transform.Position = new Vector3(1.0f, 0.0f, 0);

    cameraEntity.Add(new ThirdPersonCameraScript { PlayerEntity = player });
    cameraEntity.Add(new CursorReticleScript());
    // cameraEntity.Add(new LookaheadDebugScript());


    return player;
}