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

        return ExecuteParsed(
            new ServerCommandSource(ServerCommandSourceKind.Player, issuer),
            request.RequestId,
            request.Command ?? string.Empty);
    }

    public CommandResultData ExecuteConsole(string command)
        => ExecuteParsed(
            new ServerCommandSource(ServerCommandSourceKind.Console, null),
            requestId: 0,
            command);

    private CommandResultData ExecuteParsed(
        ServerCommandSource source,
        uint requestId,
        string input)
    {
        var parsed = GameCommandParser.Parse(input);
        if (!parsed.Success)
            return Result(requestId, false, parsed.Error!);

        CommandResultData result;
        try
        {
            result = parsed.Command switch
            {
                SpawnMobCommand command => SpawnMob(requestId, source, command),
                SpawnPickupCommand command => SpawnPickup(requestId, source, command),
                EquipCommand command => Equip(requestId, source, command),
                _ => Result(requestId, false, "Unsupported command"),
            };
        }
        catch (Exception ex)
        {
            result = Result(requestId, false, $"Command failed: {ex.Message}");
        }

        Console.WriteLine(
            $"[Command] source={source.Kind.ToString().ToLowerInvariant()} actor={source.Actor?.Id.ToString() ?? "-"} success={result.Success} input={input} output={result.Output}");
        return result;
    }

    public void Forget(ushort clientId) => rateWindows.Remove(clientId);

    private CommandResultData SpawnMob(uint requestId, ServerCommandSource source, SpawnMobCommand command)
    {
        if (!TryResolvePosition(source, command.Position, out var position, out string? error))
            return Result(requestId, false, error!);

        var mob = world.SpawnMob(position);
        return Result(requestId, true,
            $"Spawned mob; actor ID @{mob.Id}; position {FormatPosition(mob.Position)}");
    }

    private CommandResultData SpawnPickup(uint requestId, ServerCommandSource source, SpawnPickupCommand command)
    {
        if (!TryResolvePosition(source, command.Position, out var position, out string? error))
            return Result(requestId, false, error!);

        var pickup = world.SpawnPickup(command.Item, position);
        return Result(requestId, true,
            $"Spawned {ItemCatalog.Id(command.Item)} pickup; object ID #{pickup.NetworkId}; position {FormatPosition(position)}");
    }

    private CommandResultData Equip(uint requestId, ServerCommandSource source, EquipCommand command)
    {
        if (command.Target.IsSelf && source.Actor is null)
            return Result(requestId, false, "@s is unavailable from the dedicated server console");

        ushort actorId = command.Target.IsSelf ? source.Actor!.Id : command.Target.ActorId;
        if (!world.TryGetActor(actorId, out var target))
            return Result(requestId, false, $"Actor @{actorId} does not exist");

        var stats = ItemConfig.Get(command.Item);
        if (stats.Category != ItemCategory.Equippable)
            return Result(requestId, false, $"{ItemCatalog.Id(command.Item)} cannot be equipped");

        world.Equip(target, command.Item);
        return Result(requestId, true, $"Equipped @{actorId} with {ItemCatalog.Id(command.Item)}");
    }

    private bool TryResolvePosition(
        ServerCommandSource source,
        CommandPosition? requested,
        out Vector3 position,
        out string? error)
    {
        float x;
        float z;
        if (source.Kind == ServerCommandSourceKind.Console)
        {
            if (requested is not { } consoleCoordinates)
            {
                position = default;
                error = "Dedicated server console spawn commands require absolute X and Z coordinates";
                return false;
            }
            if (consoleCoordinates.X.Relative || consoleCoordinates.Z.Relative)
            {
                position = default;
                error = "Relative coordinates are unavailable from the dedicated server console";
                return false;
            }
            x = consoleCoordinates.X.Value;
            z = consoleCoordinates.Z.Value;
        }
        else if (requested is { } coordinates)
        {
            x = coordinates.X.Resolve(source.Actor!.Position.X);
            z = coordinates.Z.Resolve(source.Actor.Position.Z);
        }
        else
        {
            x = source.Actor!.Position.X + MathF.Sin(source.Actor.Yaw) * DefaultSpawnDistance;
            z = source.Actor.Position.Z + MathF.Cos(source.Actor.Yaw) * DefaultSpawnDistance;
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

internal enum ServerCommandSourceKind
{
    Player,
    Console,
}

internal readonly record struct ServerCommandSource(
    ServerCommandSourceKind Kind,
    ServerPlayer? Actor);
