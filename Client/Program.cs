using Stride.CommunityToolkit.Engine;
using Stride.CommunityToolkit.Games;
using Stride.CommunityToolkit.Rendering.Compositing;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Rendering.Compositing;
using Stride.Engine;
using Stride.Games;
using Stride.Rendering.Images;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;

using Demiurge;
using Demiurge.GameClient;
using Riptide.Utils;
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
    ? new Demiurge.GameServer.ServerHost(allowCheats: true)
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

using var game = new Game();

var network = new NetworkManager();

// Sim layer: the client's copy of the terrain, filled ONLY from the wire. The server owns terrain
// and streams it; there is deliberately no client-side generator to fall back on.
// Constructed before the registries because the local player predicts movement against it.
var terrainState = new TerrainState();

// Terrain arrives on its own TCP connection rather than through Riptide — see ChunkTransport for why.
// Receive only enqueues, so the reader thread raising this is safe; Drain applies it on the main thread.
var chunkStream = new ChunkTcpClient();
chunkStream.ChunkReceived += terrainState.Receive;

// Terrain edits come over Riptide rather than the chunk stream: they are 26-byte commands, not bulk
// data, and they have to interleave with gameplay rather than queue behind a megabyte of terrain.
// Both handlers only ENQUEUE — Drain applies them on the main thread.
network.TerrainEdited += terrainState.ReceiveEdit;

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
var localWeaponView = new LocalWeaponView();
var inputState = new ClientInputState();

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
ClientTerrain CreateTerrainView(Scene rootScene)
    => new(rootScene, new ChunkMeshFactory(game, new TerrainMaterials(game)), terrainState);


void Start(Scene rootScene)
{
    ConfigureRendering();
    ConfigureWindow();

    game.Services.AddService<IPlayerStatus>(new PlayerStatus());

    terrainView = CreateTerrainView(rootScene);
    AddSceneLighting(rootScene);
    AddHud(rootScene);
    AddEffects(rootScene);

    var cameraEntity = AddGameplayCamera();
    CreateViewFactories(rootScene, cameraEntity);
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
}

void ConfigureRendering()
{
    var compositor = game.AddGraphicsCompositor();
    compositor.AddCleanUIStage();
    compositor.AddSceneRenderer(new LineSceneRenderer());

    // AddCleanUIStage replaces the compositor's deliberately-disabled post effects with a fresh
    // default set. Disable the screen-space effects that are unsuitable here. Stride's SSAO
    // reconstructs from the camera depth buffer, so dense alpha-cutout grass self-occludes into
    // moving bands and the triangulated terrain gets dark, view-dependent diagonals that look like
    // shadow acne. FXAA then turns those high-contrast depth edges into crawling zig-zags.
    //
    // LocalReflections also allocates R11G11B10_Float buffers, a format missing from Stride's Vulkan
    // backend, and crashes on the first frame.
    if (((ForwardRenderer)compositor.SingleView).PostEffects is PostProcessingEffects postFx)
    {
        postFx.AmbientOcclusion.Enabled = false;
        postFx.LocalReflections.Enabled = false;
        postFx.Antialiasing.Enabled = false;
    }

    // AddGraphicsCompositor() does NOT include particle rendering; add it explicitly
    // or ParticleSystemComponents simulate but never draw.
    // DISABLED: ParticleEmitterRenderFeature crashes with an AccessViolationException
    // on the Vulkan backend (known engine bug, see stride3d/stride#2496 — the official
    // particle samples crash the same way on Linux). Re-enable once Stride fixes it
    // or if we return to OpenGL.
    // game.AddParticleRenderer();

    // DISABLED: the profiler overlay draws through FastTextRenderer, which crashes on
    // Vulkan (use-after-unmap bug: FastTextRenderer.Initialize reads back a mapped
    // pointer after UnmapSubresource; still broken in Stride master). DebugTextSystem
    // uses the same renderer — see the disabled Print call in Update(). Use the UI/HUD
    // (TextBlock) for on-screen text instead; it renders through SpriteBatch, which is fine.
    // game.AddProfiler();
}

void ConfigureWindow()
{
    // Borderless fullscreen (safe on SDL/Linux; keeps the windowed backbuffer format).
    // game.Window.FullscreenIsBorderlessWindow = true;
    // game.GraphicsDeviceManager.IsFullScreen = true;
    game.GraphicsDeviceManager.PreferredBackBufferWidth = 1280;
    game.GraphicsDeviceManager.PreferredBackBufferHeight = 720;
    game.GraphicsDeviceManager.ApplyChanges();
}

void AddSceneLighting(Scene rootScene)
{
    CreateDirectionalLight("DirectionalLight").Scene = rootScene;
    CreateAmbientLight().Scene = rootScene;
}

void AddHud(Scene rootScene)
{
    HUD.CreateUI(game).Scene = rootScene;
    HUD.CreateTerminal(game, inputState, network).Scene = rootScene;

    // Entity/FPS counters, top-left (UI-based replacement for the Vulkan-broken
    // profiler overlay and DebugTextSystem — see the disabled calls above/below).
    HUD.CreateDebugStats(game).Scene = rootScene;
}

