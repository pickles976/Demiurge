namespace Demiurge.GameServer;

public sealed record ServerOptions
{
    public bool AllowCheats { get; init; }
    public string? MapPath { get; init; }
}
