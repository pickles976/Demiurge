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
    public required EditorInteractionState InteractionState { get; init; }
    public Action? SaveRequested { get; init; }
    public Action? BakeRequested { get; init; }
    public Action<string>? FeedbackRequested { get; init; }

    private readonly List<Float3> strokeDabs = [];
    private readonly List<System.Numerics.Vector2> groveDabs = [];
    private bool groveErasing;
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
    private bool structureModeWasDown;
    private EditMode activeStrokeMode;
    private bool blockRemoving;
    private Guid? selectedPlacement;

    /// <summary>How far object picking reaches when no terrain is behind the cursor.</summary>
    private const float ObjectPickRange = 2_000f;

    public NVector3? LodFocus => Entity.Transform.Position;
    public EInt3? TargetCell { get; private set; }
    public bool TargetIsValid { get; private set; }
    public Guid? SelectedPlacementId => selectedPlacement;
    public Guid? HoveredPlacementId { get; private set; }

    public void SelectPlacement(Guid id, bool announce = true)
    {
        var placement = Session.Placement(id)
            ?? throw new ArgumentException($"Placement {id} does not exist");
        selectedPlacement = id;
        Settings.Mode = EditorToolMode.Object;
        if (announce)
            FeedbackRequested?.Invoke(
                $"Selected {placement.Kind.ToString().ToLowerInvariant()} placement " +
                EditorPlacementIds.Display(id));
    }

    public override void Update()
    {
        if (InteractionState.Playtesting)
        {
            CaptureDisabledInput();
            return;
        }

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

        // Objects are picked against their own bounds, so one hovering over a pit or silhouetted
        // against the sky stays clickable even though the ray never reaches terrain.
        HoveredPlacementId = Settings.Mode == EditorToolMode.Object
            ? EditorPlacementPicker.Pick(
                Session.Document.Placements,
                Session.Terrain,
                origin,
                direction,
                hit?.Distance ?? ObjectPickRange)
            : null;

        if (hit is { } terrainHit)
        {
            var objectCells = EditorTargeting.Cells(terrainHit.Point, terrainHit.Normal);
            var blockSamples = EditorTargeting.Samples(terrainHit.Point, terrainHit.Normal);
            // Structure mode targets sample cells like block mode does — it is placing and
            // capturing blocks, and `editor structure corner` reads this.
            TargetCell = Settings.Mode is EditorToolMode.Block or EditorToolMode.Structure
                ? blockSamples.Air
                : objectCells.Air;
            TargetIsValid = IsTargetValid(terrainHit, objectCells, blockSamples);
            DrawPreview(terrainHit, objectCells, blockSamples);

            switch (Settings.Mode)
            {
                case EditorToolMode.Terrain:
                    HandleTerrain(terrainHit, left, right, leftPressed, rightPressed, leftReleased, rightReleased);
                    break;
                case EditorToolMode.Block:
                    HandleBlocks(
                        blockSamples, left, right, leftPressed, rightPressed, leftReleased, rightReleased);
                    break;
                case EditorToolMode.Structure:
                    HandleStructures(blockSamples, leftPressed);
                    break;
                case EditorToolMode.Object:
                    // The grove brush is a brush, and strokes here work the way they do everywhere
                    // else in the editor: accumulate while held, resolve once on release.
                    if (Settings.ObjectKind == EditorObjectChoiceKind.TreeBrush)
                        HandleGrove(
                            objectCells, left, right, leftPressed, rightPressed,
                            leftReleased, rightReleased);
                    else if (leftPressed) HandleObject(objectCells);
                    break;
            }
        }
        else if (leftReleased || rightReleased)
        {
            CommitTerrain();
            CommitBlocks();
            CommitGrove();
        }

        if (Settings.Mode == EditorToolMode.Object)
        {
            // Selecting an object needs no terrain under the cursor; placing one does, and that
            // path stays inside the terrain-hit branch above.
            if (hit is null && leftPressed && HoveredPlacementId is { } picked) SelectPlacement(picked);
            // Right-drag is the grove eraser, so it must not also mean "delete the placement under
            // the cursor" — which would fight it, one tree per click, over the same ground.
            if (rightPressed && Settings.ObjectKind != EditorObjectChoiceKind.TreeBrush)
                HandleObjectRightClick();
            DrawHoveredPlacement();
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
        if (leftPressed || rightPressed)
        {
            blockBefore.Clear();
            blockCells.Clear();
            blockRemoving = rightPressed;
        }

        if (left || right)
        {
            if (!TargetIsValid) return;
            EInt3 anchor = blockRemoving ? cells.Solid : cells.Air;
            foreach (var cell in BlockBrush.Cells(anchor, Settings.BlockSize))
                if (blockCells.Add(cell))
                    blockBefore[cell] = Session.BlockAt(cell);
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

    /// <summary>
    /// Structure mode places the selected structure, and does nothing without one.
    ///
    /// There is deliberately no capture interaction. What a save captures is the whole pad, bounded
    /// by the blocks already on it, so there is no region for the user to mark out — the corners
    /// this mode used to ask for were a question the document could answer itself.
    /// </summary>
    private void HandleStructures(EditorTargetCells cells, bool leftPressed)
    {
        if (Structures.Selected is not { } structure || !leftPressed || !TargetIsValid) return;
        Session.Execute(StructureLibrary.CreatePlacementCommand(
            structure, cells.Air, Structures.QuarterTurns, Structures.MirrorX, Session));
    }

    private void HandleObject(EditorTargetCells cells)
    {
        if (HoveredPlacementId is { } id)
        {
            SelectPlacement(id);
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
            EditorObjectChoiceKind.Crate => EditorPlacementKind.SupplyCrate,
            EditorObjectChoiceKind.Mob => EditorPlacementKind.Mob,
            EditorObjectChoiceKind.Spawn => EditorPlacementKind.PlayerSpawn,
            EditorObjectChoiceKind.ConquestFlag => EditorPlacementKind.ConquestFlag,
            EditorObjectChoiceKind.Tree => EditorPlacementKind.Tree,
            _ => throw new InvalidOperationException(),
        };
        var placement = new EditorPlacement
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ArchetypeId = Settings.ObjectId,
            Cell = cells.Air,
            // A tree has no facing anybody authors, and a stand of them all pointing the same way
            // is the one thing that gives away that they are the same model. Rolled once here and
            // saved, so it stays put across reloads.
            Yaw = kind == EditorPlacementKind.Tree
                ? Random.Shared.NextSingle() * MathF.Tau
                : Settings.ObjectYaw,
            // Null lets the server allocate this NPC as one member of the default mixed squad.
            // `editor object equip` turns it into an explicit per-placement override.
            WeaponId = null,
            Team = kind is EditorPlacementKind.ConquestFlag or EditorPlacementKind.Tree
                ? 0
                : Settings.ObjectTeam,
        };
        Session.Execute(new AddPlacementCommand(placement));
        FeedbackRequested?.Invoke(
            $"Placed {kind.ToString().ToLowerInvariant()}; placement ID " +
            $"{EditorPlacementIds.Display(placement.Id)}");
    }

    /// <summary>
    /// A grove stroke, shaped exactly like a terrain stroke: the press decides whether it plants or
    /// clears, the drag records where it went, and the release resolves the lot as one action. Right
    /// drag is the eraser, the same way right drag inverts the terrain operation.
    /// </summary>
    private void HandleGrove(
        EditorTargetCells cells,
        bool left,
        bool right,
        bool leftPressed,
        bool rightPressed,
        bool leftReleased,
        bool rightReleased)
    {
        if (leftPressed || rightPressed)
        {
            groveDabs.Clear();
            groveErasing = rightPressed;
        }

        if (left || right)
        {
            if (!TargetIsValid) return;
            AddGroveDab(new System.Numerics.Vector2(cells.Air.X + 0.5f, cells.Air.Z + 0.5f));
        }

        if (leftReleased || rightReleased) CommitGrove();
    }

    /// <summary>
    /// Dabs are thinned to half a brush width apart, as terrain dabs are: a stroke is a path, not a
    /// frame rate, and recording one dab per frame would make a slow drag cost more than a fast one
    /// for a grove that comes out identical either way.
    /// </summary>
    private void AddGroveDab(System.Numerics.Vector2 point)
    {
        float spacing = MathF.Max(0.5f, Settings.TreeBrush.Radius * 0.5f);
        if (groveDabs.Count > 0
            && System.Numerics.Vector2.DistanceSquared(groveDabs[^1], point) < spacing * spacing)
            return;
        groveDabs.Add(point);
    }

    private void CommitGrove()
    {
        if (groveDabs.Count == 0) return;

        if (groveErasing)
        {
            var erased = TreeBrush.Erase(Session.Document, groveDabs, Settings.TreeBrush.Radius);
            if (erased.Count > 0)
            {
                if (erased.Any(placement => placement.Id == selectedPlacement))
                    selectedPlacement = null;
                Session.Execute(new DeletePlacementsCommand($"Erase {erased.Count} trees", erased));
                FeedbackRequested?.Invoke($"Erased {erased.Count} trees");
            }
        }
        else
        {
            var painted = TreeBrush.Paint(
                Session.Document, Session.Terrain, groveDabs, Settings.TreeBrush);
            if (painted.Count > 0)
            {
                Session.Execute(new AddPlacementsCommand($"Paint {painted.Count} trees", painted));
                FeedbackRequested?.Invoke($"Painted {painted.Count} trees");
            }
        }

        groveDabs.Clear();
    }

    /// <summary>
    /// Right click deletes the object under the cursor, and otherwise clears any selection — the
    /// same "cancel" it has always meant when pointed at nothing.
    /// </summary>
    private void HandleObjectRightClick()
    {
        if (HoveredPlacementId is { } id && Session.Placement(id) is { } placement)
        {
            DeletePlacement(placement);
            HoveredPlacementId = null;
            return;
        }
        selectedPlacement = null;
    }

    private void DeletePlacement(EditorPlacement placement)
    {
        Session.Execute(new DeletePlacementCommand(placement));
        if (selectedPlacement == placement.Id) selectedPlacement = null;
        FeedbackRequested?.Invoke(
            $"Deleted {placement.Kind.ToString().ToLowerInvariant()} placement " +
            EditorPlacementIds.Display(placement.Id));
    }

    /// <summary>The box a right click would delete, drawn from the same bounds picking tested.</summary>
    private void DrawHoveredPlacement()
    {
        if (HoveredPlacementId is not { } id || Session.Placement(id) is not { } placement) return;
        var (min, max) = EditorPlacementBounds.World(Session.Terrain, placement);
        WorldPreviewRenderer.Cube(min, max, new Color(255, 80, 80, 250));
    }

    private void HandleSelectionKeys()
    {
        bool delete = Input.IsKeyDown(Keys.Delete);
        if (delete && !deleteWasDown && selectedPlacement is { } id && Session.Placement(id) is { } placement)
            DeletePlacement(placement);
        deleteWasDown = delete;

        bool rotate = Input.IsKeyDown(Keys.R);
        if (rotate && !rotateWasDown)
        {
            if (Settings.Mode == EditorToolMode.Structure && Structures.Selected is not null)
                Structures.QuarterTurns = (Structures.QuarterTurns + 1) % 4;
            else if (selectedPlacement is { } selected && Session.Placement(selected) is { } current)
                Session.Execute(new UpdatePlacementCommand(
                    $"Rotate {current.Kind}", current, current with { Yaw = current.Yaw + MathF.PI / 2f }));
        }
        rotateWasDown = rotate;

        if (Input.IsKeyDown(Keys.Escape)) selectedPlacement = null;
    }

    private void HandleShortcuts()
    {
        bool ctrl = Input.IsKeyDown(Keys.LeftCtrl) || Input.IsKeyDown(Keys.RightCtrl);
        bool shift = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);

        bool undo = Input.IsKeyDown(Keys.U);
        if (undo && !undoWasDown) Session.Undo();
        undoWasDown = undo;

        bool redo = Input.IsKeyDown(Keys.Y);
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
        bool structures = IsModeKeyDown(Keys.D4, Keys.NumPad4);

        EditorToolMode? requested = terrain && !terrainModeWasDown
            ? EditorToolMode.Terrain
            : block && !blockModeWasDown
                ? EditorToolMode.Block
                : objects && !objectModeWasDown
                    ? EditorToolMode.Object
                    : structures && !structureModeWasDown
                        ? EditorToolMode.Structure
                        : null;

        terrainModeWasDown = terrain;
        blockModeWasDown = block;
        objectModeWasDown = objects;
        structureModeWasDown = structures;

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
        structureModeWasDown = IsModeKeyDown(Keys.D4, Keys.NumPad4);
    }

    private void CaptureDisabledInput()
    {
        HoveredPlacementId = null;
        strokeDabs.Clear();
        groveDabs.Clear();
        blockBefore.Clear();
        blockCells.Clear();
        leftWasDown = Input.IsMouseButtonDown(MouseButton.Left);
        rightWasDown = Input.IsMouseButtonDown(MouseButton.Right);
        deleteWasDown = Input.IsKeyDown(Keys.Delete);
        rotateWasDown = Input.IsKeyDown(Keys.R);
        undoWasDown = Input.IsKeyDown(Keys.U);
        redoWasDown = Input.IsKeyDown(Keys.Y);
        bool ctrl = Input.IsKeyDown(Keys.LeftCtrl) || Input.IsKeyDown(Keys.RightCtrl);
        bool shift = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);
        saveWasDown = ctrl && !shift && Input.IsKeyDown(Keys.S);
        bakeWasDown = ctrl && shift && Input.IsKeyDown(Keys.B);
        CaptureModeHotkeys();
    }

    private bool IsModeKeyDown(Keys numberRow, Keys numberPad)
        => Input.IsKeyDown(numberRow) || Input.IsKeyDown(numberPad);

    private void HandleWheel()
    {
        float wheel = Input.MouseWheelDelta;
        if (MathF.Abs(wheel) < 0.01f) return;

        if (Settings.Mode == EditorToolMode.Terrain)
        {
            bool shift = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);
            if (shift)
                Settings.TerrainStrength = Math.Clamp(
                    Settings.TerrainStrength + MathF.Sign(wheel) * 0.1f, 0.1f, 1f);
            else
                Settings.TerrainHalfExtent = System.Numerics.Vector3.Max(
                    new NVector3(0.5f),
                    Settings.TerrainHalfExtent + new NVector3(MathF.Sign(wheel) * 0.25f));
            return;
        }

        if (Settings.Mode == EditorToolMode.Block)
        {
            int delta = Math.Sign(wheel);
            var size = new EInt3(
                Math.Clamp(
                    Settings.BlockSize.X + delta, 1, EditorToolSettings.MaxBlockBrushDimension),
                Math.Clamp(
                    Settings.BlockSize.Y + delta, 1, EditorToolSettings.MaxBlockBrushDimension),
                Math.Clamp(
                    Settings.BlockSize.Z + delta, 1, EditorToolSettings.MaxBlockBrushDimension));
            if (BlockBrush.IsValidSize(size)) Settings.BlockSize = size;
        }
    }

    private void DrawPreview(
        TerrainHit hit,
        EditorTargetCells objectCells,
        EditorTargetCells blockSamples)
    {
        var targetColor = TargetIsValid
            ? new Color(255, 255, 255, 230)
            : new Color(255, 70, 70, 240);
        switch (Settings.Mode)
        {
            case EditorToolMode.Terrain:
                if (Settings.TerrainShape == EditShape.Sphere)
                    WorldPreviewRenderer.Sphere(hit.Point, Settings.TerrainHalfExtent.X, targetColor);
                else if (Settings.TerrainShape == EditShape.Organic)
                    WorldPreviewRenderer.Organic(
                        hit.Point, Settings.TerrainHalfExtent, targetColor);
                else
                    WorldPreviewRenderer.Cube(
                        hit.Point - Settings.TerrainHalfExtent,
                        hit.Point + Settings.TerrainHalfExtent, targetColor);
                break;
            case EditorToolMode.Structure:
                DrawStructurePreview(blockSamples, targetColor);
                break;
            case EditorToolMode.Block:
                {
                    var anchor = Input.IsMouseButtonDown(MouseButton.Right)
                        ? blockSamples.Solid
                        : blockSamples.Air;
                    var (min, max) = BlockBrush.Bounds(anchor, Settings.BlockSize);
                    WorldPreviewRenderer.Cube(
                        min.SamplePosition - new NVector3(0.5f),
                        max.SamplePosition + new NVector3(0.5f),
                        targetColor);
                }
                break;
            case EditorToolMode.Object:
                // A brush needs its footprint shown, not the one cell under the cursor: the radius
                // is the whole of what you are aiming.
                if (Settings.ObjectKind == EditorObjectChoiceKind.TreeBrush)
                    WorldPreviewRenderer.Sphere(
                        hit.Point,
                        Settings.TreeBrush.Radius,
                        Input.IsMouseButtonDown(MouseButton.Right)
                            ? new Color(255, 120, 90, 200)
                            : new Color(120, 230, 120, 200));
                else
                    WorldPreviewRenderer.Cell(
                        objectCells.Air,
                        TargetIsValid ? new Color(255, 220, 80, 230) : targetColor);
                break;
        }

        if (selectedPlacement is { } id && Session.Placement(id) is { } selected)
            WorldPreviewRenderer.Cell(selected.Cell, new Color(80, 220, 255, 240));
        foreach (var placement in Session.Document.Placements.Where(p => p.Kind == EditorPlacementKind.PlayerSpawn))
            WorldPreviewRenderer.Cell(
                placement.Cell,
                TeamColor(placement.Team, alpha: 220));
        foreach (var placement in Session.Document.Placements.Where(p => p.Kind == EditorPlacementKind.ConquestFlag))
            WorldPreviewRenderer.Cell(placement.Cell, new Color(220, 220, 220, 220));
    }

    /// <summary>The ghost of what is about to be placed. Nothing selected draws nothing: the mode
    /// has no other state to show.</summary>
    private void DrawStructurePreview(EditorTargetCells blockSamples, Color targetColor)
    {
        if (Structures.Selected is not { } structure) return;

        foreach (var block in structure.Blocks)
        {
            var offset = StructureLibrary.Transform(
                block.Offset, Structures.QuarterTurns, Structures.MirrorX);
            WorldPreviewRenderer.VoxelSample(new EInt3(
                blockSamples.Air.X + offset.X,
                blockSamples.Air.Y + offset.Y,
                blockSamples.Air.Z + offset.Z), targetColor);
        }
    }

    private static Color TeamColor(int team, byte alpha)
    {
        if (team <= 0) return new Color(220, 220, 220, alpha);
        uint hash = unchecked((uint)team * 2654435761u);
        return new Color(
            (byte)(80 + (hash & 0x7f)),
            (byte)(80 + ((hash >> 8) & 0x7f)),
            (byte)(80 + ((hash >> 16) & 0x7f)),
            alpha);
    }

    private bool IsTargetValid(
        TerrainHit hit,
        EditorTargetCells objectCells,
        EditorTargetCells blockSamples)
    {
        return Settings.Mode switch
        {
            EditorToolMode.Terrain => EditorValidation.IsBrushInBounds(
                Float3.From(hit.Point), Float3.From(Settings.TerrainHalfExtent)),
            EditorToolMode.Structure when Structures.Selected is { } structure =>
                structure.Blocks.All(block =>
                {
                    var offset = StructureLibrary.Transform(
                        block.Offset, Structures.QuarterTurns, Structures.MirrorX);
                    return EditorValidation.IsCellInBounds(new EInt3(
                        blockSamples.Air.X + offset.X,
                        blockSamples.Air.Y + offset.Y,
                        blockSamples.Air.Z + offset.Z));
                }),
            // Capturing: any cell you can point at is a corner you can set.
            EditorToolMode.Structure => EditorValidation.IsCellInBounds(blockSamples.Air),
            EditorToolMode.Block => IsBlockTargetValid(
                Input.IsMouseButtonDown(MouseButton.Right)
                    ? blockSamples.Solid
                    : blockSamples.Air),
            EditorToolMode.Object => IsObjectTargetValid(objectCells.Air),
            _ => false,
        };
    }

    private bool IsBlockTargetValid(EInt3 anchor)
    {
        var (min, max) = BlockBrush.Bounds(anchor, Settings.BlockSize);
        return EditorValidation.IsCellInBounds(min)
            && EditorValidation.IsCellInBounds(max);
    }

    private bool IsObjectTargetValid(EInt3 cell)
    {
        if (!EditorValidation.IsCellInBounds(cell)) return false;
        bool isSpawn = Settings.ObjectKind == EditorObjectChoiceKind.Spawn
            || selectedPlacement is { } id
                && Session.Placement(id)?.Kind == EditorPlacementKind.PlayerSpawn;
        if (!isSpawn) return true;

        var feet = EditorPlacementPosition.ResolvePlayerFeet(
            Session.Terrain,
            EditorPlacementPosition.Resolve(Session.Terrain, cell));
        return TerrainCollision.TryDeepestContact(
                Session.Terrain, PlayerMovement.Body, feet, out var contact)
            && contact.Distance >= PlayerMovement.Body.Radius;
    }

    private void CancelGestures()
    {
        HoveredPlacementId = null;
        strokeDabs.Clear();
        groveDabs.Clear();
        blockBefore.Clear();
        blockCells.Clear();
        leftWasDown = rightWasDown = false;
    }
}
