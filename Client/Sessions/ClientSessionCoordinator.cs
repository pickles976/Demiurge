using Demiurge.Editor;
using Demiurge.GameServer;
using Stride.Engine;
using Stride.Games;

namespace Demiurge;

public sealed class ClientSessionCoordinator : ITerminalCommandDispatcher, IDisposable
{
    private readonly Game game;
    private readonly ClientInputState inputState;
    private readonly MapRepository maps;
    private readonly Queue<SessionRequest> transitions = new();

    private Scene scene = null!;
    private IClientSession? current;
    private DateTime lastEditUtc;
    private DateTime lastAutosaveUtc;
    private EditorPlaytestState? playtestEditor;

    public event Action<TerminalOutput>? OutputReceived;

    public ClientSessionCoordinator(Game game, ClientInputState inputState, string? mapRoot = null)
    {
        this.game = game;
        this.inputState = inputState;
        maps = new MapRepository(mapRoot);
    }

    public void Start(Scene scene, SessionRequest initial)
    {
        this.scene = scene;
        transitions.Enqueue(initial);
        ApplyTransition();
    }

    public void Update(GameTime time)
    {
        current?.Update(time);
        ApplyTransition();
        Autosave();
    }

    public TerminalOutput? Execute(string commandLine)
    {
        string normalized = commandLine.StartsWith('/') ? commandLine[1..] : commandLine;
        string[] tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return TerminalOutputFor(false, "Enter a command");

        try
        {
            if (tokens[0].Equals("session", StringComparison.OrdinalIgnoreCase))
                return ExecuteSession(tokens);
            if (tokens[0].Equals("map", StringComparison.OrdinalIgnoreCase))
                return ExecuteMap(tokens);
            if (tokens[0].Equals("editor", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not EditorClientSession editor)
                    return TerminalOutputFor(false, "Editor commands require an editor session");
                if (editor.IsPlaytesting)
                    return TerminalOutputFor(false, "Return to the editor before changing editor state");
                if (tokens.Length > 1 && tokens[1].Equals("structure", StringComparison.OrdinalIgnoreCase))
                    return ExecuteStructure(tokens, editor);
                if (tokens.Length > 2
                    && tokens[1].Equals("object", StringComparison.OrdinalIgnoreCase)
                    && (tokens[2].ToLowerInvariant() is "list" or "select" or "equip"
                        || tokens[2].Equals("set-team", StringComparison.OrdinalIgnoreCase)))
                    return ExecuteEditorObject(tokens, editor);
                var result = EditorCommandParser.Execute(normalized, editor.Settings, editor.Editor);
                return TerminalOutputFor(result.Success, result.Output);
            }

            // A view-only overlay toggle, so it is answered here rather than sent to the server. It is
            // also accepted with no session attached: the flag simply applies to the next one.
            if (tokens[0].Equals("ai", StringComparison.OrdinalIgnoreCase)
                && tokens.Length > 1
                && tokens[1].Equals("track", StringComparison.OrdinalIgnoreCase))
                return ExecuteAiTrack(tokens);

            if (current is RuntimeClientSession runtime)
            {
                runtime.Network.SendCommand(commandLine);
                return null;
            }
            if (current is EditorClientSession embedded
                && embedded.PlaytestNetwork is { } playtestNetwork)
            {
                playtestNetwork.SendCommand(commandLine);
                return null;
            }

            return TerminalOutputFor(false, $"Unknown command: {tokens[0]}");
        }
        catch (Exception ex)
        {
            return TerminalOutputFor(false, ex.Message);
        }
    }

