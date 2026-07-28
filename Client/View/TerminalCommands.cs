namespace Demiurge;

public readonly record struct TerminalOutput(bool Success, string Text);

public interface ITerminalCommandDispatcher
{
    event Action<TerminalOutput>? OutputReceived;
    TerminalOutput? Execute(string commandLine);
    IReadOnlyList<string> Help();
}
