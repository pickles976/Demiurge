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
                if (tokens.Length > 1 && tokens[1].Equals("structure", StringComparison.OrdinalIgnoreCase))
                    return ExecuteStructure(tokens, editor);
                var result = EditorCommandParser.Execute(normalized, editor.Settings, editor.Editor);
                return TerminalOutputFor(result.Success, result.Output);
            }

            if (current is RuntimeClientSession runtime)
            {
                runtime.Network.SendCommand(commandLine);
                return null;
            }

            return TerminalOutputFor(false, $"Unknown command: {tokens[0]}");
        }
        catch (Exception ex)
        {
            return TerminalOutputFor(false, ex.Message);
        }
    }

    public IReadOnlyList<string> Help()
    {
        var lines = new List<string>
        {
            "session <status|editor|host|join> ...",
            "map <list|new|status|save|save-as|load|recover|discard-autosave|validate|bake> ...",
        };
        if (current is EditorClientSession)
            lines.Add("editor <status|mode|terrain|block|object|rotate|undo|redo> ...");
        else
        {
            lines.Add("spawn mob [x z]");
            lines.Add("spawn pickup <item> [x z]");
            lines.Add("equip <@s|@actor-id> <item>");
        }
        return lines;
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

    private TerminalOutput ExecuteSession(string[] tokens)
    {
        if (tokens.Length < 2)
            return TerminalOutputFor(false, "Usage: session <status|editor|host|join> ...");

        switch (tokens[1].ToLowerInvariant())
        {
            case "status":
                return TerminalOutputFor(true, current switch
                {
                    EditorClientSession editor =>
                        $"editor map={editor.Editor.Document.Name} dirty={editor.Editor.Dirty}",
                    RuntimeClientSession => "runtime",
                    _ => "idle",
                });

            case "editor":
                if (tokens.Length != 3) return TerminalOutputFor(false, "Usage: session editor <map-name>");
                if (!CanLeaveEditor(discard: false, out var error)) return TerminalOutputFor(false, error!);
                transitions.Enqueue(SessionRequest.EditorMap(tokens[2]));
                return TerminalOutputFor(true, $"Switching to editor map {tokens[2]}");

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

                transitions.Enqueue(SessionRequest.HostMap(tokens[2], maps.Paths.RuntimePath(tokens[2])));
                return TerminalOutputFor(true, $"Hosting runtime map {tokens[2]}");
            }

            case "join":
                if (tokens.Length != 3) return TerminalOutputFor(false, "Usage: session join <host>");
                if (!CanLeaveEditor(discard: false, out var joinError))
                    return TerminalOutputFor(false, joinError!);
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
                    game, inputState, request.Document
                        ?? (maps.HasSource(request.MapName!)
                            ? maps.Load(request.MapName!)
                            : maps.New(request.MapName!))),
                SessionRequestKind.Host => new RuntimeClientSession(
                    game, inputState, NetworkConfig.ServerHost,
                    new ServerOptions { AllowCheats = true, MapPath = request.RuntimeMapPath }),
                SessionRequestKind.GeneratedHost => new RuntimeClientSession(
                    game, inputState, NetworkConfig.ServerHost,
                    new ServerOptions { AllowCheats = true }),
                SessionRequestKind.Join => new RuntimeClientSession(game, inputState, request.Host),
                _ => throw new InvalidOperationException($"Unknown session request {request.Kind}"),
            };
            candidate.Start(scene);
            current = candidate;
            AttachCurrent();
            OutputReceived?.Invoke(new TerminalOutput(true, $"Session active: {current.Kind}"));
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            current = null;
            OutputReceived?.Invoke(new TerminalOutput(false, $"Session transition failed: {ex.Message}"));
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
            editor.Editor.Changed -= OnEditorChanged;
            editor.SaveRequested -= OnEditorSaveRequested;
            editor.BakeRequested -= OnEditorBakeRequested;
        }
    }

    private void OnNetworkCommandResult(CommandResultData result)
        => OutputReceived?.Invoke(new TerminalOutput(result.Success, result.Output));

    private void OnEditorChanged(EditorChange change) => lastEditUtc = DateTime.UtcNow;

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
    EditorDocument? Document = null)
{
    public static SessionRequest EditorMap(string name) => new(SessionRequestKind.Editor, MapName: name);
    public static SessionRequest EditorDocument(EditorDocument document) => new(SessionRequestKind.Editor, Document: document);
    public static SessionRequest HostMap(string name, string path) => new(SessionRequestKind.Host, name, path);
    public static SessionRequest GeneratedHost() => new(SessionRequestKind.GeneratedHost);
    public static SessionRequest Join(string host) => new(SessionRequestKind.Join, Host: host);
}
