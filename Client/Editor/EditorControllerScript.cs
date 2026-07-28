using Demiurge.Editor;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using EInt3 = Demiurge.Editor.Int3;
using NVector3 = System.Numerics.Vector3;

namespace Demiurge;

public sealed class EditorControllerScript : SyncScript
{
    public required ClientInputState InputState { get; init; }
    public required EditorSession Session { get; init; }
    public required EditorToolSettings Settings { get; init; }
    public required EditorStructureState Structures { get; init; }
    public Action? SaveRequested { get; init; }
    public Action? BakeRequested { get; init; }

    private readonly List<Float3> strokeDabs = [];
    private readonly Dictionary<EInt3, EditorBlockPlacement?> blockBefore = [];
    private readonly HashSet<EInt3> blockCells = [];
    private bool leftWasDown;
    private bool rightWasDown;
    private bool deleteWasDown;
    private bool rotateWasDown;
    private bool undoWasDown;
    private bool redoWasDown;
    private bool saveWasDown;
    private bool bakeWasDown;
    private bool terrainModeWasDown;
    private bool blockModeWasDown;
    private bool objectModeWasDown;
    private EditMode activeStrokeMode;
    private bool blockRemoving;
    private Guid? selectedPlacement;

    public NVector3? LodFocus => Entity.Transform.Position;
    public EInt3? TargetCell { get; private set; }
    public bool TargetIsValid { get; private set; }

    public override void Update()
    {
        if (InputState.TerminalOpen)
        {
            CancelGestures();
            CaptureModeHotkeys();
            return;
        }

        HandleModeHotkeys();

        var origin = Entity.Transform.Position.ToNumerics();
        var direction = (Entity.Transform.Rotation * -Vector3.UnitZ).ToNumerics();
        var hit = TerrainRaycast.Cast(Session.Terrain, origin, direction, 2_000f);
        TargetCell = null;
        TargetIsValid = false;

        bool left = Input.IsMouseButtonDown(MouseButton.Left);
        bool right = Input.IsMouseButtonDown(MouseButton.Right);
        bool leftPressed = left && !leftWasDown;
        bool rightPressed = right && !rightWasDown;
        bool leftReleased = !left && leftWasDown;
        bool rightReleased = !right && rightWasDown;

        if (hit is { } terrainHit)
        {
            var cells = EditorTargeting.Cells(terrainHit.Point, terrainHit.Normal);
            TargetCell = cells.Air;
            TargetIsValid = IsTargetValid(terrainHit, cells);
            DrawPreview(terrainHit, cells);

            switch (Settings.Mode)
            {
                case EditorToolMode.Terrain:
                    HandleTerrain(terrainHit, left, right, leftPressed, rightPressed, leftReleased, rightReleased);
                    break;
                case EditorToolMode.Block:
                    HandleBlocks(cells, left, right, leftPressed, rightPressed, leftReleased, rightReleased);
                    break;
                case EditorToolMode.Object:
                    if (leftPressed) HandleObject(origin, direction, terrainHit, cells);
                    break;
            }
        }
        else if (leftReleased || rightReleased)
        {
            CommitTerrain();
            CommitBlocks();
        }

        HandleSelectionKeys();
        HandleShortcuts();
        HandleWheel();
        leftWasDown = left;
        rightWasDown = right;
    }

    private void HandleTerrain(
        TerrainHit hit,
        bool left,
        bool right,
        bool leftPressed,
        bool rightPressed,
        bool leftReleased,
        bool rightReleased)
    {
        if (!TargetIsValid) return;
        if (leftPressed || rightPressed)
        {
            strokeDabs.Clear();
            activeStrokeMode = rightPressed
                ? (Settings.TerrainMode == EditMode.Add ? EditMode.Subtract : EditMode.Add)
                : Settings.TerrainMode;
            AddStrokeDab(hit.Point);
        }

        if ((left || right) && strokeDabs.Count > 0) AddStrokeDab(hit.Point);
        if (leftReleased || rightReleased) CommitTerrain();
    }