    public IReadOnlyList<string> Help(string? topic = null)
    {
        string[] topics = (topic ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (topics.Length > 0) return DetailedHelp(topics);

        var lines = new List<string>
        {
            "session <status|editor|host|join|playtest|playtest-networked> ...",
            "map <list|new|status|save|save-as|load|recover|discard-autosave|validate|bake> ...",
        };
        if (current is EditorClientSession editor)
        {
            if (editor.IsPlaytesting)
            {
                lines.Add("spawn mob [x z]");
                lines.Add("spawn pickup <item> [x z]");
                lines.Add("equip <@s|@actor-id> <item>");
                lines.Add("ai stats");
                lines.Add("ai track <off|on|beacons|facing|clustering>");
            }
            else
            {
                lines.Add("editor <status|mode|terrain|block|object|rotate|undo|redo> ...");
            }
        }
        else
        {
            lines.Add("spawn mob [x z]");
            lines.Add("spawn pickup <item> [x z]");
            lines.Add("equip <@s|@actor-id> <item>");
            lines.Add("ai stats");
            lines.Add("ai track <off|on|beacons|facing|clustering>");
        }
        lines.Add("Type 'help <command>' for details. Press Tab to complete names and IDs.");
        return lines;
    }

    public IReadOnlyList<string> Complete(string commandLine)
    {
        string normalized = commandLine.StartsWith('/') ? commandLine[1..] : commandLine;
        bool startsNewToken = normalized.Length > 0 && char.IsWhiteSpace(normalized[^1]);
        string[] tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int tokenIndex = startsNewToken ? tokens.Length : Math.Max(0, tokens.Length - 1);
        string prefix = startsNewToken || tokens.Length == 0 ? string.Empty : tokens[^1];

        IEnumerable<string> candidates = CompletionCandidates(tokens, tokenIndex);
        return candidates
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose()
    {
        if (current is EditorClientSession editor && editor.Editor.Dirty)
        {
            try
            {
                maps.SaveAutosave(editor.Editor.Document);
                Console.WriteLine(
                    $"[Editor] Unsaved changes preserved in {maps.Paths.AutosavePath(editor.Editor.Document.Name)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Editor] Failed to preserve unsaved changes: {ex.Message}");
            }
        }
        DetachCurrent();
        current?.Dispose();
        current = null;
    }

    private IEnumerable<string> CompletionCandidates(string[] tokens, int tokenIndex)
    {
        if (tokenIndex == 0)
            return current is EditorClientSession activeEditor
                ? activeEditor.IsPlaytesting
                    ? ["spawn", "equip", "ai", "map", "session", "clear", "help"]
                    : ["editor", "map", "session", "clear", "help"]
                : ["spawn", "equip", "ai", "map", "session", "clear", "help"];

        if (tokens.Length == 0) return [];
        string root = tokens[0].ToLowerInvariant();

        if (root == "editor" && current is EditorClientSession editor)
        {
            if (tokenIndex == 1)
                return ["status", "mode", "terrain", "block", "object", "rotate", "undo", "redo", "structure"];
            if (tokenIndex == 2 && tokens.Length > 1)
                return tokens[1].ToLowerInvariant() switch
                {
                    "mode" => ["terrain", "block", "object"],
                    "terrain" => ["operation", "shape", "size", "strength", "material"],
                    "block" => BlockCatalog.All.Select(definition => definition.Id)
                        .Concat(["size"]),
                    "object" => ["pickup", "mob", "spawn", "flag", "team", "clear", "list", "select", "equip", "set-team"],
                    _ => [],
                };
            if (tokenIndex == 3 && tokens.Length > 2)
            {
                string branch = $"{tokens[1].ToLowerInvariant()} {tokens[2].ToLowerInvariant()}";
                return branch switch
                {
                    "terrain operation" => ["add", "subtract"],
                    "terrain shape" => ["sphere", "box", "organic"],
                    "terrain material" => BlockCatalog.All.Select(definition => definition.Id),
                    "object pickup" => ItemCatalog.All.Select(definition => definition.Id),
                    "object select" => editor.Editor.Document.Placements
                        .Select(placement => EditorPlacementIds.Display(placement.Id)),
                    "object equip" => editor.Editor.Document.Placements
                        .Where(placement => placement.Kind == EditorPlacementKind.Mob)
                        .Select(placement => EditorPlacementIds.Display(placement.Id))
                        .Prepend("selected"),
                    "object set-team" => editor.Editor.Document.Placements
                        .Where(placement => placement.Kind is EditorPlacementKind.Mob or EditorPlacementKind.PlayerSpawn)
                        .Select(placement => EditorPlacementIds.Display(placement.Id))
                        .Prepend("selected"),
                    _ => [],
                };
            }
            if (tokenIndex == 4 && tokens.Length > 3
                && tokens[1].Equals("object", StringComparison.OrdinalIgnoreCase)
                && tokens[2].Equals("equip", StringComparison.OrdinalIgnoreCase))
                return ItemCatalog.All
                    .Where(definition => WeaponConfig.Get(definition.Type) is not null)
                    .Select(definition => definition.Id);
        }

        if (root == "spawn")
        {
            if (tokenIndex == 1) return ["mob", "pickup"];
            if (tokenIndex == 2 && tokens.Length > 1
                && tokens[1].Equals("pickup", StringComparison.OrdinalIgnoreCase))
                return ItemCatalog.All.Select(definition => definition.Id);
        }
        if (root == "equip" && tokenIndex == 2)
            return ItemCatalog.All.Select(definition => definition.Id);
        if (root == "ai")
        {
            if (tokenIndex == 1) return ["stats", "track"];
            if (tokenIndex >= 2 && tokens.Length > 1
                && tokens[1].Equals("track", StringComparison.OrdinalIgnoreCase))
                return ["off", "on", "beacons", "facing", "clustering"];
        }
        if (root == "session" && tokenIndex == 1)
            return ["status", "editor", "host", "join", "playtest", "playtest-networked"];
        if (root == "help")
        {
            if (tokenIndex == 1)
                return ["editor", "terrain", "block", "object", "map", "session", "spawn", "equip", "ai"];
            if (tokenIndex == 2 && tokens.Length > 1
                && tokens[1].Equals("editor", StringComparison.OrdinalIgnoreCase))
                return ["terrain", "block", "object"];
        }

        return [];
    }

    private IReadOnlyList<string> DetailedHelp(string[] topics)
    {
        string root = topics[0].ToLowerInvariant();
        if (root == "editor" && topics.Length > 1) root = topics[1].ToLowerInvariant();

        return root switch
        {
            "editor" =>
            [
                "Editor commands:",
                "  editor mode <terrain|block|object>",
                "  editor terrain <operation|shape|size|strength|material> ...",
                "  editor block <block-id|size> ...",
                "  editor object <pickup|mob|spawn|flag|team|clear|list|select|equip|set-team> ...",
                "  editor undo | editor redo",
                "Use 'help terrain', 'help block', or 'help object' for details.",
            ],
            "terrain" =>
            [
                "Terrain brush:",
                "  editor terrain operation <add|subtract>",
                "  editor terrain shape <sphere|box|organic>",
                "  editor terrain size <uniform|x y z>",
                "  editor terrain strength <0..1>",
                "  editor terrain material <block-id>",
                "Mouse wheel changes size; Shift+wheel changes strength.",
            ],
            "block" =>
            [
                "Block brush:",
                "  editor block <block-id>",
                "  editor block size <uniform|x y z>",
                $"Blocks: {string.Join(", ", BlockCatalog.All.Select(definition => definition.Id))}",
                "Mouse wheel changes all three dimensions.",
            ],
            "object" =>
            [
                "Object placement:",
                "  editor object pickup <item-id>",
                "  editor object mob",
                "  editor object spawn [spawn-id]",
                "  editor object flag",
                "  editor object team <positive-integer>",
                "  editor object clear",
                "  editor object list",
                "  editor object select <placement-id>",
                "  editor object equip <placement-id|selected> <weapon-id>",
                "  editor object set-team <placement-id|selected> <positive-integer>",
                $"Items: {string.Join(", ", ItemCatalog.All.Select(definition => definition.Id))}",
                "Examples: editor object pickup demiurge:ak47 | editor object mob",
                "Left click places the selected archetype and reports its stable placement ID.",
            ],
            "session" =>
            [
                "Session commands:",
                "  session status",
                "  session editor <map-name>",
                "  session host <map-name> [--build]",
                "  session join <host>",
                "  session playtest",
                "  session playtest-networked",
                "F4 toggles the fast authoritative playtest inside the editor.",
                "playtest-networked performs the full save/load/stream/remesh validation path.",
            ],
            "map" =>
            [
                "Map commands:",
                "  map <list|new|status|save|save-as|load>",
                "  map <recover|discard-autosave|validate|bake>",
            ],
            "spawn" =>
            [
                "Runtime spawning:",
                "  spawn mob [x z]",
                "  spawn pickup <item-id> [x z]",
                "Successful spawns report a mob actor ID (@id) or pickup object ID (#id).",
            ],
            "equip" =>
            [
                "Runtime equipment:",
                "  equip <@s|@actor-id> <item-id>",
                $"Items: {string.Join(", ", ItemCatalog.All.Select(definition => definition.Id))}",
                "Runtime equipment changes last for the current session only; map save does not record them.",
            ],
            "ai" =>
            [
                "AI diagnostics:",
                "  ai stats",
                "  ai track [off|on|beacons|facing|clustering]",
                "Shows the latest 1-second average for mob movement and off-thread path searches.",
                "track draws a debug overlay over every NPC; layers combine, and no argument reports",
                "the current state. beacons are vertical beams visible through terrain, facing adds a",
                "ground ring and heading spoke, clustering links NPCs within 4 m of each other.",
                "The overlay is client-side only and never reaches the server.",
            ],
            _ => [$"No help topic named '{topics[0]}'. Type 'help' to list commands."],
        };
    }

    private TerminalOutput ExecuteAiTrack(string[] tokens)
    {
        if (tokens.Length == 2)
            return TerminalOutputFor(
                true,
                $"NPC tracking: {DescribeTrackerLayers(NpcTracker.Layers)}");

        var layers = NpcTrackerLayers.None;
        for (int i = 2; i < tokens.Length; i++)
            switch (tokens[i].ToLowerInvariant())
            {
                case "off" or "none":
                    break;
                case "on" or "all":
                    layers |= NpcTrackerLayers.All;
                    break;
                case "beacons":
                    layers |= NpcTrackerLayers.Beacons;
                    break;
                case "facing":
                    layers |= NpcTrackerLayers.Facing;
                    break;
                case "clustering":
                    layers |= NpcTrackerLayers.Clustering;
                    break;
                default:
                    return TerminalOutputFor(
                        false,
                        $"Unknown tracking layer: {tokens[i]}. "
                        + "Use off, on, beacons, facing, or clustering");
            }

        NpcTracker.Layers = layers;
        return TerminalOutputFor(true, $"NPC tracking: {DescribeTrackerLayers(layers)}");
    }

    private static string DescribeTrackerLayers(NpcTrackerLayers layers)
        => layers == NpcTrackerLayers.None ? "off" : layers.ToString().ToLowerInvariant();

    private TerminalOutput ExecuteSession(string[] tokens)
    {
        if (tokens.Length < 2)
            return TerminalOutputFor(
                false,
                "Usage: session <status|editor|host|join|playtest|playtest-networked> ...");

        switch (tokens[1].ToLowerInvariant())
        {
            case "status":
                return TerminalOutputFor(true, current switch
                {
                    EditorClientSession editor =>
                        $"editor map={editor.Editor.Document.Name} dirty={editor.Editor.Dirty}" +
                        (editor.IsPlaytesting ? " mode=playtest" : string.Empty),
                    RuntimeClientSession => "runtime",
                    _ => "idle",
                });

            case "editor":
                if (tokens.Length != 3) return TerminalOutputFor(false, "Usage: session editor <map-name>");
                if (!CanLeaveEditor(discard: false, out var error)) return TerminalOutputFor(false, error!);
                playtestEditor = null;
                transitions.Enqueue(SessionRequest.EditorMap(tokens[2]));
                return TerminalOutputFor(true, $"Switching to editor map {tokens[2]}");

            case "playtest":
                if (tokens.Length != 2)
                    return TerminalOutputFor(false, "Usage: session playtest");
                if (current is EditorClientSession playtestSource)
                {
                    if (playtestSource.IsPlaytesting)
                    {
                        if (playtestSource.PlaytestNetwork is { } stoppingNetwork)
                            stoppingNetwork.CommandResultReceived -= OnNetworkCommandResult;
                        playtestSource.StopPlaytest();
                        return TerminalOutputFor(true, "Returned to editor");
                    }

                    var runtimeMap = EditorTerrainEvaluator.Bake(
                        playtestSource.Editor.Document,
                        playtestSource.Editor.Terrain);
                    playtestSource.StartPlaytest(runtimeMap);
                    playtestSource.PlaytestNetwork!.CommandResultReceived += OnNetworkCommandResult;
                    return TerminalOutputFor(
                        true,
                        $"Playtesting {playtestSource.Editor.Document.Name} in editor; press F4 to return");
                }
                if (current is RuntimeClientSession && playtestEditor is { } compatibilityResume)
                {
                    transitions.Enqueue(SessionRequest.ResumeEditor(
                        compatibilityResume.Editor,
                        compatibilityResume.Settings,
                        compatibilityResume.Structures));
                    return TerminalOutputFor(true, "Returning to editor");
                }
                return TerminalOutputFor(false, "Fast playtest requires an editor session");

            case "playtest-networked":
                if (tokens.Length != 2)
                    return TerminalOutputFor(false, "Usage: session playtest-networked");
                if (current is EditorClientSession networkedSource)
                {
                    if (networkedSource.IsPlaytesting)
                    {
                        if (networkedSource.PlaytestNetwork is { } embeddedNetwork)
                            embeddedNetwork.CommandResultReceived -= OnNetworkCommandResult;
                        networkedSource.StopPlaytest();
                    }
                    SaveAndBake(networkedSource);
                    playtestEditor = new EditorPlaytestState(
                        networkedSource.Editor,
                        networkedSource.Settings,
                        networkedSource.Structures);
                    string name = networkedSource.Editor.Document.Name;
                    transitions.Enqueue(SessionRequest.HostMap(
                        name, maps.Paths.RuntimePath(name), networkedSource.PlaytestSpawn));
                    return TerminalOutputFor(
                        true,
                        $"Starting full networked playtest for {name}");
                }
                if (current is RuntimeClientSession && playtestEditor is { } resume)
                {
                    transitions.Enqueue(SessionRequest.ResumeEditor(
                        resume.Editor, resume.Settings, resume.Structures));
                    return TerminalOutputFor(true, "Returning to editor");
                }
                return TerminalOutputFor(
                    false,
                    "Networked playtest requires an editor or a runtime started by playtest-networked");

            case "host":
            {
                if (tokens.Length is < 3 or > 4)
                    return TerminalOutputFor(false, "Usage: session host <map-name> [--build]");
                bool build = tokens.Length == 4 && tokens[3].Equals("--build", StringComparison.OrdinalIgnoreCase);
                if (tokens.Length == 4 && !build) return TerminalOutputFor(false, "Unknown host option");

                if (build)
                {
                    if (current is EditorClientSession editor
                        && editor.Editor.Document.Name.Equals(tokens[2], StringComparison.OrdinalIgnoreCase))
                    {
                        SaveAndBake(editor);
                    }
                    else
                    {
                        if (!CanLeaveEditor(discard: false, out var buildError))
                            return TerminalOutputFor(false, buildError!);
                        var document = maps.Load(tokens[2]);
                        maps.Save(document);
                        SaveRuntime(document);
                    }
                }
                else
                {
                    if (!CanLeaveEditor(discard: false, out var hostError))
                        return TerminalOutputFor(false, hostError!);
                    EnsureCurrentRuntimeBake(tokens[2]);
                }

                playtestEditor = null;
                transitions.Enqueue(SessionRequest.HostMap(tokens[2], maps.Paths.RuntimePath(tokens[2])));
                return TerminalOutputFor(true, $"Hosting runtime map {tokens[2]}");
            }

            case "join":
                if (tokens.Length != 3) return TerminalOutputFor(false, "Usage: session join <host>");
                if (!CanLeaveEditor(discard: false, out var joinError))
                    return TerminalOutputFor(false, joinError!);
                playtestEditor = null;
                transitions.Enqueue(SessionRequest.Join(tokens[2]));
                return TerminalOutputFor(true, $"Joining {tokens[2]}");

            default:
                return TerminalOutputFor(false, $"Unknown session command: {tokens[1]}");
        }
    }

    private TerminalOutput ExecuteMap(string[] tokens)
    {
        if (tokens.Length < 2)
            return TerminalOutputFor(false, "Usage: map <list|new|status|save|save-as|load|recover|discard-autosave|validate|bake> ...");

        switch (tokens[1].ToLowerInvariant())
        {
            case "list":
                var names = maps.List();
                return TerminalOutputFor(true, names.Count == 0 ? "No maps" : string.Join(", ", names));

            case "new":
                if (tokens.Length != 3) return TerminalOutputFor(false, "Usage: map new <map-name>");
                if (!CanLeaveEditor(discard: false, out var newError)) return TerminalOutputFor(false, newError!);
                transitions.Enqueue(SessionRequest.EditorDocument(maps.New(tokens[2])));
                return TerminalOutputFor(true, $"Created map {tokens[2]}");

            case "status":
                if (current is not EditorClientSession statusEditor)
                    return TerminalOutputFor(false, "Map status requires an editor session");
                string sourceHash = Convert.ToHexStringLower(SourceMapSerializer.Hash(statusEditor.Editor.Document));
                return TerminalOutputFor(true,
                    $"map={statusEditor.Editor.Document.Name} dirty={statusEditor.Editor.Dirty} source={sourceHash[..12]} bakeCurrent={maps.RuntimeIsCurrent(statusEditor.Editor.Document)}");

            case "save":
                if (current is not EditorClientSession saveEditor)
                    return TerminalOutputFor(false, "Map save requires an editor session");
                maps.Save(saveEditor.Editor.Document);
                saveEditor.Editor.MarkSaved();
                return TerminalOutputFor(true, $"Saved {saveEditor.Editor.Document.Name}");

            case "save-as":
                if (tokens.Length != 3) return TerminalOutputFor(false, "Usage: map save-as <map-name>");
                if (current is not EditorClientSession saveAsEditor)
                    return TerminalOutputFor(false, "Map save-as requires an editor session");
                var copy = saveAsEditor.Editor.Document with
                {
                    Name = MapPathResolver.ValidateName(tokens[2]),
                    MapId = Guid.NewGuid(),
                };
                maps.Save(copy);
                transitions.Enqueue(SessionRequest.EditorDocument(copy));
                return TerminalOutputFor(true, $"Saved as {tokens[2]}");

            case "load":
            {
                if (tokens.Length is < 3 or > 4)
                    return TerminalOutputFor(false, "Usage: map load <map-name> [--discard]");
                bool discard = tokens.Length == 4 && tokens[3].Equals("--discard", StringComparison.OrdinalIgnoreCase);
                if (tokens.Length == 4 && !discard) return TerminalOutputFor(false, "Unknown load option");
                if (!CanLeaveEditor(discard, out var loadError)) return TerminalOutputFor(false, loadError!);
                transitions.Enqueue(SessionRequest.EditorMap(tokens[2]));
                return TerminalOutputFor(true, $"Loading source map {tokens[2]}");
            }

            case "recover":
            {
                if (tokens.Length != 2) return TerminalOutputFor(false, "Usage: map recover");
                if (current is not EditorClientSession recoverEditor)
                    return TerminalOutputFor(false, "Map recovery requires an editor session");
                if (recoverEditor.Editor.Dirty)
                    return TerminalOutputFor(false, "Save or discard current edits before recovering an autosave");
                string name = recoverEditor.Editor.Document.Name;
                if (!maps.HasAutosave(name))
                    return TerminalOutputFor(false, $"Map {name} has no autosave");
                var recovered = maps.LoadAutosave(name);
                if (recovered.MapId != recoverEditor.Editor.Document.MapId)
                    return TerminalOutputFor(false, "Autosave belongs to a different map ID");
                maps.Save(recovered);
                maps.DeleteAutosave(name);
                transitions.Enqueue(SessionRequest.EditorDocument(recovered));
                return TerminalOutputFor(true, $"Recovered and saved autosave for {name}");
            }

            case "discard-autosave":
                if (tokens.Length != 2) return TerminalOutputFor(false, "Usage: map discard-autosave");
                if (current is not EditorClientSession discardEditor)
                    return TerminalOutputFor(false, "Autosave discard requires an editor session");
                maps.DeleteAutosave(discardEditor.Editor.Document.Name);
                return TerminalOutputFor(true, $"Discarded autosave for {discardEditor.Editor.Document.Name}");

            case "validate":
                if (current is not EditorClientSession validateEditor)
                    return TerminalOutputFor(false, "Map validation requires an editor session");
                var validation = EditorValidation.Validate(
                    validateEditor.Editor.Document,
                    validateEditor.Editor.Terrain);
                return TerminalOutputFor(validation.IsValid,
                    validation.IsValid
                        ? $"Valid ({validation.Warnings.Count} warning(s)): {string.Join("; ", validation.Warnings)}"
                        : string.Join("; ", validation.Errors));

            case "bake":
                if (current is not EditorClientSession bakeEditor)
                    return TerminalOutputFor(false, "Map bake requires an editor session");
                byte[] contentHash = SaveAndBake(bakeEditor);
                return TerminalOutputFor(
                    true,
                    $"Baked {bakeEditor.Editor.Document.Name} hash={Convert.ToHexStringLower(contentHash)}");

            default:
                return TerminalOutputFor(false, $"Unknown map command: {tokens[1]}");
        }
    }

    private TerminalOutput ExecuteStructure(string[] tokens, EditorClientSession editor)
    {
        if (tokens.Length < 3)
            return TerminalOutputFor(false, "Usage: editor structure <corner|pivot|save|select|rotate|mirror|clear> ...");

        var state = editor.Structures;
        switch (tokens[2].ToLowerInvariant())
        {
            case "corner":
                if (tokens.Length != 4 || tokens[3] is not ("1" or "2"))
                    return TerminalOutputFor(false, "Usage: editor structure corner <1|2>");
                if (editor.Controller.TargetCell is not { } corner)
                    return TerminalOutputFor(false, "No highlighted cell");
                if (tokens[3] == "1") state.Corner1 = corner;
                else state.Corner2 = corner;
                return TerminalOutputFor(true, $"Structure corner {tokens[3]}: {corner}");

            case "pivot":
                if (editor.Controller.TargetCell is not { } pivot)
                    return TerminalOutputFor(false, "No highlighted cell");
                state.Pivot = pivot;
                return TerminalOutputFor(true, $"Structure pivot: {pivot}");

            case "save":
            {
                if (tokens.Length != 4) return TerminalOutputFor(false, "Usage: editor structure save <name>");
                if (state.Corner1 is not { } first || state.Corner2 is not { } second)
                    return TerminalOutputFor(false, "Set structure corners 1 and 2 first");
                Int3 structurePivot = state.Pivot ?? first;
                var structure = StructureLibrary.Capture(
                    tokens[3], first, second, structurePivot, editor.Editor.Document.Blocks);
                string path = StructurePath(editor.Editor.Document.Name, tokens[3]);
                StructureLibrary.Save(path, structure);
                return TerminalOutputFor(true, $"Saved structure {tokens[3]} ({structure.Blocks.Count} blocks)");
            }

            case "select":
                if (tokens.Length != 4) return TerminalOutputFor(false, "Usage: editor structure select <name>");
                state.Selected = StructureLibrary.Load(StructurePath(editor.Editor.Document.Name, tokens[3]));
                state.QuarterTurns = 0;
                state.MirrorX = false;
                editor.Settings.Mode = EditorToolMode.Block;
                return TerminalOutputFor(true, $"Selected structure {tokens[3]}");

            case "rotate":
                if (tokens.Length != 4 || !int.TryParse(tokens[3], out int degrees) || degrees % 90 != 0)
                    return TerminalOutputFor(false, "Usage: editor structure rotate <multiple-of-90>");
                state.QuarterTurns = (state.QuarterTurns + degrees / 90) % 4;
                return TerminalOutputFor(true, $"Structure rotation: {state.QuarterTurns * 90} degrees");

            case "mirror":
                if (tokens.Length != 4 || !tokens[3].Equals("x", StringComparison.OrdinalIgnoreCase))
                    return TerminalOutputFor(false, "Usage: editor structure mirror x");
                state.MirrorX = !state.MirrorX;
                return TerminalOutputFor(true, $"Structure mirror X: {state.MirrorX}");

            case "clear":
                state.Selected = null;
                state.Corner1 = state.Corner2 = state.Pivot = null;
                return TerminalOutputFor(true, "Cleared structure selection");

            default:
                return TerminalOutputFor(false, $"Unknown structure command: {tokens[2]}");
        }
    }

    private TerminalOutput ExecuteEditorObject(string[] tokens, EditorClientSession editor)
    {
        switch (tokens[2].ToLowerInvariant())
        {
            case "list":
                if (tokens.Length != 3)
                    return TerminalOutputFor(false, "Usage: editor object list");
                if (editor.Editor.Document.Placements.Count == 0)
                    return TerminalOutputFor(true, "No editor placements");
                return TerminalOutputFor(
                    true,
                    string.Join(
                        "\n",
                        editor.Editor.Document.Placements
                            .OrderBy(placement => placement.Kind)
                            .ThenBy(placement => placement.Id)
                            .Select(FormatEditorPlacement)));

            case "select":
                if (tokens.Length != 4)
                    return TerminalOutputFor(false, "Usage: editor object select <placement-id>");
                var selected = ResolveEditorPlacement(editor, tokens[3]);
                editor.Controller.SelectPlacement(selected.Id, announce: false);
                return TerminalOutputFor(
                    true,
                    $"Selected {selected.Kind.ToString().ToLowerInvariant()} placement " +
                    EditorPlacementIds.Display(selected.Id));

            case "equip":
                if (tokens.Length != 5)
                    return TerminalOutputFor(
                        false,
                        "Usage: editor object equip <placement-id|selected> <weapon-id>");
                var mob = ResolveEditorPlacement(editor, tokens[3]);
                if (mob.Kind != EditorPlacementKind.Mob)
                    return TerminalOutputFor(false, $"Placement {EditorPlacementIds.Display(mob.Id)} is not a mob");
                if (!ItemCatalog.TryResolve(tokens[4], out var weapon)
                    || WeaponConfig.Get(weapon) is null)
                    return TerminalOutputFor(false, $"Unknown weapon: {tokens[4]}");
                string weaponId = ItemCatalog.Id(weapon);
                editor.Editor.Execute(new UpdatePlacementCommand(
                    $"Equip mob {EditorPlacementIds.Display(mob.Id)}",
                    mob,
                    mob with { WeaponId = weaponId }));
                return TerminalOutputFor(
                    true,
                    $"Equipped mob placement {EditorPlacementIds.Display(mob.Id)} with {weaponId}");

            case "set-team":
                if (tokens.Length != 5
                    || !int.TryParse(tokens[4], out int team)
                    || team <= 0)
                    return TerminalOutputFor(
                        false,
                        "Usage: editor object set-team <placement-id|selected> <positive-integer>");
                var actor = ResolveEditorPlacement(editor, tokens[3]);
                if (actor.Kind is not (EditorPlacementKind.Mob or EditorPlacementKind.PlayerSpawn))
                    return TerminalOutputFor(
                        false,
                        $"Placement {EditorPlacementIds.Display(actor.Id)} cannot have a playable team");
                editor.Editor.Execute(new UpdatePlacementCommand(
                    $"Set team for {EditorPlacementIds.Display(actor.Id)}",
                    actor,
                    actor with { Team = team }));
                return TerminalOutputFor(
                    true,
                    $"Set placement {EditorPlacementIds.Display(actor.Id)} to team {team}");

            default:
                return TerminalOutputFor(false, $"Unknown editor object command: {tokens[2]}");
        }
    }

    private static EditorPlacement ResolveEditorPlacement(EditorClientSession editor, string value)
    {
        if (value.Equals("selected", StringComparison.OrdinalIgnoreCase))
        {
            if (editor.Controller.SelectedPlacementId is not { } id
                || editor.Editor.Placement(id) is not { } selected)
                throw new ArgumentException("No editor placement is selected");
            return selected;
        }
        return EditorPlacementIds.Resolve(editor.Editor.Document, value);
    }

    private static string FormatEditorPlacement(EditorPlacement placement)
    {
        string details = placement.Kind switch
        {
            EditorPlacementKind.Mob =>
                $" weapon={placement.WeaponId ?? ItemCatalog.Id(ItemConfig.DefaultPrimaryWeapon)} team={placement.Team}",
            EditorPlacementKind.PlayerSpawn => $" team={placement.Team}",
            EditorPlacementKind.Flag => " neutral",
            _ => string.Empty,
        };
        return $"{EditorPlacementIds.Display(placement.Id)} " +
               $"{placement.Kind.ToString().ToLowerInvariant()} {placement.ArchetypeId} " +
               $"cell={placement.Cell}{details}";
    }

    private void ApplyTransition()
    {
        if (transitions.Count == 0) return;
        var request = transitions.Dequeue();
        IClientSession? candidate = null;

        try
        {
            DetachCurrent();
            current?.Dispose();
            current = null;
            candidate = request.Kind switch
            {
                SessionRequestKind.Editor => new EditorClientSession(
                    game,
                    inputState,
                    request.EditorSession
                        ?? new EditorSession(
                            request.Document
                            ?? (maps.HasSource(request.MapName!)
                                ? maps.Load(request.MapName!)
                                : maps.New(request.MapName!))),
                    request.EditorSettings ?? new EditorToolSettings(),
                    request.EditorStructures ?? new EditorStructureState()),
                SessionRequestKind.Host => new RuntimeClientSession(
                    game, inputState, NetworkConfig.ServerHost,
                    new ServerOptions
                    {
                        AllowCheats = true,
                        MapPath = request.RuntimeMapPath
                            ?? PrepareRuntimeFromSource(request.MapName!),
                        SpawnOverride = request.SpawnOverride,
                        InitialPlayerTeam = request.InitialPlayerTeam,
                        InitialNpcsPerTeam = request.InitialNpcsPerTeam,
                    }),
                SessionRequestKind.GeneratedHost => new RuntimeClientSession(
                    game, inputState, NetworkConfig.ServerHost,
                    new ServerOptions { AllowCheats = true }),
                SessionRequestKind.Join => new RuntimeClientSession(game, inputState, request.Host),
                _ => throw new InvalidOperationException($"Unknown session request {request.Kind}"),
            };
            candidate.Start(scene);
            current = candidate;
            if (request.EditorSession is not null) playtestEditor = null;
            AttachCurrent();
            OutputReceived?.Invoke(new TerminalOutput(true, $"Session active: {current.Kind}"));
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            current = null;
            OutputReceived?.Invoke(new TerminalOutput(false, $"Session transition failed: {ex.Message}"));
            if (request.Kind == SessionRequestKind.Host && playtestEditor is { } resume)
            {
                transitions.Enqueue(SessionRequest.ResumeEditor(
                    resume.Editor, resume.Settings, resume.Structures));
                OutputReceived?.Invoke(new TerminalOutput(true, "Returning to editor after failed playtest start"));
            }
        }
    }

    private void AttachCurrent()
    {
        if (current is RuntimeClientSession runtime)
            runtime.Network.CommandResultReceived += OnNetworkCommandResult;
        if (current is EditorClientSession editor)
        {
            editor.Editor.Changed += OnEditorChanged;
            editor.SaveRequested += OnEditorSaveRequested;
            editor.BakeRequested += OnEditorBakeRequested;
            editor.FeedbackRequested += OnEditorFeedbackRequested;
            lastEditUtc = DateTime.UtcNow;
            if (maps.HasNewerAutosave(editor.Editor.Document.Name))
                OutputReceived?.Invoke(new TerminalOutput(
                    false,
                    $"A newer autosave exists for {editor.Editor.Document.Name}. Run 'map recover' or 'map discard-autosave'."));
        }
    }

    private void DetachCurrent()
    {
        if (current is RuntimeClientSession runtime)
            runtime.Network.CommandResultReceived -= OnNetworkCommandResult;
        if (current is EditorClientSession editor)
        {
            if (editor.PlaytestNetwork is { } playtestNetwork)
                playtestNetwork.CommandResultReceived -= OnNetworkCommandResult;
            editor.Editor.Changed -= OnEditorChanged;
            editor.SaveRequested -= OnEditorSaveRequested;
            editor.BakeRequested -= OnEditorBakeRequested;
            editor.FeedbackRequested -= OnEditorFeedbackRequested;
        }
    }

    private void OnNetworkCommandResult(CommandResultData result)
        => OutputReceived?.Invoke(new TerminalOutput(result.Success, result.Output));

    private void OnEditorChanged(EditorChange change) => lastEditUtc = DateTime.UtcNow;

    private void OnEditorFeedbackRequested(string message)
        => OutputReceived?.Invoke(new TerminalOutput(true, message));

    private void OnEditorSaveRequested()
    {
        if (current is not EditorClientSession editor) return;
        try
        {
            maps.Save(editor.Editor.Document);
            editor.Editor.MarkSaved();
            OutputReceived?.Invoke(new TerminalOutput(true, $"Saved {editor.Editor.Document.Name}"));
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(new TerminalOutput(false, $"Save failed: {ex.Message}"));
        }
    }

    private void OnEditorBakeRequested()
    {
        if (current is not EditorClientSession editor) return;
        try
        {
            byte[] contentHash = SaveAndBake(editor);
            OutputReceived?.Invoke(new TerminalOutput(
                true,
                $"Baked {editor.Editor.Document.Name} hash={Convert.ToHexStringLower(contentHash)}"));
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(new TerminalOutput(false, $"Bake failed: {ex.Message}"));
        }
    }

    private void Autosave()
    {
        if (current is not EditorClientSession editor || !editor.Editor.Dirty) return;
        DateTime now = DateTime.UtcNow;
        if (now - lastEditUtc < TimeSpan.FromSeconds(10)) return;
        if (now - lastAutosaveUtc < TimeSpan.FromMinutes(1)) return;
        try
        {
            maps.SaveAutosave(editor.Editor.Document);
            lastAutosaveUtc = now;
            OutputReceived?.Invoke(new TerminalOutput(true, $"Autosaved {editor.Editor.Document.Name}"));
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(new TerminalOutput(false, $"Autosave failed: {ex.Message}"));
        }
    }

    private byte[] SaveAndBake(EditorClientSession editor)
    {
        maps.Save(editor.Editor.Document);
        editor.Editor.MarkSaved();
        return SaveRuntime(editor.Editor.Document);
    }

    private byte[] SaveRuntime(EditorDocument document)
    {
        var runtime = EditorTerrainEvaluator.Bake(document);
        RuntimeMapSerializer.Save(maps.Paths.RuntimePath(document.Name), runtime);
        return runtime.ContentHash;
    }

    private string PrepareRuntimeFromSource(string name)
    {
        if (!maps.HasSource(name))
            throw new FileNotFoundException($"Map {name} has no saved source");

        var source = maps.Load(name);
        if (!maps.HasRuntime(name) || !maps.RuntimeIsCurrent(source))
            SaveRuntime(source);

        string path = maps.Paths.RuntimePath(name);
        _ = RuntimeMapSerializer.Load(path);
        return path;
    }

    private void EnsureCurrentRuntimeBake(string name)
    {
        if (!maps.HasRuntime(name)) throw new FileNotFoundException($"Map {name} has no runtime bake");
        if (maps.HasSource(name))
        {
            var source = maps.Load(name);
            if (!maps.RuntimeIsCurrent(source))
                throw new InvalidOperationException($"Runtime bake for {name} is stale; use --build");
        }
        _ = RuntimeMapSerializer.Load(maps.Paths.RuntimePath(name));
    }

    private bool CanLeaveEditor(bool discard, out string? error)
    {
        if (current is EditorClientSession editor && editor.Editor.Dirty && !discard)
        {
            error = "Map has unsaved changes; save it, use session host --build, or pass --discard where supported";
            return false;
        }
        error = null;
        return true;
    }

    private static TerminalOutput TerminalOutputFor(bool success, string text) => new(success, text);

    private string StructurePath(string mapName, string structureName)
        => Path.Combine(
            maps.Paths.DirectoryFor(mapName),
            "structures",
            MapPathResolver.ValidateName(structureName) + ".json");
}

public enum SessionRequestKind
{
    Editor,
    Host,
    GeneratedHost,
    Join,
}

public sealed record SessionRequest(
    SessionRequestKind Kind,
    string? MapName = null,
    string? RuntimeMapPath = null,
    string? Host = null,
    EditorDocument? Document = null,
    EditorSession? EditorSession = null,
    EditorToolSettings? EditorSettings = null,
    EditorStructureState? EditorStructures = null,
    System.Numerics.Vector3? SpawnOverride = null,
    int? InitialPlayerTeam = null,
    int InitialNpcsPerTeam = 0)
{
    public static SessionRequest EditorMap(string name) => new(SessionRequestKind.Editor, MapName: name);
    public static SessionRequest EditorDocument(EditorDocument document) => new(SessionRequestKind.Editor, Document: document);
    public static SessionRequest ResumeEditor(
        EditorSession editor,
        EditorToolSettings settings,
        EditorStructureState structures)
        => new(
            SessionRequestKind.Editor,
            EditorSession: editor,
            EditorSettings: settings,
            EditorStructures: structures);
    public static SessionRequest HostMap(
        string name, string path, System.Numerics.Vector3? spawnOverride = null)
        => new(SessionRequestKind.Host, name, path, SpawnOverride: spawnOverride);
    public static SessionRequest SourceHost(
        string name,
        int? initialPlayerTeam = null,
        int initialNpcsPerTeam = 0)
        => new(
            SessionRequestKind.Host,
            MapName: name,
            InitialPlayerTeam: initialPlayerTeam,
            InitialNpcsPerTeam: initialNpcsPerTeam);
    public static SessionRequest GeneratedHost() => new(SessionRequestKind.GeneratedHost);
    public static SessionRequest Join(string host) => new(SessionRequestKind.Join, Host: host);
}

internal sealed record EditorPlaytestState(
    EditorSession Editor,
    EditorToolSettings Settings,
    EditorStructureState Structures);
