using Demiurge.Editor;
using Demiurge.GameClient;
using Demiurge.GameServer;
using Stride.CommunityToolkit.Engine;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;

namespace Demiurge;

public sealed class EditorClientSession : IClientSession
{
    private readonly Game game;
    private readonly ClientInputState inputState;
    private readonly List<Entity> ownedEntities = [];
    private readonly EditorInteractionState interactionState = new();
    private readonly HashSet<ChunkIndex> playtestTerrainChanges = [];

    private Scene scene = null!;
    private EditorTerrainState? terrainState;
    private ClientTerrain? terrainView;
    private EditorPlacementViewFactory? placementViews;
    private Entity? camera;
    private RuntimeClientSession? playtest;

    public ClientSessionKind Kind => ClientSessionKind.Editor;
    public bool IsPlaytesting => playtest is not null;
    public NetworkManager? PlaytestNetwork => playtest?.Network;

    /// <summary>The playtest's in-process transport, for <c>net seed</c> / <c>net log</c>. A playtest
    /// hosts its own server, so this is non-null whenever one is running.</summary>
    public Demiurge.Net.InProcessNetwork? PlaytestInProcessTransport => playtest?.InProcessTransport;
    public EditorSession Editor { get; }
    public EditorToolSettings Settings { get; }
    public EditorStructureState Structures { get; }
    public EditorControllerScript Controller { get; private set; } = null!;

    /// <summary>
    /// Where a playtest started right now would put the player: exactly where the fly camera is,
    /// so you drop into the spot you were looking at. Null before the session has started.
    /// </summary>
    public System.Numerics.Vector3? PlaytestSpawn =>
        camera?.Transform.Position.ToNumerics();

    public event Action? SaveRequested;
    public event Action? BakeRequested;
    public event Action<string>? FeedbackRequested;

    public EditorClientSession(Game game, ClientInputState inputState, EditorDocument document)
        : this(
            game,
            inputState,
            new EditorSession(document),
            new EditorToolSettings(),
            new EditorStructureState())
    {
    }

    public EditorClientSession(
        Game game,
        ClientInputState inputState,
        EditorSession editor,
        EditorToolSettings settings,
        EditorStructureState structures)
    {
        this.game = game;
        this.inputState = inputState;
        Editor = editor;
        Settings = settings;
        Structures = structures;
    }

    public void Start(Scene scene)
    {
        this.scene = scene;
        terrainState = new EditorTerrainState(Editor);
        terrainView = new ClientTerrain(
            scene, new ChunkMeshFactory(game, new TerrainMaterials(game)), terrainState);

        camera = game.Add3DCamera();
        ownedEntities.Add(camera);
        LineRenderer.Camera = camera.Get<CameraComponent>();
        var surface = SurfaceQuery.SurfacePosition(Editor.Terrain, 0.5f, 0.5f);
        camera.Transform.Position = new Vector3(surface.X, surface.Y + 12f, surface.Z + 12f);
        camera.Add(new EditorCameraScript
        {
            InputState = inputState,
            InteractionState = interactionState,
            Priority = 0,
        });
        Controller = new EditorControllerScript
        {
            InputState = inputState,
            Session = Editor,
            Settings = Settings,
            Structures = Structures,
            InteractionState = interactionState,
            SaveRequested = () => SaveRequested?.Invoke(),
            BakeRequested = () => BakeRequested?.Invoke(),
            FeedbackRequested = message => FeedbackRequested?.Invoke(message),
            Priority = 10,
        };
        camera.Add(Controller);

        placementViews = new EditorPlacementViewFactory(game, scene, Editor);
        var status = HUD.CreateEditorStatus(
            game, Settings, Editor, Controller, interactionState, Structures, inputState);
        status.Scene = scene;
        ownedEntities.Add(status);
        terrainState.AnnounceAll();
    }

