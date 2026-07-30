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
    private const string DefaultSingleplayerMap = "conquest";
    private const int WindowWidth = 1920;
    private const int WindowHeight = 1080;

    private readonly Game game = new();
    private readonly ClientInputState inputState = new();
    private ClientSessionCoordinator coordinator = null!;
    private Entity? terminal;

    public void Run(string[] args)
    {
        var initial = ParseInitialSession(args);
        ConfigureWindow();
        game.WindowCreated += UseBorderlessFullscreen;
        game.Run(
            context: GameContextFactory.NewGameContext(
                AppContextType.DesktopSDL,
                WindowWidth,
                WindowHeight),
            start: scene => Start(scene, initial),
            update: (_, time) => coordinator.Update(time));
    }

    private void UseBorderlessFullscreen(object? sender, EventArgs args)
    {
        // Exclusive fullscreen on Stride's SDL backend repeatedly reports a size change on this
        // Linux setup. The graphics manager responds by resizing the device, which reports another
        // size change. Desktop fullscreen has the same presentation size without that mode switch.
        game.Window.FullscreenIsBorderlessWindow = true;
        game.WindowCreated -= UseBorderlessFullscreen;
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
        LogDisplayGeometry();
        AddLighting(scene);

        coordinator = new ClientSessionCoordinator(game, inputState);
        terminal = HUD.CreateTerminal(game, inputState, coordinator);
        terminal.Scene = scene;
        coordinator.Start(scene, initial);
    }

    private void LogDisplayGeometry()
    {
        var window = game.Window.ClientBounds;
        var backBuffer = game.GraphicsDevice.Presenter.BackBuffer;
        Console.WriteLine(
            $"[Display]: window {window.Width}x{window.Height}, "
            + $"back buffer {backBuffer.Width}x{backBuffer.Height}");
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
            return SessionRequest.SourceHost(
                DefaultSingleplayerMap,
                initialPlayerTeam: 1,
                initialNpcsPerTeam: 16);
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
        // The toolkit's start callback runs after Stride creates the window and graphics device, so
        // all initial presentation preferences must be set before Run. This client configures its
        // compositor and window in code; allowing the generated GameSettings asset to auto-load
        // would overwrite these values with Stride's 1280x720 defaults during PrepareContext.
        game.AutoLoadDefaultSettings = false;
        game.GraphicsDeviceManager.PreferredBackBufferWidth = WindowWidth;
        game.GraphicsDeviceManager.PreferredBackBufferHeight = WindowHeight;
        game.GraphicsDeviceManager.IsFullScreen = true;
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
