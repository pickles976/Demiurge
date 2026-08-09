# AI Tactical Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix six reported AI behaviours by replacing three hand-written ladders/branches with pricing in one currency, and by adding the two sense inputs the AI is missing.

**Architecture:** Everything scoreable is priced in **tickets per second**, which is what actually ends a round. The three existing currencies collapse into one: `ConquestConfig.BleedFor` already makes a flag worth `1 / TicketBleedSeconds` tickets per second, and `TicketSystem.ChargeRespawn` costs one ticket per body — so combat's health-per-second converts through health-per-death, and navigation's estimated seconds are the time axis both are discounted over. New scoring lives pure in `Common/Ai/`; the server supplies observations and consumes scores.

**Tech Stack:** C# / net10.0, xUnit, `System.Numerics`. No new dependencies.

## The five phases are independently shippable

Each ends with its own verification gate and changes behaviour on its own. Stop between any two. They are ordered by value-per-risk, not by dependency — only Phase 2 depends on Phase 1 (it reuses `StrategicValue.TicketsPerSecondPerFlag`).

| phase | fixes | root cause being removed |
|---|---|---|
| 1 | fights over 1-2 flags; squads oscillate between flags | `SlotPriority`'s discrete ladder over noisy presence booleans |
| 2 | squad charges a distant sniper, abandoning its objective | incoming fire manufactures a squad-wide contact at any range, unpriced |
| 3 | assault units useless at long range | the `assault` weapon-identity branch and a one-size standoff |
| 4 | NPCs do not run from grenades | no grenade sense exists at all |
| 5 | NPCs walk in a line, not a formation | the wedge is a destination, not a march |

## Global Constraints

- **Never run `git commit`.** Sebastian reviews and commits. Every phase ends with a build-and-test
  verification step instead of a commit step. This overrides the sub-skill's commit steps.
- `Common` must not reference Stride, and must not reference `Server`. All scoring goes in
  `Common/Ai/` and tests headlessly.
- **Do not add a per-weapon `ItemType` branch, and do not add a hand-written priority ladder.** Both
  are what this plan removes. If a behaviour seems to need one, the cost model is wrong — say so and
  stop rather than encoding the special case.
- Assert **properties of the model**, never traces through an implementation. See the design-method
  section in `CLAUDE.md`.
- Run `dotnet build DemiurgeSharp.slnx` and
  `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"` at the end of
  every phase, plus the integration tier where the phase says so.
- Three integration tests fail before this plan starts and must not get worse:
  `EachConquestTeamCapturesBothCentralFlagsWithoutStuckRelocation` (both teams, a stale
  `flags.Length == 4` assertion) and `NpcExcavatesOutOfADeepWidePit`.
- Per-tick cost matters: the reference machine owes 33 ms per server tick. Strategic planning runs at
  1 Hz and may be generous; anything per-actor per-tick may not.

## File Structure

- **Create** `Common/Ai/StrategicValue.cs` — flag worth, in tickets/second. Pure.
- **Create** `Common/Ai/ThreatResponse.cs` — whether a threat is worth answering. Pure.
- **Create** `Common/Ai/GrenadeDanger.cs` — blast risk at a position and where to go. Pure.
- **Modify** `Common/Ai/StrategicObjectivePlanner.cs` — allocation by value, not by ladder.
- **Modify** `Common/Ai/WeaponEffectiveness.cs` — add `PreferredRange`.
- **Modify** `Server/Ai/SquadTactics.cs` — per-weapon standoff.
- **Modify** `Server/MobSystem.cs` — delete `assault`; price the gunshot/incoming-fire response;
  add the evade-blast intent.
- **Modify** `Server/Ai/ActorIntent.cs` — `EvadeBlast` case.
- **Modify** `Server/GrenadeSystem.cs` — expose live grenades.
- **Modify** `Server/Ai/PathFollower.cs` — wedge offset as steering displacement.
- **Test** `Common.Tests/StrategicValueTests.cs`, `Common.Tests/ThreatResponseTests.cs`,
  `Common.Tests/GrenadeDangerTests.cs`, `Common.Tests/WeaponDispersionTests.cs`,
  `Server.Tests/SquadTacticsTests.cs`.

---

## Phase 1: Strategic value in tickets per second

**Fixes:** squads pile onto 1-2 flags and never take free ground; squads oscillate between flags.

**Why the current code does that.** `StrategicObjectivePlanner.SlotPriority` returns discrete
priority classes. A *second* squad at a contested flag scores 800; a *first* squad at an undefended
neutral flag scores 600. So reinforcing a fight always outbids taking free ground — and because
`contested` is defined as `FriendlyPresence > 0 && EnemyPresence > 0`, sending a squad is what keeps
the flag contested. That is the self-reinforcing loop. The same table oscillates: `contested` and
`threatenedFriendly` are step functions of presence counts, one man crossing a capture radius flips a
flag between 950 and 600, and the only damping is `ReassignmentBiasMetres = 15f` applied to
*distance*, which cannot damp a priority-class flip.

**Files:**
- Create: `Common/Ai/StrategicValue.cs`
- Create: `Common.Tests/StrategicValueTests.cs`
- Modify: `Common/Ai/StrategicObjectivePlanner.cs` (replace `SlotPriority` and `EffectiveDistance`)

**Interfaces:**
- Produces: `StrategicValue.TicketsPerSecondPerFlag`, `StrategicValue.Swing(int team, in StrategicFlag flag)`,
  `StrategicValue.Marginal(int team, in StrategicFlag flag, int squadsAlreadyAssigned, float travelSeconds)`.

- [ ] **Step 1: Write the failing tests**

Create `Common.Tests/StrategicValueTests.cs`:

