namespace Demiurge;

/// <summary>
/// Shared ownership state for client input modes. Gameplay scripts read this before polling input;
/// the terminal owns the flag while it is open.
/// </summary>
public sealed class ClientInputState
{
    public bool TerminalOpen { get; internal set; }
}
