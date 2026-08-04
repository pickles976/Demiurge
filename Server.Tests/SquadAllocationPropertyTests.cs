using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// Invariants of the squad allocation, fuzzed rather than exampled.
///
/// Every bug this file exists for was an unintended COMBINATION rather than a wrong value: a guard
/// that also skipped a roster insertion, a permit that overrode an order, an entrench flag that
/// overrode a bound. None of them is visible in any single hand-written scenario, because each one
/// needs a particular mix of ranges, weapons and prior state to show up.
///
/// So these assert properties over randomised squads instead. `SquadTactics.Plan` is pure, which is
/// what makes fuzzing it cheap — a thousand squads cost milliseconds and no server.
/// </summary>
public class SquadAllocationPropertyTests
{
    private const int Cases = 400;

    private static readonly ItemType[] Weapons =
        [ItemType.Ppsh, ItemType.Ak47, ItemType.Sks, ItemType.Mosin];

    private static (SquadPlanInput Input, SquadMemberState[] Members) Squad(Random random)
    {
        int size = random.Next(1, SquadBlackboard.MaximumMembers + 1);
        var threat = new Vector3(
            (float)(random.NextDouble() * 200 - 100),
            0f,
            (float)(random.NextDouble() * 200 - 100));

        var members = new SquadMemberState[size];
        for (int i = 0; i < size; i++)
            members[i] = new SquadMemberState(
                (ushort)(60_000 + i),
                new Vector3(
                    (float)(random.NextDouble() * 200 - 100),
                    0f,
                    (float)(random.NextDouble() * 200 - 100)),
                Weapons[random.Next(Weapons.Length)],
                SelfExposure.Of((float)random.NextDouble()),
                SkillFactor: 0.5f + (float)random.NextDouble(),
                BoundIndex: random.Next(0, 4),
                MovingSinceTick: random.Next(0, 2) == 0 ? 0u : (uint)random.Next(1, 500));

        var input = new SquadPlanInput(
            threat,
            HasThreat: random.Next(0, 5) != 0,
            Weapons[random.Next(Weapons.Length)],
            Aggression: 0.25f + (float)random.NextDouble() * 4f,
            Tick: (uint)random.Next(0, 2000));

        return (input, members);
    }

    private static List<SquadTacticalOrder> Plan(in SquadPlanInput input, SquadMemberState[] members)
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(input, members, orders);
        return orders;
    }

    /// <summary>
    /// The one that would have caught the roster bug outright. Every man handed to the allocation
    /// must come back out of it with exactly one order — no duplicates, and above all nobody dropped.
    /// </summary>
    [Fact]
    public void EveryMemberGetsExactlyOneOrder()
    {
        var random = new Random(20260804);
        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            var orders = Plan(input, members);

            Assert.Equal(members.Length, orders.Count);
            foreach (var member in members)
                Assert.Single(orders, order => order.ActorId == member.ActorId);
        }
    }

    /// <summary>
    /// The anti-deadlock invariant, stated over arbitrary squads rather than one scenario. With a
    /// threat and more than one man, the plan must never be "everybody stands still" — that is the
    /// exact state the old IsSet gate produced, and the one the player reported as NPCs standing at
    /// the flagpost doing nothing.
    /// </summary>
    [Fact]
    public void AContestedSquadIsNeverEntirelyStatic()
    {
        var random = new Random(555);
        int contested = 0;

        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            if (!input.HasThreat || members.Length < 2) continue;
            contested++;

            var orders = Plan(input, members);
            Assert.Contains(orders, order => order.Role != SquadRole.None);
        }

        Assert.True(contested > 50, $"only {contested} contested squads generated; fuzz is too narrow");
    }

    [Fact]
    public void NoThreatMeansNobodyIsGivenACombatRole()
    {
        var random = new Random(777);
        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            if (input.HasThreat) continue;

            Assert.All(Plan(input, members), order => Assert.Equal(SquadRole.None, order.Role));
        }
    }

    /// <summary>A man the rotation has given up waiting for is never handed another bound in the same
    /// breath — that is what makes the timeout an escape hatch rather than a no-op.</summary>
    [Fact]
    public void ATimedOutMoverIsNeverOrderedToBoundAgain()
    {
        var random = new Random(31337);
        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            var orders = Plan(input, members);

            foreach (var member in members)
            {
                bool timedOut = member.MovingSinceTick != 0
                    && input.Tick >= member.MovingSinceTick
                    && input.Tick - member.MovingSinceTick >= SquadTactics.MoverTimeoutTicks;
                if (!timedOut) continue;

                Assert.DoesNotContain(
                    orders,
                    order => order.ActorId == member.ActorId && order.Role == SquadRole.Bound);
            }
        }
    }

    /// <summary>Two movers behind the same piece of cover are one mover. Bearings must stay distinct,
    /// which is the entire reason flanks spread rather than stacking.</summary>
    [Fact]
    public void MoversNeverShareABearing()
    {
        var random = new Random(24601);
        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            var bearings = Plan(input, members)
                .Where(order => order.Role == SquadRole.Bound)
                .Select(order => order.Bearing)
                .ToList();

            Assert.Equal(bearings.Count, bearings.Distinct().Count());
        }
    }

    /// <summary>
    /// A bound must close. It used to be possible for a man already inside the opening standoff to be
    /// sent to a point further from the threat than he started — measured, two of six men ended a
    /// thirty-second assault further away than they began.
    /// </summary>
    [Fact]
    public void EveryBoundEndsCloserToTheThreatThanItBegan()
    {
        var random = new Random(8675309);
        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            var orders = Plan(input, members);

            foreach (var order in orders)
            {
                if (order.Role != SquadRole.Bound) continue;
                var member = members.Single(m => m.ActorId == order.ActorId);

                float before = Horizontal(member.Position, input.Threat);
                float after = Horizontal(order.Destination, input.Threat);

                Assert.True(
                    after <= before + 0.01f,
                    $"bound sent {order.ActorId} from {before:0.0} m to {after:0.0} m");
            }
        }
    }

    /// <summary>Somebody has to be shooting while somebody moves, or the bound is unsupported and the
    /// suppression term that paid for it was a fiction.</summary>
    [Fact]
    public void SomeoneCoversEveryBound()
    {
        var random = new Random(112358);
        for (int c = 0; c < Cases; c++)
        {
            var (input, members) = Squad(random);
            var orders = Plan(input, members);
            if (!orders.Any(order => order.Role == SquadRole.Bound)) continue;

            Assert.Contains(orders, order => order.Role == SquadRole.BaseOfFire);
        }
    }

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