    private void AddStrokeDab(NVector3 point)
    {
        float spacing = MathF.Max(0.1f,
            MathF.Min(Settings.TerrainHalfExtent.X,
                MathF.Min(Settings.TerrainHalfExtent.Y, Settings.TerrainHalfExtent.Z)) * 0.5f);
        if (strokeDabs.Count > 0 && NVector3.DistanceSquared(strokeDabs[^1].Vector, point) < spacing * spacing)
            return;
        strokeDabs.Add(Float3.From(point));
    }

    private void CommitTerrain()
    {
        if (strokeDabs.Count == 0) return;
        var stroke = new TerrainStroke
        {
            Id = Guid.NewGuid(),
            Sequence = Session.AllocateSequence(),
            Mode = activeStrokeMode,
            Shape = Settings.TerrainShape,
            HalfExtent = Float3.From(Settings.TerrainHalfExtent),
            Strength = Settings.TerrainStrength,
            MaterialId = BlockCatalog.Id(Settings.TerrainMaterial),
            Dabs = [.. strokeDabs],
        };
        Session.Execute(new AddTerrainStrokeCommand(stroke));
        strokeDabs.Clear();
    }

    private void HandleBlocks(
        EditorTargetCells cells,
        bool left,
        bool right,
        bool leftPressed,
        bool rightPressed,
        bool leftReleased,
        bool rightReleased)
    {
        if (Structures.Selected is { } structure && leftPressed)
        {
            if (!TargetIsValid) return;
            Session.Execute(StructureLibrary.CreatePlacementCommand(
                structure, cells.Air, Structures.QuarterTurns, Structures.MirrorX, Session));
            return;
        }

        if (leftPressed || rightPressed)
        {
            blockBefore.Clear();
            blockCells.Clear();
            blockRemoving = rightPressed;
        }

        if (left || right)
        {
            EInt3 cell = blockRemoving ? cells.Solid : cells.Air;
            if (!EditorValidation.IsCellInBounds(cell)) return;
            if (blockCells.Add(cell)) blockBefore[cell] = Session.BlockAt(cell);
        }

        if (leftReleased || rightReleased) CommitBlocks();
    }

    private void CommitBlocks()
    {
        if (blockCells.Count == 0) return;
        var after = new Dictionary<EInt3, EditorBlockPlacement?>();
        foreach (var cell in blockCells)
            after[cell] = blockRemoving
                ? null
                : new EditorBlockPlacement
                {
                    Id = Guid.NewGuid(),
                    Sequence = Session.AllocateSequence(),
                    Cell = cell,
                    BlockId = BlockCatalog.Id(Settings.Block),
                };
        Session.Execute(new SetBlocksCommand(
            blockRemoving ? "Remove blocks" : "Place blocks", blockBefore, after));
        blockBefore.Clear();
        blockCells.Clear();
    }

