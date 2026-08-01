using System.Diagnostics;
using Demiurge.GameClient;
using Demiurge.GameServer;
using Stride.CommunityToolkit.Engine;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;

namespace Demiurge;

public sealed record RuntimeClientEmbedding(
    TerrainState Terrain,
    ClientTerrain TerrainView,
    Entity Camera,
    Action<System.Numerics.Vector3, System.Numerics.Vector3>? TerrainEdited = null);

public sealed class RuntimeClientSession : IClientSession
{
    private readonly Game game;
    private readonly string host;
    private readonly ServerOptions? localServerOptions;
    private readonly List<Entity> ownedEntities = [];
    private readonly RuntimeClientEmbedding? embedding;

    private readonly NetworkManager network;
    private readonly TerrainState terrainState;
    private readonly ChunkTcpClient? chunkStream;
    private readonly ModelLocators modelLocators;
    private readonly WeaponMount weaponMount;
    private readonly LocalWeaponView localWeaponView = new();
    private readonly SpawnReadiness spawnReadiness = new();
    private FrameBreakdown frame;

    /// <summary>
    /// Where the client's own Update goes, once a second. Everything here runs on the MAIN thread,
    /// and in singleplayer that includes the whole server tick — which is the point: the 16.6 ms
    /// frame budget has to cover both, so a server cost is a frame cost.
    /// </summary>
    private struct FrameBreakdown
    {
        private static readonly Stride.Core.Diagnostics.Logger Log =
            Stride.Core.Diagnostics.GlobalLogger.GetLogger("Frame");

        private long windowStart;
        private int frames;
        private long serverTicks, networkTicks, drainTicks, terrainTicks;
        private double worstMs;

        public void Record(long t0, long t1, long t2, long t3, long t4)
        {
            frames++;
            serverTicks += t1 - t0;
            networkTicks += t2 - t1;
            drainTicks += t3 - t2;
            terrainTicks += t4 - t3;
            worstMs = Math.Max(worstMs, (t4 - t0) * 1000.0 / Stopwatch.Frequency);

            long now = Stopwatch.GetTimestamp();
            if (windowStart == 0) windowStart = now;
            double elapsed = (now - windowStart) / (double)Stopwatch.Frequency;
            if (elapsed < 1.0) return;

            double perFrame = 1000.0 / Stopwatch.Frequency / frames;
            Log.Info(
                $"frame: {frames} fps | server {serverTicks * perFrame:F2} ms "
              + $"| net {networkTicks * perFrame:F2} | drain {drainTicks * perFrame:F2} "
              + $"| terrain {terrainTicks * perFrame:F2} "
              + $"| session total {(serverTicks + networkTicks + drainTicks + terrainTicks) * perFrame:F2} "
              + $"| worst frame {worstMs:F1} ms");

            windowStart = now;
            frames = 0;
            serverTicks = networkTicks = drainTicks = terrainTicks = 0;
            worstMs = 0;
        }
    }
    private readonly ClientInputState inputState;
    private readonly PlayerRegistry registry;
    private readonly ObjectRegistry objectRegistry;

    private Scene scene = null!;
    private ServerHost? localServer;
    private ClientTerrain? terrainView;
    private PlayerViewFactory? playerViews;
    private RagdollViewFactory? ragdolls;
    private ObjectViewFactory? objectViews;
    private SoundManager? sound;
    private IPlayerStatus? playerStatus;
    private Entity? camera;

    /// <summary>How much ground has to be meshed before the player is let loose. A little over one
    /// chunk, so the hole they are standing in and the ones they can immediately walk to are real.</summary>
    private const float SpawnPreloadRadius = 24f;

    public ClientSessionKind Kind => ClientSessionKind.Runtime;
    public NetworkManager Network => network;