```csharp
using Demiurge;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the strategic cost model. Not a trace through the allocator: each of these is a
/// statement about what a flag is WORTH that must survive any allocator written over it.
/// </summary>
public class StrategicValueTests
{
    private const int Us = 1;
    private const int Them = 2;

    private static StrategicFlag Flag(
        int owner,
        int friendly = 0,
        int enemy = 0,
        uint id = 1)
        => new(id, new System.Numerics.Vector3(0f, 0f, 0f), owner, FlagConfig.NeutralTeam, 0f, friendly, enemy);

    /// <summary>
    /// The bug, as a property. Taking an undefended flag flips a whole flag of bleed; adding a
    /// second squad to a flag the first already secures flips nothing. Any model where the second
    /// outranks the first produces the mass loop that started this.
    /// </summary>
    [Fact]
    public void ASecondSquadOnASecuredFlagIsWorthLessThanAFirstOnAFreeOne()
    {
        float reinforce = StrategicValue.Marginal(
            Us, Flag(Us, friendly: 4), squadsAlreadyAssigned: 1, travelSeconds: 5f);
        float takeFree = StrategicValue.Marginal(
            Us, Flag(Them), squadsAlreadyAssigned: 0, travelSeconds: 30f);

        Assert.True(
            takeFree > reinforce,
            $"free ground {takeFree} must outbid reinforcement {reinforce}");
    }

    /// <summary>An enemy flag moves the differential by two — they lose one and we gain one — while
    /// a neutral one moves it by one. Derived from ConquestConfig.BleedFor, not picked.</summary>
    [Fact]
    public void TakingAnEnemyFlagIsWorthTwiceTakingANeutralOne()
    {
        Assert.Equal(2f * StrategicValue.Swing(Us, Flag(FlagConfig.NeutralTeam)),
                     StrategicValue.Swing(Us, Flag(Them)),
                     4);
    }

    /// <summary>Value falls off with how long it takes to start paying, and never goes negative —
    /// a distant flag is worth less, not worth avoiding.</summary>
    [Fact]
    public void ValueDecaysWithTravelAndStaysPositive()
    {
        float near = StrategicValue.Marginal(Us, Flag(Them), 0, travelSeconds: 5f);
        float far = StrategicValue.Marginal(Us, Flag(Them), 0, travelSeconds: 120f);

        Assert.True(near > far);
        Assert.True(far > 0f, "a far flag is worth less, not worth avoiding");
    }

    /// <summary>
    /// Continuity is what kills the oscillation. One man stepping into a capture radius must move
    /// the value a little, never reclassify the flag — a step function over a noisy input is a
    /// reassignment generator.
    /// </summary>
    [Fact]
    public void ValueIsContinuousInPresence()
    {
        float previous = StrategicValue.Marginal(Us, Flag(Them, enemy: 0), 0, 20f);
        for (int enemy = 1; enemy <= 8; enemy++)
        {
            float next = StrategicValue.Marginal(Us, Flag(Them, enemy: enemy), 0, 20f);
            Assert.True(
                MathF.Abs(next - previous) < StrategicValue.TicketsPerSecondPerFlag,
                $"presence {enemy} jumped the value by {MathF.Abs(next - previous)}");
            previous = next;
        }
    }

    /// <summary>Defending something about to be lost is worth as much as taking it back, because it
    /// is the same two-flag swing — and it is worth it sooner.</summary>
    [Fact]
    public void HoldingAThreatenedFlagIsWorthAsMuchAsRetakingIt()
    {
        float defend = StrategicValue.Swing(Us, Flag(Us, friendly: 1, enemy: 3));
        float retake = StrategicValue.Swing(Us, Flag(Them, enemy: 3));

        Assert.Equal(retake, defend, 4);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~StrategicValueTests"`

Expected: FAIL to compile — `StrategicValue` does not exist.

- [ ] **Step 3: Write `StrategicValue`**

Create `Common/Ai/StrategicValue.cs`:

```csharp
namespace Demiurge;

/// <summary>
/// What a flag is worth, in TICKETS PER SECOND.
///
/// The currency is not a modelling choice — it is the win condition. `ConquestConfig.BleedFor`
/// charges a team one ticket every `TicketBleedSeconds` for every flag it is behind by, so one net
/// flag is worth exactly `1 / TicketBleedSeconds` tickets per second and nothing else on the map is
/// worth anything except through that. It is also the same unit combat is in once converted: a body
/// costs one ticket at `TicketSystem.ChargeRespawn`, so health per second divided by health per
/// death IS tickets per second. Strategy, combat and movement therefore share one scale.
///
/// This replaces a ladder of discrete priorities keyed on presence booleans. Two things went wrong
/// with that and both are structural rather than tuning:
///
///  - the second squad sent to a contested flag scored above the first squad sent to an empty one,
///    and sending squads is what made a flag contested — a loop that concentrated the whole force on
///    one or two objectives and never took free ground;
///  - the classes were step functions of a noisy input, so one man crossing a capture radius
///    reclassified a flag and reshuffled every assignment.
///
/// Both disappear from a continuous marginal value rather than being tuned out of a table.
/// </summary>
public static class StrategicValue
{
    /// <summary>One net flag of advantage, as a rate. Straight out of the bleed rule.</summary>
    public static float TicketsPerSecondPerFlag => 1f / ConquestConfig.TicketBleedSeconds;

    /// <summary>
    /// How far ahead the commander values. A flag that takes this long to secure is worth about half
    /// what an instant one is; it is the horizon of the discount below, not a deadline.
    ///
    /// Sized against a round rather than picked: 300 tickets bleeding at one per three seconds per
    /// flag is many minutes, and a squad crosses this map in well under two.
    /// </summary>
    public const float PlanningHorizonSeconds = 60f;

    /// <summary>
    /// Expected seconds of fighting per enemy already on a flag, added to the time before it starts
    /// paying. Deliberately smooth: it is what makes a defended flag less attractive than an empty
    /// one WITHOUT reclassifying it as "contested".
    /// </summary>
    public const float SecondsPerDefender = 8f;

    /// <summary>
    /// How many flags of bleed differential taking this flag moves.
    ///
    /// Two for an enemy-held flag, because they lose one and we gain one; one for a neutral. A
    /// friendly flag with enemies on it is the same two-flag swing seen from the other end — losing
    /// it would cost exactly what retaking it would gain — which is why defence needs no separate
    /// priority class to outrank an attack.
    /// </summary>
    public static float Swing(int team, in StrategicFlag flag)
    {
        if (flag.OwnerTeam == team)
            // Only worth something if somebody is actually threatening it. A quiet rear flag is not
            // in danger of changing hands, so holding it swings nothing.
            return flag.EnemyPresence > 0 || IsEnemyCapturing(team, flag) ? 2f : 0f;
        return flag.OwnerTeam == FlagConfig.NeutralTeam ? 1f : 2f;
    }

    private static bool IsEnemyCapturing(int team, in StrategicFlag flag)
        => flag.CapturingTeam != FlagConfig.NeutralTeam && flag.CapturingTeam != team;

    /// <summary>
    /// What assigning one MORE squad to this flag is worth, in tickets per second.
    ///
    /// Marginal, not total, and that is the whole fix. The first squad on an undefended flag converts
    /// the entire swing; the second converts whatever the first left, which on a flag already being
    /// taken is nearly nothing. Diminishing returns are produced by the model rather than by a
    /// hand-written rule that two squads per objective is enough.
    /// </summary>
    public static float Marginal(
        int team,
        in StrategicFlag flag,
        int squadsAlreadyAssigned,
        float travelSeconds)
    {
        float swing = Swing(team, flag);
        if (swing <= 0f) return 0f;

        // Securing takes travel, then the capture clock, then however long the defenders last.
        float seconds = MathF.Max(0f, travelSeconds)
            + FlagConfig.CaptureSeconds
            + MathF.Max(0, flag.EnemyPresence) * SecondsPerDefender;

        // Value now versus value later, as a smooth discount. Never negative and never zero, so a
        // distant flag is merely worth less rather than actively avoided.
        float discounted = swing * TicketsPerSecondPerFlag
            * (PlanningHorizonSeconds / (PlanningHorizonSeconds + seconds));

        // The nth squad gets what the first n-1 left on the table. Halving per squad is the simplest
        // form with the property that matters — strictly decreasing, never negative — and the
        // capture cap makes anything past a couple of squads genuinely idle.
        return discounted / (1 << Math.Clamp(squadsAlreadyAssigned, 0, 8));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~StrategicValueTests"`

