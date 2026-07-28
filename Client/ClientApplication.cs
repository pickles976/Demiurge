using Stride.CommunityToolkit.Engine;
using Stride.CommunityToolkit.Games;
using Stride.CommunityToolkit.Rendering.Compositing;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Rendering.Colors;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;

namespace Demiurge;

public sealed class ClientApplication : IDisposable
{
    private readonly Game game = new();
    private readonly ClientInputState inputState = new();
    private ClientSessionCoordinator coordinator = null!;
    private Entity? terminal;

    public void Run(string[] args)
    {
        var initial = ParseInitialSession(args);
        game.Run(
            start: scene => Start(scene, initial),
            update: (_, time) => coordinator.Update(time));
    }

    public void Dispose()
    {
        coordinator?.Dispose();
        terminal?.Scene = null;
        game.Dispose();
    }

    private void Start(Scene scene, SessionRequest initial)
    {
        ConfigureRendering();
        ConfigureWindow();
        AddLighting(scene);

        coordinator = new ClientSessionCoordinator(game, inputState);
        terminal = HUD.CreateTerminal(game, inputState, coordinator);
        terminal.Scene = scene;
        coordinator.Start(scene, initial);
    }

    private static SessionRequest ParseInitialSession(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i].Equals("--editor", StringComparison.OrdinalIgnoreCase))
            {
                string name = i + 1 < args.Length && !args[i + 1].StartsWith("--")
                    ? args[i + 1]
                    : "untitled";
                return SessionRequest.EditorMap(name);
            }

        if (args.Contains("--singleplayer", StringComparer.OrdinalIgnoreCase))
            return SessionRequest.GeneratedHost();
        return SessionRequest.Join(NetworkConfig.ServerHost);
    }

    private void ConfigureRendering()
    {
        var compositor = game.AddGraphicsCompositor();
        compositor.AddCleanUIStage();
        compositor.AddSceneRenderer(new LineSceneRenderer());

        if (((ForwardRenderer)compositor.SingleView).PostEffects is PostProcessingEffects postFx)
        {
            postFx.AmbientOcclusion.Enabled = false;
            postFx.LocalReflections.Enabled = false;
            postFx.Antialiasing.Enabled = false;
        }
    }

    private void ConfigureWindow()
    {
        game.GraphicsDeviceManager.PreferredBackBufferWidth = 1280;
        game.GraphicsDeviceManager.PreferredBackBufferHeight = 720;
        game.GraphicsDeviceManager.ApplyChanges();
    }

    private static void AddLighting(Scene scene)
    {
        new Entity("Sky Fill")
        {
            new LightComponent
            {
                Intensity = 2.25f,
                Type = new LightAmbient
                {
                    Color = new ColorRgbProvider(new Color(170, 195, 230)),
                },
            },
        }.Scene = scene;

        var directional = new Entity("Directional Light")
        {
            new LightComponent
            {
                Intensity = 11f,
                Type = new LightDirectional
                {
                    Color = new ColorRgbProvider(new Color(255, 235, 205)),
                    Shadow = { Enabled = false },
                },
            },
        };
        directional.Transform.Position = new Vector3(0, 2, 0);
        directional.Transform.Rotation =
            Quaternion.RotationX(MathUtil.DegreesToRadians(-30f))
            * Quaternion.RotationY(MathUtil.DegreesToRadians(-180f));
        directional.Scene = scene;
    }
}