    public RuntimeClientSession(
        Game game,
        ClientInputState inputState,
        string? host = null,
        ServerOptions? localServerOptions = null,
        RuntimeClientEmbedding? embedding = null)
    {
        this.game = game;
        this.inputState = inputState;
        this.host = host ?? NetworkConfig.ServerHost;
        this.localServerOptions = localServerOptions;
        this.embedding = embedding;
        terrainState = embedding?.Terrain ?? new TerrainState();
        network = new NetworkManager(this.host);
        modelLocators = ModelLocators.Load();
        weaponMount = new WeaponMount(modelLocators, ItemCosmetics.Model);
        registry = new PlayerRegistry(network, terrainState, weaponMount);
        objectRegistry = new ObjectRegistry(network);

        if (embedding is null)
        {
            chunkStream = new ChunkTcpClient();
            chunkStream.ChunkReceived += terrainState.Receive;
            network.Welcomed += OnWelcomed;
        }
        else
        {
            terrainState.RegionEdited += OnEmbeddedTerrainEdited;
        }
        network.TerrainEdited += terrainState.ReceiveEdit;
        objectRegistry.ObjectSpawned += OnObjectSpawned;
        registry.PlayerJoined += OnPlayerJoined;
        objectRegistry.ObjectDespawned += OnObjectDespawned;
    }

    public void Start(Scene scene)
    {
        this.scene = scene;
        if (localServerOptions is not null)
        {
            localServer = new ServerHost(localServerOptions);
            localServer.Start();
        }

        game.Services.AddService(network);
        game.Services.AddService(registry);
        game.Services.AddService(spawnReadiness);
        playerStatus = new PlayerStatus();
        game.Services.AddService<IPlayerStatus>(playerStatus);

        terrainView = embedding?.TerrainView
            ?? new ClientTerrain(scene, new ChunkMeshFactory(game, new TerrainMaterials(game)), terrainState);

        Add(HUD.CreateUI(game));
        Add(HUD.CreateDebugStats(game));
        Add(new Entity("TracerSystem")
        {
            new TracerSystem(),
            new GrenadeExplosionScript { Objects = objectRegistry, Players = registry },
            new NpcTrackerScript { Registry = registry },
            new DamageFeedbackScript { Registry = registry },
            new ActionSoundsScript
            {
                Registry = registry,
                Objects = objectRegistry,
                Terrain = terrainState,
            },
        });

        camera = embedding?.Camera ?? game.Add3DCamera();
        if (embedding is null) ownedEntities.Add(camera);
        LineRenderer.Camera = camera.Get<CameraComponent>();
        sound = new SoundManager(camera);
        game.Services.AddService(sound);

        camera.Add(new DebugFlyCameraScript { InputState = inputState, Priority = 0 });
        camera.Add(new FirstPersonCameraScript { Registry = registry, InputState = inputState, Priority = 10 });
        camera.Add(new KillcamCameraScript
        {
            Registry = registry,
            Terrain = terrainState,
            InputState = inputState,
            Priority = 15,
        });
        camera.Add(new LocalPlayerController
        {
            CameraEntity = camera,
            Registry = registry,
            Terrain = terrainState,
            Mount = weaponMount,
            WeaponView = localWeaponView,
            InputState = inputState,
            Readiness = spawnReadiness,
            Priority = 20,
        });
        camera.Add(new ReticleScript { Registry = registry, InputState = inputState, Priority = 30 });
        camera.Add(new DigScript
        {
            Registry = registry,
            Terrain = terrainState,
            Network = network,
            InputState = inputState,
            Priority = 30,
        });
        camera.Add(new ShotEffectsScript
        {
            Registry = registry,
            Objects = objectRegistry,
            Network = network,
            Terrain = terrainState,
        });

        playerViews = new PlayerViewFactory(game, scene, registry);
        ragdolls = new RagdollViewFactory(game, scene, registry, objectRegistry, terrainState);
        objectViews = new ObjectViewFactory(
            game, scene, objectRegistry, weaponMount, registry, camera,
            localWeaponView, modelLocators);

        network.Connect();
    }

