using System.Numerics;
using System.Reflection;
using Demiurge.Net;
using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// Layer 1 of the transport conformance suite: every wire type must survive a byte round trip.
/// </summary>
/// <remarks>
/// The type list comes from <b>reflection, not from a list someone maintains</b>. That is the point of
/// this file. A wire type added through the RECIPES.md checklist is covered the moment it exists,
/// without anyone remembering to write a test — the completeness of the enumeration buys the coverage,
/// so diligence does not have to.
/// <para>
/// These run against <see cref="Message"/> directly rather than through a transport, because there is
/// only ONE serializer in the project. Both transports carry the bytes this produces, so proving the
/// round trip here proves it for both.
/// </para>
/// </remarks>
public class WireRoundTripTests
{
    public static IEnumerable<object[]> WireTypes()
        => WireTypeList().Select(type => new object[] { type });

    private static List<Type> WireTypeList()
        => typeof(Message).Assembly
            .GetTypes()
            .Where(type => typeof(IMessageSerializable).IsAssignableFrom(type)
                           && !type.IsInterface
                           && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Guards the guard. If the reflection query silently matched nothing — a namespace move, a renamed
    /// interface — every Theory below would vanish and the suite would still report green.
    /// </summary>
    [Fact]
    public void EnumerationFindsTheWireTypes()
    {
        List<Type> types = WireTypeList();

        Assert.True(
            types.Count >= 20,
            $"Expected at least 20 IMessageSerializable types, found {types.Count}. "
            + "If wire types genuinely moved, fix the query rather than lowering this number.");

        Assert.Contains(types, type => type.Name == "ComponentBundle");
        Assert.Contains(types, type => type.Name == "PlayerInputData");
    }

    /// <summary>
    /// Serialize, read back, serialize again — the two byte sequences must match.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT field-by-field equality. A type is allowed to carry a field it does not put on
    /// the wire, and comparing fields would fail those for no reason. Comparing re-serialized bytes still
    /// catches what actually matters: a Deserialize that reads fields in a different order, reads the
    /// wrong width, or forgets one, because any of those change what the second Serialize emits.
    /// </remarks>
    [Theory]
    [MemberData(nameof(WireTypes))]
    public void RoundTripsThroughBytes(Type type)
    {
        object original = Populate(type);

        Message first = Message.Create(MessageSendMode.Reliable, 1);
        ((IMessageSerializable)original).Serialize(first);
        byte[] firstBytes = first.Payload.ToArray();

        Message read = Message.CreateForRead(MessageSendMode.Reliable, 1, firstBytes);
        object restored = Activator.CreateInstance(type)!;
        ((IMessageSerializable)restored).Deserialize(read);

        Assert.True(
            read.UnreadBytes == 0,
            $"{type.Name} wrote {firstBytes.Length} bytes but Deserialize left {read.UnreadBytes} unread. "
            + "The reader and writer disagree about field order.");

        Message second = Message.Create(MessageSendMode.Reliable, 1);
        ((IMessageSerializable)restored).Serialize(second);

        Assert.Equal(firstBytes, second.Payload.ToArray());
    }

    /// <summary>
    /// Fills every public field with a deterministic non-default value.
    /// </summary>
    /// <remarks>
    /// Non-default matters. A default-constructed struct is all zeroes, and all-zeroes round-trips
    /// through almost any bug you care to write — including reading two fields in swapped order when
    /// both happen to be zero.
    /// </remarks>
    private static object Populate(Type type)
    {
        object instance = Activator.CreateInstance(type)!;

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(instance, ValueFor(field.FieldType, field.Name));

        return instance;
    }

    private static object? ValueFor(Type type, string seedName)
    {
        // Stable per field name, so a failure reproduces exactly.
        int seed = Math.Abs(StringComparer.Ordinal.GetHashCode(seedName));

        if (type.IsEnum)
        {
            Array values = Enum.GetValues(type);

            // For a [Flags] enum, OR everything together. On ComponentBundle.Mask specifically, that
            // makes the whole if-chain serialize — which is the branch coverage we actually want, since
            // that chain's ORDER is the protocol.
            if (type.GetCustomAttribute<FlagsAttribute>() is not null)
            {
                long combined = 0;
                foreach (object value in values) combined |= Convert.ToInt64(value);
                return Enum.ToObject(type, combined);
            }

            // Otherwise prefer a non-zero member, so the value is distinguishable from default.
            foreach (object value in values)
                if (Convert.ToInt64(value) != 0)
                    return value;

            return values.GetValue(0);
        }

        if (type == typeof(bool)) return true;
        if (type == typeof(byte)) return (byte)(seed % 200 + 7);
        if (type == typeof(ushort)) return (ushort)(seed % 30000 + 11);
        if (type == typeof(uint)) return (uint)(seed % 1_000_000 + 13);
        if (type == typeof(int)) return seed % 1_000_000 + 17;
        if (type == typeof(float)) return seed % 1000 + 0.5f;
        if (type == typeof(string)) return $"conformance-{seedName}";
        if (type == typeof(Guid)) return new Guid(seed, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        if (type == typeof(Vector3)) return new Vector3(seed % 100 + 0.25f, seed % 50 - 1.5f, seed % 75 + 2.75f);
        if (type == typeof(Vector2)) return new Vector2(seed % 100 + 0.25f, seed % 50 - 1.5f);
        if (type == typeof(byte[])) return new byte[] { 1, 2, 3, 4, 5 };

        if (typeof(IMessageSerializable).IsAssignableFrom(type)) return Populate(type);

        throw new NotSupportedException(
            $"The conformance filler does not know how to populate {type.Name} (field '{seedName}'). "
            + "Add it here — leaving it unpopulated would silently weaken every round-trip test.");
    }
}
