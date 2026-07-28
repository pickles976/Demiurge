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
using NoiseDotNet;
using Stride.Core.Storage;
using BulletSharp;
// NOT System.Numerics: it makes Vector3/Vector2/Quaternion/Matrix ambiguous against
// Stride.Core.Mathematics throughout this file. Stride defines implicit conversions both ways, so
// Common's values cross the boundary without it.

// Init riptide message logging
var Log = GlobalLogger.GetLogger("Program");
RiptideLogger.Initialize(
      msg => Log.Debug(msg),
      msg => Log.Info(msg),
      msg => Log.Warning(msg),
      msg => Log.Error(msg),
      false);

// Singleplayer runs a real server in this process — the same ServerHost the standalone exe runs — so
// testing a server-side change is one launch instead of two. Nothing about the netcode is bypassed:
// the client still connects over the socket to 127.0.0.1 and is still a non-authoritative peer.
// `dotnet run -- --singleplayer` (the bare `--` matters; dotnet swallows unknown flags first).
//
// It is stepped from Update(), NOT given its own thread: Riptide's Message and PendingMessage pools
// are unsynchronised statics, so a server and a client creating messages on two threads corrupt each
// other. See the note on ServerHost.
using var localServer = Environment.GetCommandLineArgs().Contains("--singleplayer")
    ? new Demiurge.GameServer.ServerHost()
    : null;

if (localServer is not null)
{
    Log.Info("Singleplayer: starting local server");

    try
    {
        localServer.Start();
    }
    catch (Exception ex)
    {
        // Do NOT fall back to connecting to whatever is on the port. The whole point of
        // --singleplayer is testing the server code in THIS build; silently attaching to an
        // already-running server means testing a stale one and not knowing.
        Console.Error.WriteLine($"\n--singleplayer could not host a server: {ex.Message}");
        Console.Error.WriteLine($"Port {NetworkConfig.Port} is probably held by a standalone server.");
        Console.Error.WriteLine("Stop it, or drop --singleplayer to connect to it deliberately.\n");
        return;
    }
}


Entity? sphere = null;

Entity? basil = null;

CameraComponent? camera = null;
BepuSimulation? simulation = null;

using var game = new Game();


// how does this work?
var network = new NetworkManager();

// Sim layer: the client's copy of the terrain, filled ONLY from the wire. The server owns terrain
// and streams it; there is deliberately no client-side generator to fall back on.
// Constructed before the registries because the local player predicts movement against it.
var terrainState = new TerrainState();

// Terrain arrives on its own TCP connection rather than through Riptide — see ChunkTransport for why.
// Receive only enqueues, so the reader thread raising this is safe; Drain applies it on the main thread.
var chunkStream = new ChunkTcpClient();
chunkStream.ChunkReceived += terrainState.Receive;

// Welcome carries the token that identifies us on that stream, so the connection can only be made once
// the Riptide handshake has completed.
network.Welcomed += welcome => chunkStream.Connect(NetworkConfig.ServerHost, welcome.ChunkToken);

// Grip/barrel points measured off the source .gltf at build time — a weapon model has
// no nodes left at runtime to read them from. See ModelLocators. WeaponMount turns them
// into the two answers that must agree: where the renderer seats a gun, and where the
// sim says its shots start. ItemCosmetics owns the ItemType -> model table, so the root
// hands it over rather than letting Core reach into View for it.
var modelLocators = ModelLocators.Load();
var weaponMount = new WeaponMount(modelLocators, ItemCosmetics.Model);

var registry = new PlayerRegistry(network, terrainState, weaponMount);
var objectRegistry = new ObjectRegistry(network);

// View over that state, built in Start() once the graphics device exists.
ClientTerrain? terrainView = null;

// Bridge the two registries: objects owned by our client id attach to the local
// player. Sim-to-sim glue lives here in the composition root. Our ACTIVE gun is
// exactly the Hand-slot weapon — a future rifle stowed on the Back slot is
// owned and Weapon-masked but must not drive the HUD or prediction.
void LinkOwned(LocalPlayer local, NetObject obj)
{
    if (!obj.Has.HasFlag(Demiurge.NetComponents.Owner) || obj.Owner.PlayerId != network.ClientId) return;
    if (obj.Has.HasFlag(Demiurge.NetComponents.Weapon)
        && obj.Has.HasFlag(Demiurge.NetComponents.Attachment)
        && obj.Attachment.Slot == Demiurge.EquipSlot.Hand) local.Equip(obj);
    if (obj.Type == Demiurge.ObjectType.PlayerStatus) local.Status = obj;
}