    public void Update(GameTime time)
    {
        long t0 = Stopwatch.GetTimestamp();
        localServer?.Step();
        long t1 = Stopwatch.GetTimestamp();
        network.Update();
        long t2 = Stopwatch.GetTimestamp();
        terrainState.Drain();
        long t3 = Stopwatch.GetTimestamp();
        var lodFocus = registry.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
        terrainView?.RebuildDirty(lodFocus);
        frame.Record(t0, t1, t2, t3, Stopwatch.GetTimestamp());

        // Hold the player still until the ground they are standing on exists. Spawning into a world
        // that is still assembling itself is the one loading artefact a player cannot look away
        // from, because it is underneath them. Latched: once deployed, a later streaming hitch must
        // not freeze someone mid-firefight.
        spawnReadiness.Ready |= registry.LocalPlayer is not null
            && terrainView?.IsMeshedAround(lodFocus, SpawnPreloadRadius) == true;
    }

    public void Dispose()
    {
        if (chunkStream is not null) chunkStream.ChunkReceived -= terrainState.Receive;
        network.TerrainEdited -= terrainState.ReceiveEdit;
        if (embedding is null)
            network.Welcomed -= OnWelcomed;
        else
            terrainState.RegionEdited -= OnEmbeddedTerrainEdited;
        objectRegistry.ObjectSpawned -= OnObjectSpawned;
        registry.PlayerJoined -= OnPlayerJoined;
        objectRegistry.ObjectDespawned -= OnObjectDespawned;

        chunkStream?.Dispose();
        objectViews?.Dispose();
        ragdolls?.Dispose();
        playerViews?.Dispose();
        if (embedding is null) terrainView?.Dispose();
        objectRegistry.Dispose();
        registry.Dispose();
        network.Dispose();

        if (embedding is not null && camera is not null) RemoveRuntimeCameraScripts(camera);
        foreach (var entity in ownedEntities.ToArray()) entity.Scene = null;
        ownedEntities.Clear();

        if (sound is not null) game.Services.RemoveService(sound);
        if (playerStatus is not null) game.Services.RemoveService<IPlayerStatus>(playerStatus);
        game.Services.RemoveService(spawnReadiness);
        game.Services.RemoveService(registry);
        game.Services.RemoveService(network);

        localServer?.Dispose();
        localServer = null;
    }

    private void Add(Entity entity)
    {
        entity.Scene = scene;
        ownedEntities.Add(entity);
    }

    private void OnWelcomed(WelcomeData welcome) => chunkStream!.Connect(host, welcome.ChunkToken);

    private void OnEmbeddedTerrainEdited(
        System.Numerics.Vector3 min,
        System.Numerics.Vector3 max)
    {
        terrainView?.MarkRegionDirty(min, max);
        embedding?.TerrainEdited?.Invoke(min, max);
    }

    private static void RemoveRuntimeCameraScripts(Entity entity)
    {
        entity.Remove<DebugFlyCameraScript>();
        entity.Remove<FirstPersonCameraScript>();
        entity.Remove<KillcamCameraScript>();
        entity.Remove<LocalPlayerController>();
        entity.Remove<ReticleScript>();
        entity.Remove<DigScript>();
        entity.Remove<ShotEffectsScript>();
    }

    private void OnObjectSpawned(NetObject obj)
    {
        if (registry.LocalPlayer is { } local) LinkOwned(local, obj);
    }

    private void OnPlayerJoined(Player player)
    {
        if (player is not LocalPlayer local) return;
        foreach (var obj in objectRegistry.Objects) LinkOwned(local, obj);
    }

    private void OnObjectDespawned(NetObject obj)
    {
        if (registry.LocalPlayer is not { } local) return;
        if (obj.Has.HasFlag(NetComponents.Weapon)) local.Unequip(obj);
        if (ReferenceEquals(local.Status, obj)) local.Status = null;
    }

    private void LinkOwned(LocalPlayer local, NetObject obj)
    {
        if (!obj.Has.HasFlag(NetComponents.Owner) || obj.Owner.PlayerId != network.ClientId) return;
        if (obj.Has.HasFlag(NetComponents.Weapon)
            && obj.Has.HasFlag(NetComponents.Attachment)
            && (obj.Attachment.Slot == EquipSlot.Hand
                || HotbarConfig.TryFromStorageSlot(obj.Attachment.Slot, out _)))
            local.Equip(obj);
        if (obj.Type == ObjectType.PlayerStatus) local.Status = obj;
    }
}