void AddEffects(Scene rootScene)
{
    game.AddGroundGizmo(position: new Vector3(-5, 0.1f, -5), showAxisName: true);

    // Disabled along with AddParticleRenderer above (Vulkan particle crash); without
    // the render feature this would simulate invisibly and waste CPU.
    // ParticleExample.CreateAtOrigin().Scene = rootScene;

    // Drives bullet-tracer fade/expiry once per frame (see TracerManager).
    new Entity("TracerSystem") { new TracerSystem() }.Scene = rootScene;

    // Grass is temporarily disabled. Keep GrassField/GpuGrassRenderer available for profiling work.
    // var grassField = GrassField.CreateFollower(game, registry, terrainState);
    // grassField.Scene = rootScene;
}

Entity AddGameplayCamera()
{
    var cameraEntity = game.Add3DCamera();
    LineRenderer.Camera = cameraEntity.Get<CameraComponent>();

    // OpenAL audio (see SoundManager); the camera is the 3D listener.
    game.Services.AddService(new SoundManager(cameraEntity));

    // Script priorities make the camera stack explicit: debug arbitration first, then look camera,
    // then the controller that consumes the camera pose for movement/aim, then reticle/dig readers.
    // Equal-priority SyncScript order is unspecified in Stride.
    cameraEntity.Add(new DebugFlyCameraScript { InputState = inputState, Priority = 0 });
    cameraEntity.Add(new FirstPersonCameraScript { Registry = registry, InputState = inputState, Priority = 10 });
    cameraEntity.Add(new LocalPlayerController { CameraEntity = cameraEntity, Registry = registry, Terrain = terrainState, Mount = weaponMount, WeaponView = localWeaponView, InputState = inputState, Priority = 20 });

    // Dead camera code, kept for comparison/revival:
    // - ShoulderCameraScript is the over-the-shoulder action camera.
    // - ThirdPersonCameraScript is the old high, cursor-aimed camera.
    // cameraEntity.Add(new ShoulderCameraScript { Registry = registry, Priority = 10 });
    // cameraEntity.Add(new ThirdPersonCameraScript { Registry = registry });
    // The cursor reticle belongs to the old camera: the shoulder camera locks the mouse, so there is no
    // cursor to draw one at.
    // Dead code, along with the cursor-aimed ThirdPersonCameraScript it belongs to — the shoulder
    // camera locks the mouse to the centre, so there is no cursor for it to follow.
    // cameraEntity.Add(new CursorReticleScript());

    cameraEntity.Add(new ReticleScript { Registry = registry, InputState = inputState, Priority = 30 });
    cameraEntity.Add(new DigScript { Registry = registry, Terrain = terrainState, Network = network, InputState = inputState, Priority = 30 });

    // Aim line off: it drew where the old cursor-aimed camera was pointing, which the shoulder camera
    // makes redundant — you are already looking down the shot. Kept as dead code like the camera itself.
    // cameraEntity.Add(new AimLineScript { Registry = registry, Mount = weaponMount });
    cameraEntity.Add(new ShotEffectsScript { Registry = registry, Objects = objectRegistry, Network = network, Terrain = terrainState });

    return cameraEntity;
}

void CreateViewFactories(Scene rootScene, Entity cameraEntity)
{
    _ = new PlayerViewFactory(game, rootScene, registry);
    _ = new ObjectViewFactory(game, rootScene, objectRegistry, weaponMount, registry, cameraEntity,
                              localWeaponView, modelLocators);
}

Entity CreateAmbientLight()
{
    return new Entity("Sky Fill")
    {
        new LightComponent
        {
            Intensity = 2.25f,
            Type = new LightAmbient
            {
                Color = new ColorRgbProvider(new Color(170, 195, 230)),
            },
        },
    };
}

Entity CreateDirectionalLight(string? entityName = "Directional Light")
{
    var entity = new Entity(entityName)
    {
        new LightComponent
        {
            Intensity = 11.0f,
            Type = new LightDirectional
            {
                Color = new ColorRgbProvider(new Color(255, 235, 205)),
                Shadow =
                {
                    // Disabled for now: Stride's directional shadow receiver is view-dependent
                    // (cascade selection uses DepthVS and normal-offset bias uses normalWS/NdotL).
                    // On dense alpha-cutout grass and surface-net terrain this showed up as moving
                    // bands and zig-zag shadow acne along complex edges.
                    Enabled = false,
                    Size = LightShadowMapSize.Large,
                    Filter = new LightShadowMapFilterTypePcf { FilterSize = LightShadowMapFilterTypePcfSize.Filter5x5 },
                    PartitionMode = new LightDirectionalShadowMap.PartitionLogarithmic(),
                    ComputeTransmittance = false
                    // Tuning knobs if shadows come back:
                    // CascadeCount = LightShadowMapCascadeCount.TwoCascades,
                    // StabilizationMode = LightShadowMapStabilizationMode.ViewSnapping,
                    // DepthRange = { IsAutomatic = false, ManualMinDistance = 0f, ManualMaxDistance = 80f },
                    // BiasParameters = { DepthBias = 0.02f, NormalOffsetScale = 0f },
                }
            }
        }
    };

    entity.Transform.Position = new Vector3(0, 2.0f, 0);
    entity.Transform.Rotation = Quaternion.RotationX(Stride.Core.Mathematics.MathUtil.DegreesToRadians(-30.0f)) * Quaternion.RotationY(Stride.Core.Mathematics.MathUtil.DegreesToRadians(-180.0f));

    return entity;
}