Expected: PASS, 5 tests.

- [ ] **Step 5: Replace the ladder in `StrategicObjectivePlanner`**

In `Common/Ai/StrategicObjectivePlanner.cs`, delete `SlotPriority` and `EffectiveDistance` entirely
and replace the `ObjectiveSlot`/`pendingSlots` allocation loop with a greedy assignment by value.
Replace the whole body of `Plan` after the `uniqueFlags.Length == 0` guard with:

```csharp
        // Greedy by marginal value: repeatedly take the (squad, flag) pair worth the most and commit
        // it, which naturally spreads squads once a flag's marginal value has been consumed. The old
        // code sorted discrete priority classes and then broke ties on distance, which is what let a
        // second squad at a fight outrank a first squad at free ground.
        var available = uniqueSquads.ToList();
        var assignedPerFlag = uniqueFlags.ToDictionary(flag => flag.FlagId, _ => 0);
        var assignments = new List<StrategicAssignment>(uniqueSquads.Count);

        while (available.Count > 0)
        {
            float bestValue = float.NegativeInfinity;
            int bestSquad = -1;
            uint bestFlag = 0;

            for (int squadIndex = 0; squadIndex < available.Count; squadIndex++)
                foreach (var flag in uniqueFlags)
                {
                    float value = StrategicValue.Marginal(
                        team,
                        flag,
                        assignedPerFlag[flag.FlagId],
                        TravelSeconds(available[squadIndex], flag));

                    // Staying put is worth the travel it saves. A switching margin in the SAME unit
                    // as the value, rather than the old 15 m distance nudge, which could not damp a
                    // priority-class flip and so let squads oscillate.
                    if (available[squadIndex].CurrentFlagId == flag.FlagId)
                        value += CommitmentBonus;

                    // Deterministic ordering: ties resolve by flag then squad id, never by
                    // enumeration order, so the same situation always produces the same plan.
                    if (value > bestValue
                        || value == bestValue
                        && (flag.FlagId < bestFlag
                            || flag.FlagId == bestFlag
                            && available[squadIndex].SquadId < available[bestSquad].SquadId))
                    {
                        bestValue = value;
                        bestSquad = squadIndex;
                        bestFlag = flag.FlagId;
                    }
                }

            if (bestSquad < 0) break;
            assignments.Add(new StrategicAssignment(available[bestSquad].SquadId, bestFlag));
            assignedPerFlag[bestFlag]++;
            available.RemoveAt(bestSquad);
        }

        return assignments;
    }

    /// <summary>
    /// What a squad gives up by changing its mind, in tickets per second.
    ///
    /// Hysteresis has to be in the SAME unit as the value or it cannot hold against a change in it.
    /// The old bias was 15 metres, applied to a distance that only broke ties between equal priority
    /// classes — so a flag flipping class reassigned the squad regardless and the two flipped back
    /// and forth. A tenth of a flag is small enough that a genuinely better objective still wins.
    /// </summary>
    public const float CommitmentBonus = 0.1f;

    /// <summary>Rough seconds for this squad to reach this flag, at the movement solver's walk
    /// speed — the same time axis navigation prices routes in.</summary>
    private static float TravelSeconds(in StrategicSquad squad, in StrategicFlag flag)
    {
        float dx = squad.Home.X - flag.Position.X;
        float dz = squad.Home.Z - flag.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz) / PlayerMovement.WalkSpeed;
    }
```

Delete the now-unused `ObjectiveSlot` record and the `ReassignmentBiasMetres` constant. If anything
outside this file references `ReassignmentBiasMetres`, update it to `CommitmentBonus` — check with
`grep -rn "ReassignmentBiasMetres" --include=*.cs .`

- [ ] **Step 6: Fix the tests that pinned the old ladder**

Run: `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`

`Common.Tests/StrategicObjectivePlannerTests.cs` has five tests and they were written against the
ladder. For each failure, decide deliberately:

- if it asserts a **property** ("a threatened flag is defended before a quiet one is garrisoned"),
  keep it — the new model must satisfy it, and if it does not, the model is wrong;
- if it asserts the **ladder** ("this flag gets priority 950"), delete it and record why in the test
  file. A test written against a heuristic obstructs the general system that replaces it.

Do not tune `PlanningHorizonSeconds`, `SecondsPerDefender` or `CommitmentBonus` to make a test pass
without first deciding which of the two kinds it is.

- [ ] **Step 7: Verify and stop for review**

Run:
```bash
dotnet build DemiurgeSharp.slnx
dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"
dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"
```
Expected: build clean; fast suite green; **integration may move** — this phase changes strategy
deliberately. Report which scenarios changed and how, and do not adjust constants to restore them
without saying so. Do not commit.

Ask Sebastian to watch a singleplayer round with `ai track ids` on and confirm that a squad now
peels off toward an undefended flag, and that assignments stop flickering.

---

## Phase 2: Price the response to being shot at

**Fixes:** a squad drops its objective and walks 200 m at a sniper it cannot reach.

**Why the current code does that.** `MobSystem.ProcessIncomingFire` calls
`brain.Contacts.Observe(...)` and `Publish(...)` on any near miss, with **no range or value test**.
That manufactures a squad-wide believed threat at any distance. `CombatBehavior` then sets
`ShouldCloseDistance = true` precisely BECAUSE the weapon has zero DPS at that range, which feeds
`wantsAdvance`, which authorises the advance. Being unable to shoot back is what makes them charge.

