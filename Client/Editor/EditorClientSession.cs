using Demiurge.Editor;
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

    private Scene scene = null!;
    private EditorTerrainState? terrainState;
    private ClientTerrain? terrainView;
    private EditorPlacementViewFactory? placementViews;
    private Entity? camera;

    public ClientSessionKind Kind => ClientSessionKind.Editor;
    public EditorSession Editor { get; }
    public EditorToolSettings Settings { get; } = new();
    public EditorStructureState Structures { get; } = new();
    public EditorControllerScript Controller { get; private set; } = null!;
    public event Action? SaveRequested;
    public event Action? BakeRequested;

    public EditorClientSession(Game game, ClientInputState inputState, EditorDocument document)
    {
        this.game = game;
        this.inputState = inputState;
        Editor = new EditorSession(document);
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
        camera.Add(new EditorCameraScript { InputState = inputState, Priority = 0 });
        Controller = new EditorControllerScript
        {
            InputState = inputState,
            Session = Editor,
            Settings = Settings,
            Structures = Structures,
            SaveRequested = () => SaveRequested?.Invoke(),
            BakeRequested = () => BakeRequested?.Invoke(),
            Priority = 10,
        };
        camera.Add(Controller);

        placementViews = new EditorPlacementViewFactory(game, scene, Editor);
        var status = HUD.CreateEditorStatus(game, Settings, Editor);
        status.Scene = scene;
        ownedEntities.Add(status);
        terrainState.AnnounceAll();
    }

    public void Update(GameTime time)
    {
        if (camera is null) return;
        terrainView?.RebuildDirty(camera.Transform.Position.ToNumerics());
    }

    public void Dispose()
    {
        placementViews?.Dispose();
        terrainView?.Dispose();
        terrainState?.Dispose();
        foreach (var entity in ownedEntities.ToArray()) entity.Scene = null;
        ownedEntities.Clear();
    }
}

public sealed class EditorStructureState
{
    public StructureDocument? Selected { get; set; }
    public int QuarterTurns { get; set; }
    public bool MirrorX { get; set; }
    public Demiurge.Editor.Int3? Corner1 { get; set; }
    public Demiurge.Editor.Int3? Corner2 { get; set; }
    public Demiurge.Editor.Int3? Pivot { get; set; }
}