objectRegistry.ObjectSpawned += obj =>
{
    if (registry.LocalPlayer is { } local) LinkOwned(local, obj);
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
    if (registry.LocalPlayer is not { } local) return;
    // Unequip guards on reference equality, so a despawning weapon PICKUP
    // (also Weapon-masked, but never our equipped object) is a no-op.
    if (obj.Has.HasFlag(Demiurge.NetComponents.Weapon)) local.Unequip(obj);
    if (ReferenceEquals(local.Status, obj)) local.Status = null;
};

game.Services.AddService(network);
game.Services.AddService(registry);

game.Run(start: Start, update: Update);

chunkStream.Dispose();

// Stops the mesher threads. They are background threads so the process would exit regardless, but
// joining them here means a worker can't be mid-read of the chunk map while shutdown tears it down.
terrainView?.Dispose();

// NOTE: do NOT set GraphicsDeviceManager.IsFullScreen here (before Run). On the
// SDL/Linux backend that creates an exclusive-fullscreen swapchain whose pixel
// format resolves to None, causing a DivideByZero in InitDefaultRenderTarget.
// Fullscreen is enabled as a borderless window inside Start() instead.


// Builds the terrain view. Nothing renders until chunks arrive from the server, which is the point:
// there is no code path that produces terrain the server hasn't sent.
ClientTerrain createTerrainView(Scene rootScene)
    => new(rootScene, new ChunkMeshFactory(game, new TerrainMaterials(game)), terrainState);


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
    game.GraphicsDeviceManager.PreferredBackBufferWidth = 1280;
    game.GraphicsDeviceManager.PreferredBackBufferHeight = 720;
    game.GraphicsDeviceManager.ApplyChanges();
    // game.AddDirectionalLight();

    terrainView = createTerrainView(rootScene);


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

    sphere = game.Create3DPrimitive(PrimitiveModelType.Sphere, new() { IncludeCollider = false });
    sphere.Transform.Position = new Vector3(0, 0.5f, 0);

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
    var ObjectViewFactory = new ObjectViewFactory(game, rootScene, objectRegistry, weaponMount);

    var cameraEntity = game.Add3DCamera();
    LineRenderer.Camera = cameraEntity.Get<CameraComponent>();
    cameraEntity.Add(new LocalPlayerController { CameraEntity = cameraEntity, Registry = registry, Terrain = terrainState });
    // Over-the-shoulder action camera. ThirdPersonCameraScript — the high, cursor-aimed one — is kept
    // in PlayerCamera.cs as dead code; swap the two lines to go back to it.
    cameraEntity.Add(new ShoulderCameraScript { Registry = registry });
    // cameraEntity.Add(new ThirdPersonCameraScript { Registry = registry });
    // Tilde detaches the camera and freezes the player; see DebugFlyCameraScript.
    cameraEntity.Add(new DebugFlyCameraScript());
    // The cursor reticle belongs to the old camera: the shoulder camera locks the mouse, so there is no
    // cursor to draw one at.
    // Dead code, along with the cursor-aimed ThirdPersonCameraScript it belongs to — the shoulder
    // camera locks the mouse to the centre, so there is no cursor for it to follow.
    // cameraEntity.Add(new CursorReticleScript());
    cameraEntity.Add(new ReticleScript { Registry = registry });
    // Aim line off: it drew where the old cursor-aimed camera was pointing, which the shoulder camera
    // makes redundant — you are already looking down the shot. Kept as dead code like the camera itself.
    // cameraEntity.Add(new AimLineScript { Registry = registry, Mount = weaponMount });
    cameraEntity.Add(new ShotEffectsScript { Registry = registry, Objects = objectRegistry, Network = network, Terrain = terrainState });

    network.Connect();


}

void Update(Scene scene, GameTime time)
{
    // Before the client pumps: same thread, so the two peers never touch Riptide's static pools
    // concurrently.
    localServer?.Step();

    network.Update();

    // Chunks stream in over several ticks, so both of these run every frame rather than once at
    // startup. Drain applies what the network thread queued; RebuildDirty hands whatever that dirtied
    // to the mesher threads and uploads a bounded slice of what they finished. Both are no-ops when
    // nothing arrived.
    //
    // The ORDER matters. Drain is the only writer of chunk voxels, and RebuildDirty only dispatches
    // sections whose chunks Drain has already finished, so a worker never reads an array Drain is
    // writing. Interleaving them differently would break that.
    terrainState.Drain();
    // The PLAYER's position drives level of detail, never the camera's. Freecam must not be able to
    // change what the terrain looks like — see TerrainLod. Before we spawn there is no player, so the
    // world origin stands in; nothing is loaded yet anyway.
    var lodOrigin = registry.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
    terrainView?.RebuildDirty(lodOrigin);

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
    entity.Transform.Rotation = Quaternion.RotationX(Stride.Core.Mathematics.MathUtil.DegreesToRadians(-30.0f)) * Quaternion.RotationY(Stride.Core.Mathematics.MathUtil.DegreesToRadians(-180.0f));

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