**Files:**
- Create: `Common/Ai/ThreatResponse.cs`
- Create: `Common.Tests/ThreatResponseTests.cs`
- Modify: `Server/MobSystem.cs` (`ProcessIncomingFire`, and the `wantsAdvance` term)

**Interfaces:**
- Consumes: `StrategicValue.TicketsPerSecondPerFlag` from Phase 1.
- Produces: `ThreatResponse.IsWorthAnswering(in Combatant self, in Engagement threat, float objectiveValue)`
  and `ThreatResponse.TicketsPerSecond(float healthPerSecond, int maximumHealth)`.

- [ ] **Step 1: Write the failing tests**

Create `Common.Tests/ThreatResponseTests.cs`:

```csharp
using Demiurge;

namespace Demiurge.Tests;

public class ThreatResponseTests
{
    private static Combatant Self(ItemType weapon) => new(weapon, 0f, 1f);

    private static Engagement At(float range, ItemType theirWeapon)
        => new(range, theirWeapon, 0f, TargetExposure.Full, SelfExposure.Full, 1f);

    /// <summary>The reported bug, as a property: a shooter you cannot reach, who is barely hurting
    /// you, does not buy your objective off you.</summary>
    [Fact]
    public void ADistantHarasserIsNotWorthAbandoningAnObjectiveFor()
    {
        Assert.False(ThreatResponse.IsWorthAnswering(
            Self(ItemType.Ppsh),
            At(200f, ItemType.Mosin),
            objectiveValue: StrategicValue.TicketsPerSecondPerFlag));
    }

    /// <summary>And the other half, which matters just as much: a man shooting at you from across
    /// the street is worth everything you were doing.</summary>
    [Fact]
    public void AThreatInsideItsOwnKillingRangeIsAlwaysWorthAnswering()
    {
        Assert.True(ThreatResponse.IsWorthAnswering(
            Self(ItemType.Ppsh),
            At(15f, ItemType.Ppsh),
            objectiveValue: StrategicValue.TicketsPerSecondPerFlag));
    }

    /// <summary>Answering gets easier as the objective gets cheaper. A squad with nothing better to
    /// do should go and deal with the sniper.</summary>
    [Fact]
    public void AWorthlessObjectiveLowersTheBarToAnswering()
    {
        var self = Self(ItemType.Sks);
        var threat = At(120f, ItemType.Mosin);

        Assert.True(
            ThreatResponse.IsWorthAnswering(self, threat, objectiveValue: 0f)
            || !ThreatResponse.IsWorthAnswering(self, threat, objectiveValue: 10f),
            "the objective's worth must enter the decision at all");
    }

    /// <summary>The bridge between the two currencies. A hundred-health man losing thirty health per
    /// second is dying every 3.3 seconds, and each death costs his team one ticket.</summary>
    [Fact]
    public void HealthPerSecondConvertsToTicketsPerSecondThroughOneTicketPerBody()
    {
        Assert.Equal(0.3f, ThreatResponse.TicketsPerSecond(30f, 100), 4);
        Assert.Equal(0f, ThreatResponse.TicketsPerSecond(0f, 100), 4);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~ThreatResponseTests"`

Expected: FAIL to compile — `ThreatResponse` does not exist.

- [ ] **Step 3: Write `ThreatResponse`**

Create `Common/Ai/ThreatResponse.cs`:

```csharp
namespace Demiurge;

/// <summary>
/// Whether a threat is worth answering, or merely worth taking cover from.
///
/// The distinction did not exist. Any round passing near an NPC published a squad-wide contact at
/// whatever range it came from, and because a weapon that cannot reach yields a zero firing solution
/// — which IS the decision to close — the squad then advanced on a shooter it had no prospect of
/// reaching. Being outranged was what made them charge.
///
/// Both sides are priced in tickets per second, which is what makes them comparable at all:
/// `CombatValue` is in health per second, a body costs one ticket when it respawns, and a flag is
/// worth `StrategicValue.TicketsPerSecondPerFlag`. So "is this shooter worth a flag?" is a real
/// question with a real answer rather than a judgement call encoded as a range constant.
/// </summary>
public static class ThreatResponse
{
    /// <summary>
    /// Health per second, as tickets per second. One body is one ticket at
    /// <c>TicketSystem.ChargeRespawn</c>, so losing a man's whole health is losing a ticket.
    /// </summary>
    public static float TicketsPerSecond(float healthPerSecond, int maximumHealth)
        => maximumHealth <= 0 ? 0f : MathF.Max(0f, healthPerSecond) / maximumHealth;

    /// <summary>
    /// A man is worth this much health, for the conversion above. Kept here rather than read from a
    /// live actor so the model stays pure and a wounded man does not become cheaper to lose.
    /// </summary>
    public const int NominalHealth = 100;

    /// <summary>
    /// Whether answering this threat beats carrying on with the objective.
    ///
    /// Answering is worth what it stops him taking from us. Carrying on is worth the objective. Both
    /// in tickets per second, so this is a comparison rather than a rule.
    /// </summary>
    public static bool IsWorthAnswering(
        in Combatant self,
        in Engagement threat,
        float objectiveValue)
        => TicketsPerSecond(CombatValue.Taken(self, [threat]), NominalHealth)
           > MathF.Max(0f, objectiveValue);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~ThreatResponseTests"`

Expected: PASS, 4 tests.

If `ADistantHarasserIsNotWorthAbandoningAnObjectiveFor` fails, do NOT add a range cutoff. It means
`CombatValue.Taken` reports a distant bolt-action as more dangerous than a flag is valuable, which is
a statement about the dispersion curve worth investigating on its own — report it and stop.

- [ ] **Step 5: Gate the squad-wide publish in `MobSystem.ProcessIncomingFire`**

In `Server/MobSystem.cs`, inside `ProcessIncomingFire`, replace:

```csharp
                brain.MarkUnderFire(tick);
                brain.NextCoverQueryTick = tick;
                brain.Contacts.Observe(
                    suppression.ShooterId,
                    suppression.ThreatPosition,
                    suppression.Tick);
                BoardFor(listener, brain).Publish(
```

with:

```csharp
                // Being shot at is always true and always worth cover, whoever is doing it.
                brain.MarkUnderFire(tick);
                brain.NextCoverQueryTick = tick;

                // Being shot at by somebody worth walking to is not. A harasser beyond the range at
                // which he can meaningfully hurt this squad becomes a reason to get down, not a
                // reason to abandon an objective and cross the map — see ThreatResponse.
                var selfCombatant = new Combatant(
                    weapons.TryGetPrimaryWeapon(listener, out var listenerWeapon)
                        ? listenerWeapon.Item.Type
                        : ItemConfig.UnidentifiedThreatWeapon,
                    0f,
                    brain.SkillFactor);
                var incoming = new Engagement(
                    HorizontalDistance(listener.Position, suppression.ThreatPosition),
                    ItemConfig.UnidentifiedThreatWeapon,
                    0f,
                    TargetExposure.Full,
                    brain.SelfExposure,
                    1f);
                if (!ThreatResponse.IsWorthAnswering(
                        selfCombatant,
                        incoming,
                        StrategicValue.TicketsPerSecondPerFlag))
                    continue;

                brain.Contacts.Observe(
                    suppression.ShooterId,
                    suppression.ThreatPosition,
                    suppression.Tick);
                BoardFor(listener, brain).Publish(
```

- [ ] **Step 6: Apply the same gate to heard gunshots**

`ProcessGunshots` has the same shape at a 60 m hearing radius: it sets an investigation goal that
clears `brain.ObjectiveReached` and re-points navigation. Leave the hearing itself alone — turning to
look is free and correct — but in `Decide`, the branch that re-points navigation at
`brain.HeardPosition` must not fire for a threat that is not worth answering. Guard the
`heardGunshot && brain.AppliedHeardRevision != brain.HeardRevision` block with the same
`ThreatResponse.IsWorthAnswering` call, using `brain.HeardPosition` as the threat position.

- [ ] **Step 7: Verify and stop for review**

Run the build, fast suite, and integration tier as in Phase 1 Step 7. Behaviour changes
deliberately; report any integration movement. Do not commit.

Ask Sebastian to shoot at a squad from well outside their range and confirm they go to ground and
keep their objective rather than charging, and — equally — that shooting at them from close range
still brings the whole squad onto him.

---

## Phase 3: Derive standoff, delete the assault branch

**Fixes:** assault units useless at long range and unable to leave it.

**Why the current code does that.** `MobSystem.cs:409` computes
`bool assault = activePrimary.Item.Type == NpcSquadLoadout.AssaultWeapon` — a live weapon-identity
branch, one indirection deeper than the `ItemType.Ppsh` tests that were removed. It feeds
`assaultWaitingInPosition`, which sets `mayAdvance = false` for an SMG man assigned to the base of
fire: parked at a range where his weapon does nothing. `SquadTactics.MinimumStandoff = 12f` is one
constant for every weapon, so his assault is priced against a fixed destination rather than where his
own weapon peaks.

**Files:**
- Modify: `Common/Ai/WeaponEffectiveness.cs` (add `PreferredRange`)
- Modify: `Common.Tests/WeaponDispersionTests.cs`
- Modify: `Server/Ai/SquadTactics.cs` (per-weapon standoff)
- Modify: `Server/MobSystem.cs` (delete `assault` and its five uses)

**Interfaces:**
- Produces: `WeaponEffectiveness.PreferredRange(ItemType weapon, float skillFactor)`.

- [ ] **Step 1: Write the failing test**

Add to `Common.Tests/WeaponDispersionTests.cs`:

```csharp
/// <summary>
/// Where each weapon wants to be fought, derived from its own damage curve rather than written down
/// per weapon. The ordering is the doctrine: an SMG closes, a bolt gun holds — and nothing in the AI
/// has to know which is which.
/// </summary>
[Fact]
public void PreferredRangeOrdersWeaponsFromSubmachineGunToBoltAction()
{
    float smg = WeaponEffectiveness.PreferredRange(ItemType.Ppsh, 1f);
    float carbine = WeaponEffectiveness.PreferredRange(ItemType.Sks, 1f);
    float rifle = WeaponEffectiveness.PreferredRange(ItemType.Mosin, 1f);

    Assert.True(smg < carbine, $"smg {smg} should want to be closer than carbine {carbine}");
    Assert.True(carbine < rifle, $"carbine {carbine} should want to be closer than rifle {rifle}");
    Assert.True(smg > 0f);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~PreferredRangeOrders"`

Expected: FAIL to compile — `PreferredRange` does not exist.

- [ ] **Step 3: Add `PreferredRange`**

Add to `Common/Ai/WeaponEffectiveness.cs`:

```csharp
    /// <summary>How far apart this weapon samples the curve when looking for its own best range.</summary>
    private const float PreferredRangeStepMetres = 2f;
    private const float PreferredRangeMaximumMetres = 400f;

    /// <summary>
    /// The range at which this weapon is worth the most, in metres.
    ///
    /// Sampled off the same curve everything else uses rather than written down per weapon, which is
    /// the point: "the SMG man closes and the rifleman holds" stops being doctrine anybody
    /// implements and becomes where two numbers peak. Every weapon peaks somewhere — damage per
    /// second is rate times hit probability, rate is flat in range and hit probability falls, so the
    /// maximum is at the closest range the model is defined for unless cadence and dispersion
    /// interact, which they do through MinimumExpectedDamagePerRound.
    ///
    /// Sampled rather than solved because the rate choice inside <see cref="Best"/> is discrete, so
    /// the curve is piecewise and has no closed form worth deriving. It is called per squad plan at
    /// 2 Hz, not per actor per tick.
    /// </summary>
    public static float PreferredRange(ItemType weapon, float skillFactor)
    {
        float bestRange = PreferredRangeStepMetres;
        float bestValue = -1f;
        for (float range = PreferredRangeStepMetres;
             range <= PreferredRangeMaximumMetres;
             range += PreferredRangeStepMetres)
        {
            float value = Best(weapon, range, TargetExposure.Full, extraMoa: 0f, skillFactor)
                .DamagePerSecond;
            // Strictly greater, so the NEAREST range achieving the maximum wins a plateau — a weapon
            // that is equally good at 10 m and 30 m should be fought at 10 m, where a miss still
            // threatens.
            if (value > bestValue)
            {
                bestValue = value;
                bestRange = range;
            }
        }
        return bestRange;
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~WeaponDispersionTests"`

Expected: PASS.

