using System.Globalization;

namespace Demiurge;

public abstract record GameCommand;

public sealed record SpawnMobCommand(CommandPosition? Position) : GameCommand;

public sealed record SpawnPickupCommand(ItemType Item, CommandPosition? Position) : GameCommand;

public sealed record EquipCommand(ActorSelector Target, ItemType Item) : GameCommand;

/// <summary>An absolute coordinate, or an offset from the command source when prefixed by '~'.</summary>
public readonly record struct CommandCoordinate(float Value, bool Relative)
{
    public float Resolve(float origin) => Relative ? origin + Value : Value;
}

/// <summary>
/// Horizontal command position. The server derives Y from authoritative terrain so spawned actors
/// and pickups cannot be placed inside the field by a client-provided height.
/// </summary>
public readonly record struct CommandPosition(CommandCoordinate X, CommandCoordinate Z);

/// <summary>The issuing actor, or a session actor id shared by human players and mobs.</summary>
public readonly record struct ActorSelector(bool IsSelf, ushort ActorId)
{
    public static ActorSelector Self => new(true, 0);
    public static ActorSelector Id(ushort actorId) => new(false, actorId);
}

public readonly record struct CommandParseResult(GameCommand? Command, string? Error)
{
    public bool Success => Command != null;

    public static CommandParseResult Ok(GameCommand command) => new(command, null);
    public static CommandParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Small typed command grammar modeled after command-tree systems such as Brigadier. Execution is a
/// separate server concern; this parser only turns text into validated command values.
/// </summary>
public static class GameCommandParser
{
    public const int MaxCommandLength = 256;

    public static CommandParseResult Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return CommandParseResult.Fail("Enter a command");
        if (input.Length > MaxCommandLength)
            return CommandParseResult.Fail($"Command exceeds {MaxCommandLength} characters");

        if (!TryTokenize(input, out var tokens, out string? tokenError))
            return CommandParseResult.Fail(tokenError!);
        if (tokens.Count == 0)
            return CommandParseResult.Fail("Enter a command");

        if (tokens[0].StartsWith('/'))
        {
            tokens[0] = tokens[0][1..];
            if (tokens[0].Length == 0)
                return CommandParseResult.Fail("Enter a command after '/'");
        }

        return tokens[0].ToLowerInvariant() switch
        {
            "spawn" => ParseSpawn(tokens),
            "equip" => ParseEquip(tokens),
            _ => CommandParseResult.Fail($"Unknown command: {tokens[0]}"),
        };
    }

    private static CommandParseResult ParseSpawn(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
            return CommandParseResult.Fail("Usage: spawn <mob|pickup> ...");

        return tokens[1].ToLowerInvariant() switch
        {
            "mob" => ParseSpawnMob(tokens),
            "pickup" => ParseSpawnPickup(tokens),
            _ => UnknownSpawnKind(tokens[1]),
        };
    }

    private static CommandParseResult UnknownSpawnKind(string value)
    {
        if (ItemCatalog.TryResolve(value, out var item))
            return CommandParseResult.Fail(
                $"'{value}' is an item. Use 'spawn pickup {ItemCatalog.Id(item)} <x> <z>'");
        return CommandParseResult.Fail(
            $"Unknown entity kind: {value}. Use 'spawn mob ...' or 'spawn pickup <item> ...'");
    }

    private static CommandParseResult ParseSpawnMob(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 2)
            return CommandParseResult.Ok(new SpawnMobCommand(null));
        if (tokens.Count != 4)
            return CommandParseResult.Fail("Usage: spawn mob [x z]");
        if (!TryPosition(tokens[2], tokens[3], out var position, out string? error))
            return CommandParseResult.Fail(error!);
        return CommandParseResult.Ok(new SpawnMobCommand(position));
    }

    private static CommandParseResult ParseSpawnPickup(IReadOnlyList<string> tokens)
    {
        if (tokens.Count is not (3 or 5))
            return CommandParseResult.Fail("Usage: spawn pickup <item> [x z]");
        if (!ItemCatalog.TryResolve(tokens[2], out ItemType item))
            return CommandParseResult.Fail($"Unknown item: {tokens[2]}");
        if (tokens.Count == 3)
            return CommandParseResult.Ok(new SpawnPickupCommand(item, null));
        if (!TryPosition(tokens[3], tokens[4], out var position, out string? error))
            return CommandParseResult.Fail(error!);
        return CommandParseResult.Ok(new SpawnPickupCommand(item, position));
    }

    private static CommandParseResult ParseEquip(IReadOnlyList<string> tokens)
    {
        if (tokens.Count != 3)
            return CommandParseResult.Fail("Usage: equip <@s|@actor-id> <item>");
        if (!TryActorSelector(tokens[1], out var selector))
            return CommandParseResult.Fail($"Invalid actor selector: {tokens[1]}");
        if (!ItemCatalog.TryResolve(tokens[2], out ItemType item))
            return CommandParseResult.Fail($"Unknown item: {tokens[2]}");
        return CommandParseResult.Ok(new EquipCommand(selector, item));
    }

    private static bool TryActorSelector(string token, out ActorSelector selector)
    {
        if (token.Equals("@s", StringComparison.OrdinalIgnoreCase))
        {
            selector = ActorSelector.Self;
            return true;
        }

        if (token.Length > 1 && token[0] == '@'
            && ushort.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out ushort id))
        {
            selector = ActorSelector.Id(id);
            return true;
        }

        selector = default;
        return false;
    }

    private static bool TryPosition(
        string xToken,
        string zToken,
        out CommandPosition position,
        out string? error)
    {
        if (!TryCoordinate(xToken, out var x))
        {
            position = default;
            error = $"Invalid X coordinate: {xToken}";
            return false;
        }
        if (!TryCoordinate(zToken, out var z))
        {
            position = default;
            error = $"Invalid Z coordinate: {zToken}";
            return false;
        }

        position = new CommandPosition(x, z);
        error = null;
        return true;
    }

    private static bool TryCoordinate(string token, out CommandCoordinate coordinate)
    {
        bool relative = token.StartsWith('~');
        ReadOnlySpan<char> number = relative ? token.AsSpan(1) : token.AsSpan();
        if (relative && number.Length == 0)
        {
            coordinate = new CommandCoordinate(0f, true);
            return true;
        }

        if (float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            && float.IsFinite(value))
        {
            coordinate = new CommandCoordinate(value, relative);
            return true;
        }

        coordinate = default;
        return false;
    }

    private static bool TryTokenize(string input, out List<string> tokens, out string? error)
    {
        tokens = new List<string>();
        error = null;

        int index = 0;
        while (index < input.Length)
        {
            while (index < input.Length && char.IsWhiteSpace(input[index])) index++;
            if (index == input.Length) break;

            if (input[index] != '"')
            {
                int start = index;
                while (index < input.Length && !char.IsWhiteSpace(input[index])) index++;
                tokens.Add(input[start..index]);
                continue;
            }

            index++;
            var value = new System.Text.StringBuilder();
            bool closed = false;
            while (index < input.Length)
            {
                char current = input[index++];
                if (current == '"')
                {
                    closed = true;
                    break;
                }
                if (current == '\\' && index < input.Length && input[index] is '"' or '\\')
                    current = input[index++];
                value.Append(current);
            }

            if (!closed)
            {
                error = "Unterminated quoted argument";
                return false;
            }
            if (index < input.Length && !char.IsWhiteSpace(input[index]))
            {
                error = "Quoted arguments must be separated by spaces";
                return false;
            }
            tokens.Add(value.ToString());
        }

        return true;
    }
}