    public void Update(GameTime time)
    {
        if (camera is null) return;
        if (playtest is not null)
            playtest.Update(time);
        else
            // The editor's fly camera IS the observer, so unlike the runtime both the eye and the
            // facing come from it. TerrainLod's "never the camera" rule is about not letting a SECOND
            // camera override the player's view; here there is only one.
            terrainView?.RebuildDirty(TerrainViewBuilder.For(
                game,
                camera.Get<CameraComponent>(),
                camera.Transform.Position.ToNumerics(),
                TerrainViewBuilder.Facing(camera)));
    }

    public void StartPlaytest(RuntimeMap runtimeMap)
    {
        if (playtest is not null) throw new InvalidOperationException("Playtest is already active");
        if (camera is null || terrainView is null)
            throw new InvalidOperationException("Editor session has not started");

        var serverMap = new RuntimeMap
        {
            MapId = runtimeMap.MapId,
            Name = runtimeMap.Name,
            Terrain = runtimeMap.Terrain.DeepClone(),
            Placements = runtimeMap.Placements,
            SourceHash = runtimeMap.SourceHash.ToArray(),
        };
        var localTerrain = new TerrainState(Editor.Terrain);
        var embedding = new RuntimeClientEmbedding(
            localTerrain,
            terrainView,
            camera,
            TrackPlaytestTerrainEdit);
        var candidate = new RuntimeClientSession(
            game,
            inputState,
            NetworkConfig.ServerHost,
            new ServerOptions
            {
                AllowCheats = true,
                RuntimeMap = serverMap,
                SpawnOverride = PlaytestSpawn,
            },
            embedding);

        interactionState.Playtesting = true;
        try
        {
            candidate.Start(scene);
            playtest = candidate;
            placementViews?.Dispose();
            placementViews = null;
        }
        catch
        {
            candidate.Dispose();
            interactionState.Playtesting = false;
            RestorePlaytestTerrain();
            throw;
        }
    }

    public void StopPlaytest()
        => StopPlaytest(restorePlacementViews: true);

    private void StopPlaytest(bool restorePlacementViews)
    {
        if (playtest is null) return;

        interactionState.Playtesting = false;
        try
        {
            playtest.Dispose();
        }
        finally
        {
            playtest = null;
            RestorePlaytestTerrain();
            if (restorePlacementViews)
                placementViews = new EditorPlacementViewFactory(game, scene, Editor);
        }
    }

    public void Dispose()
    {
        StopPlaytest(restorePlacementViews: false);
        placementViews?.Dispose();
        terrainView?.Dispose();
        terrainState?.Dispose();
        foreach (var entity in ownedEntities.ToArray()) entity.Scene = null;
        ownedEntities.Clear();
    }

    private void TrackPlaytestTerrainEdit(
        System.Numerics.Vector3 min,
        System.Numerics.Vector3 max)
    {
        var first = ChunkTransforms.ChunkAt(
            (int)MathF.Floor(min.X),
            (int)MathF.Floor(min.Z));
        var last = ChunkTransforms.ChunkAt(
            (int)MathF.Ceiling(max.X),
            (int)MathF.Ceiling(max.Z));
        for (int z = first.z; z <= last.z; z++)
            for (int x = first.x; x <= last.x; x++)
                playtestTerrainChanges.Add(new ChunkIndex { x = x, z = z });
    }

    private void RestorePlaytestTerrain()
    {
        if (playtestTerrainChanges.Count == 0) return;
        Editor.RestoreTerrain(playtestTerrainChanges);
        playtestTerrainChanges.Clear();
    }
}

public sealed class EditorStructureState
{
    /// <summary>
    /// The structure being placed, and how it is oriented. There is no capture state: what a save
    /// captures is the pad's own contents, so there is nothing for the user to set up first.
    /// </summary>
    public StructureDocument? Selected { get; set; }
    public int QuarterTurns { get; set; }
    public bool MirrorX { get; set; }
}