If the ordering assertion fails, the peak is at the sampling floor for every weapon because damage
per second is monotonically decreasing in range for all of them. In that case the honest fix is to
use the range at which the weapon still achieves a fixed FRACTION of its peak — replace the body
with a scan for the largest range where `DamagePerSecond >= 0.5f * peak` and re-run. Report which
form you used.

- [ ] **Step 5: Make the standoff per-weapon in `SquadTactics`**

In `Server/Ai/SquadTactics.cs`, `MinimumStandoff` is used in three places (the destination estimate,
the transit midpoint, and a floor in the bound destination). Replace the constant with a per-member
value computed once in `Plan`:

```csharp
            // Where THIS man's weapon is worth the most, not a single number for the squad. A
            // 12 metre standoff priced a bolt gun's assault at a range it does not want and an SMG's
            // at one it has already won at.
            float standoff = WeaponEffectiveness.PreferredRange(member.Weapon, member.SkillFactor);
```

and use `standoff` in place of `MinimumStandoff` at all three sites within the per-member loop. Keep
the constant, renamed and documented as the floor it still is:

```csharp
    /// <summary>Closest a plan will deliberately place a man, whatever his weapon prefers. Below
    /// this the movement solver and the cover query are fighting over the same metre of ground.</summary>
    public const float ClosestPlannedStandoff = 4f;
```

Clamp with `MathF.Max(ClosestPlannedStandoff, standoff)`.

- [ ] **Step 6: Delete the `assault` branch**

In `Server/MobSystem.cs`, delete the `bool assault = ...` declaration at line 409 and all five uses.
Specifically:

- `assaultWaitingInPosition` and its use in `mayAdvance` go entirely. Whether a man advances is the
  allocation's decision, and `SquadTactics` already makes it by score — a second veto keyed on weapon
  type is the two-authorities mistake `ActorIntent` exists to prevent.
- `UpdateBoundMovement`, `UpdateCoverMovement` and `CompleteBound` take an `assault` parameter; remove
  it from all three signatures and from their bodies. Where a body branches on it, the replacement is
  the member's own `WeaponEffectiveness.PreferredRange`, not a different boolean.

Run `grep -n "assault" Server/MobSystem.cs` afterwards; the only hits should be in comments
describing history.

- [ ] **Step 7: Verify and stop for review**

Build, fast suite, integration tier. Report movement. Do not commit.

Ask Sebastian whether PPSH units now close on defenders instead of standing off, and whether rifle
units still hold rather than following them in.

---

## Phase 4: Grenade danger sense

**Fixes:** NPCs do not run from grenades.

**Why the current code does that.** There is no reference to `ObjectType.Grenade` anywhere in
`Server/Ai`. Grenades are replicated objects with live positions and a three-second fuse and the
server holds them in `GrenadeSystem.active` — the information exists and nothing reads it. This is a
missing sense input, not a broken decision.

**Files:**
- Create: `Common/Ai/GrenadeDanger.cs`
- Create: `Common.Tests/GrenadeDangerTests.cs`
- Modify: `Server/GrenadeSystem.cs` (expose live grenades)
- Modify: `Server/Ai/ActorIntent.cs` (`EvadeBlast` case)
- Modify: `Server/MobSystem.cs` (`Decide`)

**Interfaces:**
- Produces: `readonly record struct LiveBlast(Vector3 Position, float Seconds, BlastProfile Blast)`,
  `GrenadeDanger.Evaluate(Vector3 position, IReadOnlyList<LiveBlast> blasts, out Vector3 away)`,
  `GrenadeSystem.LiveBlasts(uint tick)`.

- [ ] **Step 1: Write the failing tests**

Create `Common.Tests/GrenadeDangerTests.cs`:

```csharp
using System.Numerics;
using Demiurge;

namespace Demiurge.Tests;

public class GrenadeDangerTests
{
    private static LiveBlast At(float x, float seconds = 1.5f)
        => new(new Vector3(x, 0f, 0f), seconds, GrenadeConfig.Blast);

    [Fact]
    public void AGrenadeAtYourFeetIsLethalAndPointsYouAway()
    {
        float risk = GrenadeDanger.Evaluate(Vector3.Zero, [At(1f)], out var away);

        Assert.True(risk > 0f);
        Assert.True(away.X < 0f, "away from the grenade, not toward it");
        Assert.Equal(0f, away.Y);
    }

    [Fact]
    public void AGrenadeBeyondItsDamageRadiusIsNotWorthMoving()
        => Assert.Equal(
            0f,
            GrenadeDanger.Evaluate(
                Vector3.Zero,
                [At(GrenadeConfig.DamageRadius + 1f)],
                out _));

    /// <summary>
    /// A grenade you cannot get clear of before it goes off is not worth running from — the running
    /// is what leaves cover. Time to detonate has to enter the answer or NPCs will sprint into the
    /// open for a fuse that expires first.
    /// </summary>
    [Fact]
    public void AGrenadeAboutToDetonateIsNotWorthRunningFrom()
    {
        float plenty = GrenadeDanger.Evaluate(Vector3.Zero, [At(2f, seconds: 2.5f)], out _);
        float none = GrenadeDanger.Evaluate(Vector3.Zero, [At(2f, seconds: 0.05f)], out _);

        Assert.True(plenty > none);
    }

    /// <summary>Two grenades either side must not average into "stand still".</summary>
    [Fact]
    public void SurroundedByTwoBlastsStillProducesAnEscapeDirection()
    {
        float risk = GrenadeDanger.Evaluate(
            Vector3.Zero,
            [At(2f), new LiveBlast(new Vector3(-2f, 0f, 0f), 1.5f, GrenadeConfig.Blast)],
            out var away);

        Assert.True(risk > 0f);
        Assert.True(away.LengthSquared() > 0.5f, "an escape direction, not a cancelled-out zero");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~GrenadeDangerTests"`

Expected: FAIL to compile — `GrenadeDanger` and `LiveBlast` do not exist.

- [ ] **Step 3: Write `GrenadeDanger`**

Create `Common/Ai/GrenadeDanger.cs`:

