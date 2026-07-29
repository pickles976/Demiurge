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
        playerStatus = new PlayerStatus();
        game.Services.AddService<IPlayerStatus>(playerStatus);

        terrainView = embedding?.TerrainView
            ?? new ClientTerrain(scene, new ChunkMeshFactory(game, new TerrainMaterials(game)), terrainState);

        Add(HUD.CreateUI(game));
        Add(HUD.CreateDebugStats(game));
        Add(new Entity("TracerSystem")
        {
            new TracerSystem(),
            new GrenadeExplosionScript { Objects = objectRegistry },
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
        localServer?.Step();
        network.Update();
        terrainState.Drain();
        var lodFocus = registry.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
        terrainView?.RebuildDirty(lodFocus);
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
