namespace Demiurge;

public readonly record struct TerminalOutput(bool Success, string Text);

public interface ITerminalCommandDispatcher
{
    event Action<TerminalOutput>? OutputReceived;
    TerminalOutput? Execute(string commandLine);
    /// <summary>Candidates for the token being typed, and how far Tab may fill it in.</summary>
    CompletionResult Complete(string commandLine);
    IReadOnlyList<string> Help(string? topic = null);
}