```csharp
using System.Numerics;

namespace Demiurge;

/// <summary>One grenade in the world, as the AI needs to see it.</summary>
/// <param name="Seconds">Until it goes off. Negative or zero means it already has.</param>
public readonly record struct LiveBlast(Vector3 Position, float Seconds, BlastProfile Blast);

/// <summary>
/// How much trouble a man is in from live grenades, and which way is out.
///
/// Risk is the fraction of a body the blast takes, which is <see cref="BlastProfile.DamageFraction"/>
/// — the same function the server uses to hurt him, so an NPC cannot be afraid of a grenade that
/// would not have hurt it, or calm about one that would.
///
/// Time is part of the answer rather than a separate gate. Running costs cover and takes seconds; a
/// fuse with no time left cannot be outrun, so the honest response to it is to stay put rather than
/// to sprint into the open and be caught standing.
/// </summary>
public static class GrenadeDanger
{
    /// <summary>How fast a man gets clear, for deciding whether running is worth it. Walk speed
    /// rather than sprint: he is diving away from his feet, not setting off on a journey.</summary>
    public static float EscapeSpeed => PlayerMovement.WalkSpeed;

    /// <summary>
    /// The worst fraction of a body the live blasts will take at <paramref name="position"/>,
    /// discounted by whether there is time to get out of it, and the direction to go.
    ///
    /// Zero means stay where you are — either nothing is close enough, or nothing can be escaped.
    /// </summary>
    public static float Evaluate(
        Vector3 position,
        IReadOnlyList<LiveBlast> blasts,
        out Vector3 away)
    {
        away = Vector3.Zero;
        float worst = 0f;

        for (int i = 0; i < blasts.Count; i++)
        {
            var blast = blasts[i];
            var delta = position - blast.Position;
            delta.Y = 0f;
            float distance = delta.Length();

            float fraction = blast.Blast.DamageFraction(distance);
            if (fraction <= 0f) continue;

            // How much of the way out he can cover before it goes off. No time, no point running.
            float reachable = MathF.Max(0f, blast.Seconds) * EscapeSpeed;
            float escapable = blast.Blast.DamageRadius - distance;
            float feasibility = escapable <= 0f
                ? 0f
                : Math.Clamp(reachable / escapable, 0f, 1f);

            float risk = fraction * feasibility;
            if (risk <= 0f) continue;

            worst = MathF.Max(worst, risk);

            // Weighted by risk and summed, so two grenades either side push him out sideways rather
            // than cancelling to a stand. A man exactly between two is the case that has to produce
            // an answer, not a zero.
            var escape = distance > 1e-3f
                ? delta / distance
                : new Vector3(1f, 0f, 0f);
            away += escape * risk;
        }

        if (away.LengthSquared() > 1e-6f)
            away = Vector3.Normalize(away);
        else if (worst > 0f)
            // Perfectly balanced: any direction beats standing on it.
            away = new Vector3(1f, 0f, 0f);

        return worst;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~GrenadeDangerTests"`

Expected: PASS, 4 tests.

- [ ] **Step 5: Expose live grenades from `GrenadeSystem`**

Add to `Server/GrenadeSystem.cs`:

```csharp
    /// <summary>
    /// Every grenade still in the air or on the ground, as the AI needs to see them.
    ///
    /// Server-side only and deliberately not replicated: a client already sees the grenade object,
    /// and what an NPC knows about it is a server decision. Rebuilt per call rather than cached
    /// because there are rarely more than a handful and a stale list would have NPCs dodging a
    /// grenade that already went off.
    /// </summary>
    public List<LiveBlast> LiveBlasts(uint tick)
    {
        var live = new List<LiveBlast>(active.Count);
        foreach (var grenade in active)
            live.Add(new LiveBlast(
                grenade.Position,
                (grenade.DetonateTick - (float)tick) / NetworkConfig.TickRate,
                GrenadeConfig.Blast));
        return live;
    }
```

`MortarSystem` deliberately does not contribute: a bomb in the air gives no warning a man could act
on, and the shell whistle that would justify one does not exist yet.

- [ ] **Step 6: Add the `EvadeBlast` intent**

Add to `Server/Ai/ActorIntent.cs`:

```csharp
    /// <summary>
    /// Get out of a blast radius. Outranks everything, including a squad manoeuvre: a bound that
    /// walks into a grenade is not a bound anybody wanted, and the squad would rather have the man.
    /// </summary>
    internal sealed record EvadeBlast(Vector3 Away) : ActorIntent
    {
        internal override string DebugLabel => "EVADE";
    }
```

- [ ] **Step 7: Consume it in `Decide`**

In `Server/MobSystem.cs`, `MobSystem` needs the grenade system. It is constructed after
`GrenadeSystem` in `GameWorld`, so pass it in — add a `GrenadeSystem grenades` constructor parameter
and field, and have `GameWorld` supply the instance it already owns.

At the top of `Decide`, immediately after the brain is resolved and BEFORE the grenade-throw branch,
add the evade check. Place the blast list on `BeginTick` rather than per actor, so 32 NPCs share one
build:

```csharp
        // Built once per tick, in BeginTick, and read by every actor's Decide. Rebuilding it per
        // actor would be 32 allocations a tick for one answer.
        private List<LiveBlast> liveBlasts = [];
```

In `BeginTick`, before the perception loop:

```csharp
            liveBlasts = grenades.LiveBlasts(tick);
```

In `Decide`, after `var squad = BoardFor(mob, brain);`:

```csharp
            // Before everything, including the squad's manoeuvre. Nothing this man was doing is worth
            // standing in a blast for, and the squad would rather have him.
            if (liveBlasts.Count > 0
                && GrenadeDanger.Evaluate(mob.Position, liveBlasts, out var away)
                    >= GrenadeEvadeThreshold)
            {
                brain.DebugIntent = "EVADE";
                brain.Navigation.Progress.Reset();
                return new MobAction
                {
                    Intent = away,
                    Sprint = true,
                    Yaw = MathF.Atan2(away.X, away.Z),
                    TurnTo = true,
                };
            }
```

with the threshold beside the other tuning constants:

```csharp
        /// <summary>
        /// How much of a man's health a blast has to threaten before he abandons what he was doing.
        ///
        /// A tenth: enough that a grenade landing at the edge of its damage radius does not scatter a
        /// squad that was winning, and low enough that anything genuinely dangerous moves everyone.
        /// </summary>
        private const float GrenadeEvadeThreshold = 0.1f;
```

- [ ] **Step 8: Verify and stop for review**

Build, fast suite, integration tier. Do not commit.

Ask Sebastian to throw a grenade into a defending squad and confirm they scatter rather than stand,
and that they do not scatter for one landing well outside its radius.

---

## Phase 5: Wedge as steering displacement

**Fixes:** NPCs walk in a line over long stretches instead of in formation.

