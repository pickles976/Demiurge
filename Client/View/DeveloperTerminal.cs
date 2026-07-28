using System.Collections.Concurrent;
using System.Text;
using Demiurge.GameClient;
using Stride.Engine;
using Stride.Input;
using Stride.UI;
using Stride.UI.Controls;

namespace Demiurge;

/// <summary>
/// Lightweight client command terminal. Command dispatch is deliberately independent of any future
/// scripting language so an evaluator can be registered here without taking ownership of input/UI.
/// </summary>
public sealed class DeveloperTerminalScript : SyncScript, IInputEventListener<TextInputEvent>
{
    private const int MaxInputLength = 256;
    private const int VisibleLineCount = 10;

    public required ClientInputState InputState { get; init; }
    public required NetworkManager Network { get; init; }
    public required UIElement Panel { get; init; }
    public required TextBlock OutputText { get; init; }
    public required TextBlock PromptText { get; init; }

    private readonly StringBuilder input = new();
    private readonly List<string> output = new();
    private readonly List<string> history = new();
    private readonly ConcurrentQueue<CommandResultData> pendingResults = new();
    private Dictionary<string, Action<string>> localCommands = null!;
    private int historyIndex;
    private bool toggleWasDown;

    public override void Start()
    {
        Panel.Visibility = Visibility.Collapsed;
        localCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["clear"] = _ => output.Clear(),
            ["echo"] = arguments => output.Add(arguments),
            ["help"] = _ =>
            {
                output.Add("spawn mob [x z]");
                output.Add("spawn pickup <item> [x z]");
                output.Add("equip <@s|@actor-id> <item>");
                output.Add("Items: " + string.Join(", ", ItemCatalog.All.Select(item => item.Id)));
            },
        };
        Network.CommandResultReceived += ReceiveResult;
        Input.AddListener(this);
        Refresh();
    }

    public override void Cancel()
    {
        Close();
        Network.CommandResultReceived -= ReceiveResult;
        Input.RemoveListener(this);
    }

    public override void Update()
    {
        while (pendingResults.TryDequeue(out var result))
        {
            output.Add(result.Success ? result.Output : $"Error: {result.Output}");
            TrimOutput();
            Refresh();
        }

        // Stride reports repeated SDL key-down events as fresh presses, so the mode key needs an
        // explicit edge latch.
        bool toggleKeyDown = Input.IsKeyDown(Keys.OemTilde);
        bool shiftDown = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);
        if (toggleKeyDown && !toggleWasDown && !shiftDown)
        {
            if (InputState.TerminalOpen)
                Close();
            else
                Open();
        }
        toggleWasDown = toggleKeyDown;

        if (!InputState.TerminalOpen) return;

        foreach (var keyEvent in Input.KeyEvents)
        {
            if (!keyEvent.IsDown) continue;

            switch (keyEvent.Key)
            {
                case Keys.Escape:
                    Close();
                    return;
                case Keys.Enter:
                case Keys.NumPadEnter:
                    if (keyEvent.RepeatCount == 0) Submit();
                    break;
                case Keys.Back:
                    if (input.Length > 0)
                    {
                        input.Length--;
                        historyIndex = history.Count;
                        Refresh();
                    }
                    break;
                case Keys.Up:
                    if (keyEvent.RepeatCount == 0) RecallHistory(-1);
                    break;
                case Keys.Down:
                    if (keyEvent.RepeatCount == 0) RecallHistory(1);
                    break;
            }
        }
    }

    public void ProcessEvent(TextInputEvent inputEvent)
    {
        if (!InputState.TerminalOpen || inputEvent.Type != TextInputEventType.Input) return;

        foreach (char character in inputEvent.Text)
        {
            // The toggle key can also arrive as committed text before Update closes the terminal.
            if (character is '`' or '\r' or '\n' || char.IsControl(character)) continue;
            if (input.Length >= MaxInputLength) break;
            input.Append(character);
        }

        historyIndex = history.Count;
        Refresh();
    }

    private void Open()
    {
        InputState.TerminalOpen = true;
        Panel.Visibility = Visibility.Visible;
        Input.UnlockMousePosition();
        Input.TextInput?.EnabledTextInput();
        Game.IsMouseVisible = true;
        historyIndex = history.Count;
        Refresh();
    }

    private void Close()
    {
        if (!InputState.TerminalOpen) return;

        InputState.TerminalOpen = false;
        Panel.Visibility = Visibility.Collapsed;
        Input.TextInput?.DisableTextInput();
    }

    private void Submit()
    {
        string commandLine = input.ToString().Trim();
        input.Clear();
        historyIndex = history.Count;

        if (commandLine.Length == 0)
        {
            Refresh();
            return;
        }

        history.Add(commandLine);
        output.Add($"> {commandLine}");
        Execute(commandLine);
        TrimOutput();
        Refresh();
    }

    private void Execute(string commandLine)
    {
        string normalized = commandLine.StartsWith('/') ? commandLine[1..] : commandLine;
        int separator = normalized.IndexOf(' ');
        string command = separator < 0 ? normalized : normalized[..separator];
        string arguments = separator < 0 ? string.Empty : normalized[(separator + 1)..].TrimStart();

        if (localCommands.TryGetValue(command, out var executeLocal))
        {
            executeLocal(arguments);
            return;
        }

        Network.SendCommand(commandLine);
    }

    private void ReceiveResult(CommandResultData result) => pendingResults.Enqueue(result);

    private void RecallHistory(int direction)
    {
        if (history.Count == 0) return;

        historyIndex = Math.Clamp(historyIndex + direction, 0, history.Count);
        input.Clear();
        if (historyIndex < history.Count) input.Append(history[historyIndex]);
        Refresh();
    }

    private void TrimOutput()
    {
        int excess = output.Count - VisibleLineCount;
        if (excess > 0) output.RemoveRange(0, excess);
    }

    private void Refresh()
    {
        OutputText.Text = string.Join('\n', output);
        PromptText.Text = $"> {input}_";
    }
}
