using StbImageSharp;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Engine;
using Stride.CommunityToolkit.Games;
using Stride.CommunityToolkit.Rendering.ProceduralModels;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;


float movementSpeed = 5f;
Entity? sphere = null;

using var game = new Game();

game.Run(start: Start, update: Update);

void Start(Scene rootScene)
{
    game.AddGraphicsCompositor();
    game.Add3DCamera().Add3DCameraController();
    game.AddDirectionalLight();
    game.Add3DCameraController();
    game.Add3DGround();

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
    sphere.Scene = rootScene;

    var cube = game.Create3DPrimitive(PrimitiveModelType.Cube, new Primitive3DEntityOptions {
        Material = material,
    });

    cube.Scene = rootScene;
    entity.Scene = rootScene;
}

void Update(Scene scene, GameTime time)
{
    if (sphere == null) return;
    var deltaTime = (float)time.Elapsed.TotalSeconds;

    // Move the sphere using keyboard input
    if (game.Input.IsKeyDown(Keys.Left)) sphere.Transform.Position.X -= movementSpeed * deltaTime;
    if (game.Input.IsKeyDown(Keys.Right)) sphere.Transform.Position.X += movementSpeed * deltaTime;
}