    private void HandleObject(
        NVector3 origin,
        NVector3 direction,
        TerrainHit terrainHit,
        EditorTargetCells cells)
    {
        var picked = PickPlacement(origin, direction, terrainHit.Distance);
        if (picked is { } id)
        {
            selectedPlacement = id;
            return;
        }

        if (!TargetIsValid) return;

        if (selectedPlacement is { } selected && Session.Placement(selected) is { } existing)
        {
            Session.Execute(new UpdatePlacementCommand(
                $"Move {existing.Kind}", existing, existing with { Cell = cells.Air }));
            selectedPlacement = null;
            return;
        }

        if (Settings.ObjectKind == EditorObjectChoiceKind.None || Settings.ObjectId is null) return;
        var kind = Settings.ObjectKind switch
        {
            EditorObjectChoiceKind.Pickup => EditorPlacementKind.Pickup,
            EditorObjectChoiceKind.Mob => EditorPlacementKind.Mob,
            EditorObjectChoiceKind.Spawn => EditorPlacementKind.PlayerSpawn,
            _ => throw new InvalidOperationException(),
        };
        Session.Execute(new AddPlacementCommand(new EditorPlacement
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ArchetypeId = Settings.ObjectId,
            Cell = cells.Air,
            Yaw = Settings.ObjectYaw,
        }));
    }

    private Guid? PickPlacement(NVector3 origin, NVector3 direction, float terrainDistance)
    {
        Guid? best = null;
        float bestDistance = terrainDistance;
        foreach (var placement in Session.Document.Placements)
        {
            var min = new NVector3(placement.Cell.X, placement.Cell.Y, placement.Cell.Z);
            var max = min + NVector3.One;
            if (RayBox(origin, direction, min, max, out float distance) && distance < bestDistance)
            {
                best = placement.Id;
                bestDistance = distance;
            }
        }
        return best;
    }

    private void HandleSelectionKeys()
    {
        bool delete = Input.IsKeyDown(Keys.Delete);
        if (delete && !deleteWasDown && selectedPlacement is { } id && Session.Placement(id) is { } placement)
        {
            Session.Execute(new DeletePlacementCommand(placement));
            selectedPlacement = null;
        }
        deleteWasDown = delete;

        bool rotate = Input.IsKeyDown(Keys.R);
        if (rotate && !rotateWasDown && selectedPlacement is { } selected && Session.Placement(selected) is { } current)
            Session.Execute(new UpdatePlacementCommand(
                $"Rotate {current.Kind}", current, current with { Yaw = current.Yaw + MathF.PI / 2f }));
        rotateWasDown = rotate;

        if (Input.IsKeyDown(Keys.Escape)) selectedPlacement = null;
    }

    private void HandleShortcuts()
    {
        bool ctrl = Input.IsKeyDown(Keys.LeftCtrl) || Input.IsKeyDown(Keys.RightCtrl);
        bool shift = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);

        bool undo = ctrl && Input.IsKeyDown(Keys.Z);
        if (undo && !undoWasDown) Session.Undo();
        undoWasDown = undo;

        bool redo = ctrl && Input.IsKeyDown(Keys.Y);
        if (redo && !redoWasDown) Session.Redo();
        redoWasDown = redo;

        bool save = ctrl && !shift && Input.IsKeyDown(Keys.S);
        if (save && !saveWasDown) SaveRequested?.Invoke();
        saveWasDown = save;

        bool bake = ctrl && shift && Input.IsKeyDown(Keys.B);
        if (bake && !bakeWasDown) BakeRequested?.Invoke();
        bakeWasDown = bake;
    }

    private void HandleModeHotkeys()
    {
        bool terrain = IsModeKeyDown(Keys.D1, Keys.NumPad1);
        bool block = IsModeKeyDown(Keys.D2, Keys.NumPad2);
        bool objects = IsModeKeyDown(Keys.D3, Keys.NumPad3);

        EditorToolMode? requested = terrain && !terrainModeWasDown
            ? EditorToolMode.Terrain
            : block && !blockModeWasDown
                ? EditorToolMode.Block
                : objects && !objectModeWasDown
                    ? EditorToolMode.Object
                    : null;

        terrainModeWasDown = terrain;
        blockModeWasDown = block;
        objectModeWasDown = objects;

        if (requested is not { } mode || mode == Settings.Mode) return;

        CommitTerrain();
        CommitBlocks();
        Settings.Mode = mode;

        // A mouse button already held while changing tools must not begin a gesture in the new mode.
        leftWasDown = Input.IsMouseButtonDown(MouseButton.Left);
        rightWasDown = Input.IsMouseButtonDown(MouseButton.Right);
    }

    private void CaptureModeHotkeys()
    {
        terrainModeWasDown = IsModeKeyDown(Keys.D1, Keys.NumPad1);
        blockModeWasDown = IsModeKeyDown(Keys.D2, Keys.NumPad2);
        objectModeWasDown = IsModeKeyDown(Keys.D3, Keys.NumPad3);
    }

    private bool IsModeKeyDown(Keys numberRow, Keys numberPad)
        => Input.IsKeyDown(numberRow) || Input.IsKeyDown(numberPad);

    private void HandleWheel()
    {
        float wheel = Input.MouseWheelDelta;
        if (Settings.Mode != EditorToolMode.Terrain || MathF.Abs(wheel) < 0.01f) return;

        bool shift = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);
        if (shift)
            Settings.TerrainStrength = Math.Clamp(Settings.TerrainStrength + MathF.Sign(wheel) * 0.1f, 0.1f, 1f);
        else
            Settings.TerrainHalfExtent = System.Numerics.Vector3.Max(
                new NVector3(0.5f),
                Settings.TerrainHalfExtent + new NVector3(MathF.Sign(wheel) * 0.25f));
    }

    private void DrawPreview(TerrainHit hit, EditorTargetCells cells)
    {
        var targetColor = TargetIsValid
            ? new Color(255, 255, 255, 230)
            : new Color(255, 70, 70, 240);
        switch (Settings.Mode)
        {
            case EditorToolMode.Terrain:
                if (Settings.TerrainShape == EditShape.Sphere)
                    WorldPreviewRenderer.Sphere(hit.Point, Settings.TerrainHalfExtent.X, targetColor);
                else
                    WorldPreviewRenderer.Cube(
                        hit.Point - Settings.TerrainHalfExtent,
                        hit.Point + Settings.TerrainHalfExtent, targetColor);
                break;
            case EditorToolMode.Block:
                if (Structures.Selected is { } structure)
                {
                    foreach (var block in structure.Blocks)
                    {
                        var offset = StructureLibrary.Transform(
                            block.Offset, Structures.QuarterTurns, Structures.MirrorX);
                        WorldPreviewRenderer.Cell(new EInt3(
                            cells.Air.X + offset.X,
                            cells.Air.Y + offset.Y,
                            cells.Air.Z + offset.Z), targetColor);
                    }
                }
                else
                {
                    WorldPreviewRenderer.Cell(
                        Input.IsMouseButtonDown(MouseButton.Right) ? cells.Solid : cells.Air, targetColor);
                }
                break;
            case EditorToolMode.Object:
                WorldPreviewRenderer.Cell(
                    cells.Air,
                    TargetIsValid ? new Color(255, 220, 80, 230) : targetColor);
                break;
        }

        if (selectedPlacement is { } id && Session.Placement(id) is { } selected)
            WorldPreviewRenderer.Cell(selected.Cell, new Color(80, 220, 255, 240));
        foreach (var placement in Session.Document.Placements.Where(p => p.Kind == EditorPlacementKind.PlayerSpawn))
            WorldPreviewRenderer.Cell(placement.Cell, new Color(80, 255, 120, 220));
    }

    private bool IsTargetValid(TerrainHit hit, EditorTargetCells cells)
    {
        return Settings.Mode switch
        {
            EditorToolMode.Terrain => EditorValidation.IsBrushInBounds(
                Float3.From(hit.Point), Float3.From(Settings.TerrainHalfExtent)),
            EditorToolMode.Block when Structures.Selected is { } structure =>
                structure.Blocks.All(block =>
                {
                    var offset = StructureLibrary.Transform(
                        block.Offset, Structures.QuarterTurns, Structures.MirrorX);
                    return EditorValidation.IsCellInBounds(new EInt3(
                        cells.Air.X + offset.X,
                        cells.Air.Y + offset.Y,
                        cells.Air.Z + offset.Z));
                }),
            EditorToolMode.Block => EditorValidation.IsCellInBounds(
                Input.IsMouseButtonDown(MouseButton.Right) ? cells.Solid : cells.Air),
            EditorToolMode.Object => IsObjectTargetValid(cells.Air),
            _ => false,
        };
    }

    private bool IsObjectTargetValid(EInt3 cell)
    {
        if (!EditorValidation.IsCellInBounds(cell)) return false;
        bool isSpawn = Settings.ObjectKind == EditorObjectChoiceKind.Spawn
            || selectedPlacement is { } id
                && Session.Placement(id)?.Kind == EditorPlacementKind.PlayerSpawn;
        if (!isSpawn) return true;

        var feet = new NVector3(cell.X + 0.5f, cell.Y, cell.Z + 0.5f);
        return TerrainCollision.TryDeepestContact(
                Session.Terrain, PlayerMovement.Body, feet, out var contact)
            && contact.Distance >= PlayerMovement.Body.Radius;
    }

    private void CancelGestures()
    {
        strokeDabs.Clear();
        blockBefore.Clear();
        blockCells.Clear();
        leftWasDown = rightWasDown = false;
    }

    private static bool RayBox(
        NVector3 origin,
        NVector3 direction,
        NVector3 min,
        NVector3 max,
        out float distance)
    {
        float near = 0f;
        float far = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) { distance = 0; return false; }
                continue;
            }
            float t1 = (lo - o) / d;
            float t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far) { distance = 0; return false; }
        }
        distance = near;
        return true;
    }
}