**Why the current code does that.** `MobSystem.ObjectiveDestination` calls `WedgeFormation.Slot(...)`
and uses the result as the navigation DESTINATION, so the formation exists only where they are going.
During the walk each NPC paths independently from where it stands; they all start together, terrain
funnels them, and A* returns near-identical routes. The spec's stage 2 asks for "wedge offsets as
steering displacement" over a shared trunk path — the trunk sharing exists (`SharedRoute*` in
`NavigationSystem`), the displacement does not.

**Files:**
- Modify: `Server/Ai/PathFollower.cs` (`Update` gains a lateral offset)
- Modify: `Server/MobSystem.cs` (supply the offset)
- Test: `Server.Tests/PathFollowerTests.cs`

**Interfaces:**
- Consumes: `WedgeFormation.Slot` (unchanged).
- Produces: `PathFollower.Update(..., Vector3 lateralOffset, ...)`.

- [ ] **Step 1: Write the failing test**

Add to `Server.Tests/PathFollowerTests.cs`:

```csharp
/// <summary>
/// Formation is a property of the march, not of the destination. Two men following one trunk route
/// with opposite offsets must be steered apart while they walk — which is what stops a squad
/// arriving in single file having been a line the whole way.
/// </summary>
[Fact]
public void OppositeLateralOffsetsSteerFollowersApartAlongTheSameRoute()
{
    var path = StraightPath(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 40f));

    var left = new PathFollower();
    var right = new PathFollower();
    left.SetPath(path, terrainVersion: 0, currentPosition: null, terrain: null);
    right.SetPath(path, terrainVersion: 0, currentPosition: null, terrain: null);

    left.Update(new Vector3(0f, 0f, 5f), new Vector3(-4f, 0f, 0f), out var leftIntent, out _, out _);
    right.Update(new Vector3(0f, 0f, 5f), new Vector3(4f, 0f, 0f), out var rightIntent, out _, out _);

    Assert.True(leftIntent.X < 0f, $"left man should be steered left, got {leftIntent}");
    Assert.True(rightIntent.X > 0f, $"right man should be steered right, got {rightIntent}");
}
```

Adapt the helper names to whatever `PathFollowerTests.cs` already uses for building a path and
calling `Update`; read the file first and match it rather than inventing a shape.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~OppositeLateralOffsets"`

Expected: FAIL to compile — `Update` has no lateral-offset parameter.

- [ ] **Step 3: Add the displacement to `PathFollower.Update`**

Add a `Vector3 lateralOffset` parameter, and apply it to the point being steered toward rather than to
the intent:

```csharp
        // Steer toward the waypoint DISPLACED sideways, not toward the waypoint and then sideways.
        // Offsetting the intent turns a follower away from its route and it crabs; offsetting the
        // target keeps the route and moves the lane, which is what a formation is.
        var steerTo = waypoint + lateralOffset;
```

The offset must fade to zero as the follower nears the end of the path, or men converge on the
objective at a four-metre spacing and fight over the capture radius:

```csharp
        // Collapse the formation on arrival: the wedge is for crossing ground, and the objective is
        // one place rather than five.
        float remaining = RemainingDistance();
        float fade = Math.Clamp(remaining / FormationCollapseDistance, 0f, 1f);
        var steerTo = waypoint + lateralOffset * fade;
```

with:

```csharp
    /// <summary>Within this distance of the end of the route the formation closes up.</summary>
    private const float FormationCollapseDistance = 12f;
```

- [ ] **Step 4: Supply the offset from `MobSystem`**

`ObjectiveDestination` keeps computing the wedge slot, but the offset now also applies during
transit. In `Decide`, where the follower is updated, compute the man's lateral offset from his slot:

```csharp
            // The same wedge the destination uses, as a displacement from the route rather than only
            // as a place to end up. Perpendicular to the direction of travel, so it is a lane rather
            // than a fixed compass offset.
            Vector3 lateral = WedgeLateralOffset(mob.Id, brain, squad);
```

```csharp
        /// <summary>
        /// This man's lane, relative to the squad's line of march. Zero for anyone not on a roster —
        /// a lone man has no formation to keep.
        /// </summary>
        private static Vector3 WedgeLateralOffset(ushort mobId, MobBrain brain, SquadBlackboard squad)
        {
            int slot = -1;
            for (int i = 0; i < squad.Roster.Count; i++)
                if (squad.Roster[i] == mobId) { slot = i; break; }
            if (slot <= 0) return Vector3.Zero;

            var toObjective = brain.Navigation.Destination - squad.Centre;
            toObjective.Y = 0f;
            if (toObjective.LengthSquared() < 1e-4f) return Vector3.Zero;

            var forward = Vector3.Normalize(toObjective);
            var right = new Vector3(forward.Z, 0f, -forward.X);

            // Alternating sides, widening with slot: 1 right, 2 left, 3 further right. The same
            // arrangement WedgeFormation makes at the destination, expressed as a lane.
            int rank = (slot + 1) / 2;
            float side = slot % 2 == 1 ? 1f : -1f;
            return right * (side * rank * WedgeFormation.SpacingFor(squad.Roster.Count));
        }
```

Pass `lateral` into every `PathFollower.Update` call in `Decide`. There is more than one; find them
with `grep -n "\.Update(" Server/MobSystem.cs` and pass `Vector3.Zero` from any call site that is not
the objective march (cover and bound movement have their own destinations and must not be laned).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~PathFollowerTests"`

Expected: PASS.

- [ ] **Step 6: Verify and stop for review**

Build, fast suite, integration tier. This phase changes movement, so watch
`MobTraversalIntegrationTests` in particular — a lateral offset that pushes men off a bridge or into a
trench wall shows up there first. If a traversal scenario fails, the offset needs to fade with
terrain difficulty rather than be reduced globally; report before tuning. Do not commit.

Ask Sebastian to watch a squad cross open ground and confirm it moves as a wedge and closes up on
arrival.

---

## Deliberately not in this plan

- **Scored per-unit arbitration.** Still the outstanding stage-1 item: `MobSystem`'s `ActorIntent`
  selection is an ordered ternary and `ActorIntent.HoldAndFire` is never constructed. Phases 2-4 all
  add pricing that the scored form would consume, so doing it after these is strictly easier.
- **Mortar avoidance.** Phase 4 covers grenades only; a bomb in the air gives no warning a man could
  act on until there is a shell whistle.
- **The three failing integration tests.** Two are a stale `flags.Length == 4` assertion and one is a
  pit-excavation regression, both tracked in `docs/TODO.md`.
- **Retuning `aggression`.** Several of these phases change how much NPCs move; the global scalar is
  the right lever afterwards, once the model underneath it is no longer wrong.
