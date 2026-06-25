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
using MyGame;


float movementSpeed = 5f;
Entity? sphere = null;

using var game = new Game();

game.Run(start: Start, update: Update);

void Start(Scene rootScene)
{
    game.AddGraphicsCompositor();
    game.Add3DCamera().Add3DCameraController();
    // game.AddDirectionalLight();
    game.Add3DCameraController();
    game.Add3DGround();

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

    var entity = game.Create3DPrimitive(PrimitiveModelType.Capsule, new() {});

    entity.Transform.Position = new Vector3(0, 8, 0);

    // Create a 3D sphere programmatically
    sphere = game.Create3DPrimitive(PrimitiveModelType.Sphere, new() { IncludeCollider = false });
    sphere.Transform.Position = new Vector3(0, 0.5f, 0);

    var cube = game.Create3DPrimitive(PrimitiveModelType.Cube, new Primitive3DEntityOptions {
        Material = material,
    });

    var ak = new Entity("AK47") { new ModelComponent(LoadModel("assets/models/ak47.gltf")) };
    ak.Transform.Position = new Vector3(0, 1.0f, 0);
    ak.Scene = rootScene;

    var basil = new Entity("BASIL") { new ModelComponent(LoadModel("assets/models/basil.gltf")) };
    basil.Transform.Position = new Vector3(1.0f, 0.5f, 0);

    // Animations are compiled as standalone AnimationClips (see GltfAssetGenerator).
    // Register the "walk" clip on an AnimationComponent and play it.
    var basilAnimations = new AnimationComponent();
    basil.Add(basilAnimations);
    basilAnimations.Animations.Add("walk", game.Content.Load<AnimationClip>("models/basil_anim_walk"));
    basilAnimations.Play("walk");

    // Attach the SyncScript defined in Test.cs
    // basil.Add(new SampleSyncScript());

    // Move BASIL with WASD
    basil.Add(new WasdMovementScript { Speed = movementSpeed });

    basil.Scene = rootScene;

    // // Rigged Sketchfab model with four skeletal clips; play the walk cycle.
    // var girl = new Entity("GIRL") { new ModelComponent(LoadModel("assets/models/girl_mechanic/scene.gltf")) };
    // girl.Transform.Position = new Vector3(-1.0f, 0.5f, 0);

    // var girlAnimations = new AnimationComponent();
    // girl.Add(girlAnimations);
    // girlAnimations.Animations.Add("walk", game.Content.Load<AnimationClip>("models/girl_mechanic/scene_anim_root_Girl_walk"));
    // girlAnimations.Play("walk");

    // girl.Scene = rootScene;

    // sphere.Scene = rootScene;
    // cube.Scene = rootScene;
    // entity.Scene = rootScene;
}

void Update(Scene scene, GameTime time)
{
    if (sphere == null) return;
    var deltaTime = (float)time.Elapsed.TotalSeconds;

    // Move the sphere using keyboard input
    if (game.Input.IsKeyDown(Keys.Left)) sphere.Transform.Position.X -= movementSpeed * deltaTime;
    if (game.Input.IsKeyDown(Keys.Right)) sphere.Transform.Position.X += movementSpeed * deltaTime;
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