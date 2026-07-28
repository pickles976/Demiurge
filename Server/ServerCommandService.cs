using System.Diagnostics;
using System.Numerics;

namespace Demiurge.GameServer;

internal sealed class ServerCommandService
{
    private const int MaxCommandsPerSecond = 10;
    private const float DefaultSpawnDistance = 3f;

    private readonly ICommandWorld world;
    private readonly bool allowCheats;
    private readonly Dictionary<ushort, RateWindow> rateWindows = new();

    public ServerCommandService(ICommandWorld world, bool allowCheats)
    {
        this.world = world;
        this.allowCheats = allowCheats;
    }

    public CommandResultData Execute(ushort issuerId, CommandRequestData request)
    {
        if (!allowCheats)
            return Result(request.RequestId, false, "Commands are disabled on this server");
        if (!WithinRateLimit(issuerId))
            return Result(request.RequestId, false, "Command rate limit exceeded");
        if (!world.TryGetActor(issuerId, out var issuer))
            return Result(request.RequestId, false, "Command source is not an active player");

        var parsed = GameCommandParser.Parse(request.Command ?? string.Empty);
        if (!parsed.Success)
            return Result(request.RequestId, false, parsed.Error!);

        CommandResultData result;
        try
        {
            result = parsed.Command switch
            {
                SpawnMobCommand command => SpawnMob(request.RequestId, issuer, command),
                SpawnPickupCommand command => SpawnPickup(request.RequestId, issuer, command),
                EquipCommand command => Equip(request.RequestId, issuer, command),
                _ => Result(request.RequestId, false, "Unsupported command"),
            };
        }
        catch (Exception ex)
        {
            result = Result(request.RequestId, false, $"Command failed: {ex.Message}");
        }

        Console.WriteLine(
            $"[Command] actor={issuerId} success={result.Success} input={request.Command} output={result.Output}");
        return result;
    }

    public void Forget(ushort clientId) => rateWindows.Remove(clientId);

    private CommandResultData SpawnMob(uint requestId, ServerPlayer issuer, SpawnMobCommand command)
    {
        if (!TryResolvePosition(issuer, command.Position, out var position, out string? error))
            return Result(requestId, false, error!);

        var mob = world.SpawnMob(position);
        return Result(requestId, true,
            $"Spawned mob @{mob.Id} at {FormatPosition(mob.Position)}");
    }

    private CommandResultData SpawnPickup(uint requestId, ServerPlayer issuer, SpawnPickupCommand command)
    {
        if (!TryResolvePosition(issuer, command.Position, out var position, out string? error))
            return Result(requestId, false, error!);

        var pickup = world.SpawnPickup(command.Item, position);
        return Result(requestId, true,
            $"Spawned {ItemCatalog.Id(command.Item)} object #{pickup.NetworkId} at {FormatPosition(position)}");
    }

    private CommandResultData Equip(uint requestId, ServerPlayer issuer, EquipCommand command)
    {
        ushort actorId = command.Target.IsSelf ? issuer.Id : command.Target.ActorId;
        if (!world.TryGetActor(actorId, out var target))
            return Result(requestId, false, $"Actor @{actorId} does not exist");

        var stats = ItemConfig.Get(command.Item);
        if (stats.Category != ItemCategory.Equippable)
            return Result(requestId, false, $"{ItemCatalog.Id(command.Item)} cannot be equipped");

        world.Equip(target, command.Item);
        return Result(requestId, true, $"Equipped @{actorId} with {ItemCatalog.Id(command.Item)}");
    }

    private bool TryResolvePosition(
        ServerPlayer issuer,
        CommandPosition? requested,
        out Vector3 position,
        out string? error)
    {
        float x;
        float z;
        if (requested is { } coordinates)
        {
            x = coordinates.X.Resolve(issuer.Position.X);
            z = coordinates.Z.Resolve(issuer.Position.Z);
        }
        else
        {
            x = issuer.Position.X + MathF.Sin(issuer.Yaw) * DefaultSpawnDistance;
            z = issuer.Position.Z + MathF.Cos(issuer.Yaw) * DefaultSpawnDistance;
        }

        if (!float.IsFinite(x) || !float.IsFinite(z) || !world.IsSpawnableColumn(x, z))
        {
            position = default;
            error = "Spawn position is outside generated terrain";
            return false;
        }

        position = world.SurfacePosition(x, z);
        error = null;
        return true;
    }

    private bool WithinRateLimit(ushort clientId)
    {
        long now = Stopwatch.GetTimestamp();
        long oneSecond = Stopwatch.Frequency;
        if (!rateWindows.TryGetValue(clientId, out var window) || now - window.Start >= oneSecond)
        {
            rateWindows[clientId] = new RateWindow(now, 1);
            return true;
        }

        if (window.Count >= MaxCommandsPerSecond) return false;
        rateWindows[clientId] = window with { Count = window.Count + 1 };
        return true;
    }

    private static CommandResultData Result(uint requestId, bool success, string output)
        => new() { RequestId = requestId, Success = success, Output = output };

    private static string FormatPosition(Vector3 position)
        => FormattableString.Invariant($"({position.X:0.##}, {position.Y:0.##}, {position.Z:0.##})");

    private readonly record struct RateWindow(long Start, int Count);
}
