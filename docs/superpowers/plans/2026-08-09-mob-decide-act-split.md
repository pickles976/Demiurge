# Mob Decide/Act Split — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an NPC's output for a tick — `State`, `Yaw`, `Pitch`, `LastIntent`, and the movement solve — be written in exactly ONE place, so no system can silently overwrite another's decision.

**Architecture:** Sense → Decide → Act. Sense already works this way (perception, hearing, squad orders and drained navigation results all write `MobBrain`/`SquadBlackboard`). Decide becomes a function that reads brain + blackboard and returns a `MobAction` value **without touching the actor**. Act is one function that turns a `MobAction` into the actor's state and one `PlayerMovement.Step`. The bug class disappears structurally rather than by discipline: a decider that has no write access cannot overwrite anything.

**Tech Stack:** C# / net10.0, xUnit, `System.Numerics`. No new dependencies.

**Already true, and not in scope:** the squad and strategic layers already propagate downward into
the per-unit blackboard rather than acting on actors — `CommanderAi` writes objectives to
`SquadBlackboard`, `SquadTactics` writes orders to it, and `MobBrain` holds the per-unit half. That
half of the sense/decide/act design is done. What is missing is entirely below it, in how one NPC
turns its blackboard into a tick of output.

## Why this is not a threading fix

`NavigationSystem` is the only thread pool in the AI, and its workers never touch `ServerPlayer` or
`MobBrain` — they consume an immutable `PathRequest`, run a pure search over `ChunkMap`, and post a
plain `NavPath` that `MobSystem.BeginTick` drains on the main thread. **There is no race.** Every
overwrite in this plan happens sequentially inside one call to `MobSystem.Step`. Do not add locks,
queues, or concurrent collections; they would fix nothing and cost the tick.

## The five writers being collapsed

All in `Server/MobSystem.cs`, all inside `Step`, each ending in `return`:

| line | branch | writes |
|---|---|---|
| 366-368 | grenade thrown | `State = Shooting`, `LastIntent = 0`, `StepSolver` |
| 572-578 | combat (bound / cover) | `State = State.With(…)`, `LastIntent`, `StepSolver` |
| 604-610 | `brain.Entrenching` | `State = State.With(…)`, `LastIntent`, `StepSolver` |
| 639-644 | `brain.Entrenched` | `State = None.With(…)`, `LastIntent`, `StepSolver` |
| 808-831 | path following | `State = None.With(…)`, `Yaw`, `Pitch`, `LastIntent`, `StepSolver` |

Plus `Server/Ai/CombatBehavior.cs` lines 150-153, 157, 163 and 246, which write `mob.Yaw`,
`mob.Pitch`, `mob.LastIntent` and `mob.State` **before** any of the above and are then partially
overwritten by them.

**The live bug this produces.** `MobSystem.cs:485` claims "ONE decision, made here and nowhere else.
Everything below reads `intent`; nothing recomputes whether this man may move or shoot." That claim
is false today: when the decision is `ActorIntent.PursueObjective`, control falls past it into the
`brain.Entrenching` and `brain.Entrenched` branches, which move the actor somewhere else entirely.
`brain.DebugIntent` — the label `ai track states` draws over the NPC's head — says `OBJECTIVE` while
the actor is cycling between a foxhole centre and its peek station. Task 4 fixes that; Task 5 pins it.

## Global Constraints

- **Never run `git commit`.** Sebastian reviews and commits. Every task ends with a build-and-test
  verification step instead of a commit step. This overrides the sub-skill's commit steps.
- `Common` must not reference Stride. Anything placed in `Common/` compiles and tests headlessly.
- Behaviour must not change in Tasks 1-3. They are refactors; the integration suite is the check.
- Do not move work onto threads. See "Why this is not a threading fix" above.
- Do not add a per-weapon `ItemType` branch. That layer was deliberately removed; see
  `docs/ARCHITECTURE.md` "The combat currency".
- Run `dotnet build DemiurgeSharp.slnx` and
  `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"` at the end of
  every task.
- Three integration tests fail before this plan starts and must still fail identically, no worse:
  `EachConquestTeamCapturesBothCentralFlagsWithoutStuckRelocation` (both teams, a stale
  `flags.Length == 4` assertion) and `NpcExcavatesOutOfADeepWidePit`.

## File Structure

- **Create** `Server/Ai/MobAction.cs` — the value an actor emits for one tick, and the single
  `Apply`. Server-side, not on the wire.
- **Create** `Server/Ai/CombatOutcome.cs` — what `CombatBehavior` reports instead of writing.
- **Modify** `Server/Ai/CombatBehavior.cs` — return `CombatOutcome`; stop writing the actor.
- **Modify** `Server/MobSystem.cs` — `Step` becomes `Decide` + `Apply`; five exits become five
  `return new MobAction(…)`.
- **Modify** `Server/Ai/ActorIntent.cs` — add the entrenchment case the decision is currently missing.
- **Test** `Server.Tests/CombatBehaviorTests.cs`, `Server.Tests/MobDecideActTests.cs` (new).

---

### Task 1: `CombatBehavior` reports instead of writes

**Files:**
- Create: `Server/Ai/CombatOutcome.cs`
- Modify: `Server/Ai/CombatBehavior.cs:64-72` (signature), `:150-153`, `:157`, `:163`, `:246`
- Modify: `Server/MobSystem.cs:465-471` (the one caller)
- Test: `Server.Tests/CombatBehaviorTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct CombatOutcome(bool OwnsTick, float Yaw, float Pitch, PlayerStateFlags Flags)`,
  and `CombatBehavior.Tick(ServerPlayer, MobBrain, uint, float, bool, bool) → CombatOutcome`.
  `CombatOutcome.None` is the not-engaged answer.

- [ ] **Step 1: Write the failing test**

Add to `Server.Tests/CombatBehaviorTests.cs`:

```csharp
/// <summary>
/// A decider does not get to be an actor. CombatBehavior used to write mob.State, Yaw, Pitch and
/// LastIntent directly, so whether an NPC ended the tick Aiming or Moving depended on which module
/// ran last. It now REPORTS them and MobSystem applies them once.
/// </summary>
[Fact]
public void CombatReportsTheActorsStateWithoutWritingIt()
{
    var server = new NullNetServer();
    var objects = new ObjectReplication(server);
    var weapons = new WeaponSystem(server, objects, new ChunkMap());
    var items = new ItemSystem(objects);
    var combat = new CombatBehavior(weapons, new ChunkMap());

    var mob = new ServerPlayer
    {
        Id = 60_000,
        IsMob = true,
        Move = new MoveState { Position = Vector3.Zero },
        Yaw = 1.25f,
        Pitch = 0.5f,
        State = PlayerStateFlags.Moving,
        LastIntent = Vector3.UnitX,
    };
    items.SpawnInfantryLoadout(mob);

    var brain = new MobBrain();
    brain.Contacts.Observe(60_001, new Vector3(0f, 0f, 20f), tick: 10);

    var outcome = combat.Tick(mob, brain, tick: 10, dt: 1f / NetworkConfig.TickRate);

    Assert.True(outcome.OwnsTick);
    Assert.True(outcome.Flags.HasFlag(PlayerStateFlags.Aiming));

    // The actor is exactly as it was handed over.
    Assert.Equal(1.25f, mob.Yaw);
    Assert.Equal(0.5f, mob.Pitch);
    Assert.Equal(PlayerStateFlags.Moving, mob.State);
    Assert.Equal(Vector3.UnitX, mob.LastIntent);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~CombatReportsTheActorsStateWithoutWritingIt"`

Expected: FAIL to compile — `CombatOutcome` does not exist and `Tick` returns `bool`.

- [ ] **Step 3: Create the outcome type**

Create `Server/Ai/CombatOutcome.cs`:

```csharp
namespace Demiurge.GameServer;

/// <summary>
/// What combat wants the actor to look like this tick, as a VALUE it hands back rather than as
/// writes it performs.
///
/// The distinction is the whole point. CombatBehavior used to set mob.Yaw, mob.Pitch, mob.LastIntent
/// and mob.State itself, and it runs before MobSystem's own movement writes — so an NPC's final
/// State depended on which module happened to run last rather than on any decision. Reporting means
/// there is exactly one writer and the merge is visible in the source.
///
/// Firing is NOT reported: it is an authoritative action on the world, not a description of the
/// actor, so WeaponSystem.TryFireAi still happens inside Tick. Only its VISIBLE consequence —
/// the Shooting flag — rides in <see cref="Flags"/>.
/// </summary>
internal readonly record struct CombatOutcome(
    bool OwnsTick,
    float Yaw,
    float Pitch,
    PlayerStateFlags Flags)
{
    /// <summary>No believed target, or none worth engaging: combat owns nothing this tick.</summary>
    public static CombatOutcome None => new(false, 0f, 0f, PlayerStateFlags.None);
}
```

- [ ] **Step 4: Change `CombatBehavior.Tick` to return it**

In `Server/Ai/CombatBehavior.cs`, change the signature at line 64:

```csharp
    /// <returns>What the actor should look like, and whether combat owns its movement this tick.</returns>
    public CombatOutcome Tick(
        ServerPlayer mob,
        MobBrain brain,
        uint tick,
        float dt,
        bool mayFire = true,
        bool suppressing = false)
    {
```

Replace every `return false;` in the method body with `return CombatOutcome.None;`. There are two,
both after `brain.ClearCombatTarget()`.

Introduce a local accumulator immediately after the `brain.CombatTargetId != contact.ActorId` block
(just before the `float eyeHeight = …` line):

```csharp
        var flags = PlayerStateFlags.None;
```

Replace lines 150-153:

```csharp
        mob.Yaw = MathF.Atan2(brain.AimDirection.X, brain.AimDirection.Z);
        mob.Pitch = MathF.Asin(Math.Clamp(brain.AimDirection.Y, -1f, 1f));
        mob.LastIntent = Vector3.Zero;
        mob.State = PlayerStateFlags.Aiming;
```

with:

```csharp
        float yaw = MathF.Atan2(brain.AimDirection.X, brain.AimDirection.Z);
        float pitch = MathF.Asin(Math.Clamp(brain.AimDirection.Y, -1f, 1f));
        flags = PlayerStateFlags.Aiming;
```

Replace the two `mob.State = PlayerStateFlags.Reloading;` lines (157 and 163) with
`flags = PlayerStateFlags.Reloading;`, and change their `return true;` to
`return new CombatOutcome(true, yaw, pitch, flags);`.

Replace `mob.State |= PlayerStateFlags.Shooting;` at line 246 with `flags |= PlayerStateFlags.Shooting;`.

Change the two remaining `return true;` statements (the obstruction early-out and the final one) to
`return new CombatOutcome(true, yaw, pitch, flags);`.

Leave `mob.Hotbar = HotbarSlot.Primary;` at line 83 where it is — selecting the primary is an
inventory action, not a description of the actor, and `MobSystem` sets the same thing at line 373.

Leave `mob.Spread.TotalMoa(mob.State, ballistics)` at line 191 reading `mob.State`: that is the
actor's dispersion as of the START of the tick, which is what it was before this change too.

- [ ] **Step 5: Apply the outcome at the single caller**

In `Server/MobSystem.cs`, replace lines 465-471:

```csharp
            bool combatOwnsTick = combat.Tick(
                mob,
                brain,
                tick,
                dt,
                mayFire && !underFire,
                suppressing && mayFire);
```

with:

```csharp
            var combatOutcome = combat.Tick(
                mob,
                brain,
                tick,
                dt,
                mayFire && !underFire,
                suppressing && mayFire);
            bool combatOwnsTick = combatOutcome.OwnsTick;

            // Applied here, exactly where CombatBehavior used to write it, so this task changes
            // WHO writes and not WHAT is written. Task 3 moves this into the single Act step.
            if (combatOwnsTick)
            {
                mob.Yaw = combatOutcome.Yaw;
                mob.Pitch = combatOutcome.Pitch;
                mob.LastIntent = Vector3.Zero;
                mob.State = combatOutcome.Flags;
            }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~CombatBehaviorTests"`

Expected: PASS, all tests in the class.

- [ ] **Step 7: Verify nothing else broke, then stop for review**

Run:
```bash
dotnet build DemiurgeSharp.slnx
dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"
dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"
```
Expected: build clean; fast suite green; integration shows the same three pre-existing failures and
no others. Do not commit. Report the integration counts explicitly.

---

### Task 2: The `MobAction` value and its single `Apply`

**Files:**
- Create: `Server/Ai/MobAction.cs`
- Modify: `Server/MobSystem.cs` (add `Apply`, leave `Step` alone for now)
- Test: `Server.Tests/MobDecideActTests.cs`

**Interfaces:**
- Consumes: `CombatOutcome` from Task 1.
- Produces: `internal readonly record struct MobAction` with fields
  `Intent`, `Jump`, `Sprint`, `Crouch`, `Shooting`, `Aiming`, `Reloading`, `Yaw`, `Pitch`;
  and `MobSystem.Apply(ServerPlayer mob, in MobAction action, float dt)`.

- [ ] **Step 1: Write the failing test**

Create `Server.Tests/MobDecideActTests.cs`:

```csharp
using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

/// <summary>
/// The sense/decide/act invariant: an NPC's visible output for a tick is written in one place, from
/// one value. Overwriting was never a race — everything here runs on the main thread — it was five
/// branches of one function each constructing the whole answer and returning.
/// </summary>
public class MobDecideActTests
{
    [Fact]
    public void ApplyWritesEveryOutputFieldFromTheAction()
    {
        var mob = new ServerPlayer
        {
            Id = 60_000,
            IsMob = true,
            Move = new MoveState { Position = new Vector3(4f, 20f, 4f) },
            Yaw = 0f,
            Pitch = 0f,
            State = PlayerStateFlags.Reloading | PlayerStateFlags.Prone,
            LastIntent = Vector3.UnitZ,
        };

        var action = new MobAction
        {
            Intent = Vector3.UnitX,
            Jump = true,
            Sprint = true,
            Crouch = false,
            Aiming = true,
            Yaw = 1.5f,
            Pitch = -0.25f,
        };

        MobSystem.ComposeState(action, out var state);

        Assert.True(state.HasFlag(PlayerStateFlags.Moving));
        Assert.True(state.HasFlag(PlayerStateFlags.Jumping));
        Assert.True(state.HasFlag(PlayerStateFlags.Sprinting));
        Assert.True(state.HasFlag(PlayerStateFlags.Aiming));
        Assert.False(state.HasFlag(PlayerStateFlags.Crouching));

        // Nothing survives from the actor's previous state. A composed answer that ORed itself onto
        // whatever was already there is how a stale Shooting flag used to ride through a hotbar
        // change and make a rifle look like it fired.
        Assert.False(state.HasFlag(PlayerStateFlags.Reloading));
        Assert.False(state.HasFlag(PlayerStateFlags.Prone));
    }

    [Fact]
    public void AnEmptyActionIsAManStandingStill()
    {
        MobSystem.ComposeState(default, out var state);
        Assert.Equal(PlayerStateFlags.None, state);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~MobDecideActTests"`

Expected: FAIL to compile — `MobAction` and `MobSystem.ComposeState` do not exist.

- [ ] **Step 3: Create `MobAction`**

Create `Server/Ai/MobAction.cs`:

```csharp
using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Everything an NPC emits for one tick, as one value.
///
/// This exists because `MobSystem.Step` had five exits — grenade, combat, entrenching, entrenched,
/// path-following — and each one independently constructed the actor's whole output and returned.
/// Whichever was reached first won, and nothing downstream could see that an answer had already been
/// given. `ActorIntent` was introduced to make the DECISION singular; this makes the RESULT singular,
/// which is the other half and the one that was missing.
///
/// A struct with no methods on purpose: a decider builds one and hands it over. It has no access to
/// the actor, so it cannot write to it, so the failure mode is structurally unavailable rather than
/// forbidden by a comment.
/// </summary>
internal readonly record struct MobAction
{
    /// <summary>Normalized movement direction, or zero to stand.</summary>
    public Vector3 Intent { get; init; }

    public bool Jump { get; init; }
    public bool Sprint { get; init; }
    public bool Crouch { get; init; }

    /// <summary>Actuating the held item — a trigger pull or a shovel swing. Both look the same to
    /// the client's view, which is why digging sets it too.</summary>
    public bool Shooting { get; init; }

    public bool Aiming { get; init; }
    public bool Reloading { get; init; }

    /// <summary>Absolute facing. Deciders that do not care leave <see cref="TurnTo"/> false and the
    /// actor keeps the yaw it had.</summary>
    public float Yaw { get; init; }
    public float Pitch { get; init; }

    /// <summary>Whether <see cref="Yaw"/> and <see cref="Pitch"/> mean anything. A path follower
    /// turns toward its intent and a shooter turns toward its target; a man cycling inside a foxhole
    /// does neither and must not be snapped to zero.</summary>
    public bool TurnTo { get; init; }

    /// <summary>
    /// Standing still on purpose, so the stuck watchdog must not count it against him.
    ///
    /// Here rather than on MobBrain because it is a conclusion of THIS tick's decision, and per-tick
    /// state parked on a long-lived object is the same shape as the bug this whole split exists to
    /// remove — one writer sets it, a later reader finds last tick's answer.
    /// </summary>
    public bool HoldingObjective { get; init; }

    /// <summary>This tick's decision moved dirt, which counts as progress even when the actor did
    /// not move. Same reasoning as <see cref="HoldingObjective"/> for why it lives here.</summary>
    public bool TerrainProgress { get; init; }
}
```

- [ ] **Step 4: Add `ComposeState` and `Apply` to `MobSystem`**

Add to `Server/MobSystem.cs`, immediately above the existing `StepSolver` method:

```csharp
        /// <summary>
        /// The action's flags, and ONLY the action's flags.
        ///
        /// Composed from `None` rather than merged onto `mob.State` deliberately. Three of the five
        /// old exits merged (`mob.State.With(...)`) and two replaced (`PlayerStateFlags.None.With(...)`),
        /// so whether a flag survived a tick depended on which branch produced it. If a decider wants
        /// a flag it says so.
        /// </summary>
        internal static void ComposeState(in MobAction action, out PlayerStateFlags state)
            => state = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, action.Intent != Vector3.Zero)
                .With(PlayerStateFlags.Jumping, action.Jump)
                .With(PlayerStateFlags.Sprinting, action.Sprint)
                .With(PlayerStateFlags.Crouching, action.Crouch)
                .With(PlayerStateFlags.Shooting, action.Shooting)
                .With(PlayerStateFlags.Aiming, action.Aiming)
                .With(PlayerStateFlags.Reloading, action.Reloading);

        /// <summary>
        /// The one place an NPC's tick output reaches the actor. Every decider returns a MobAction;
        /// this is what makes it real.
        /// </summary>
        private void Apply(ServerPlayer mob, in MobAction action, float dt)
        {
            ComposeState(action, out var state);
            mob.State = state;
            if (action.TurnTo)
            {
                mob.Yaw = action.Yaw;
                mob.Pitch = action.Pitch;
            }
            mob.LastIntent = action.Intent;
            StepSolver(mob, action.Intent, dt);
        }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~MobDecideActTests"`

Expected: PASS, 2 tests.

- [ ] **Step 6: Verify nothing else broke, then stop for review**

Run:
```bash
dotnet build DemiurgeSharp.slnx
dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"
```
Expected: build clean, fast suite green. `Apply` is unused until Task 3 and may draw an unused-member
warning; leave it. If this repo's analyzer settings promote that to an error, move `Apply` into Task 3
Step 1 rather than suppressing it. Do not commit.

---

### Task 3: Convert the five exits to return `MobAction`

**Files:**
- Modify: `Server/MobSystem.cs:305-836` (`Step`)
- Test: existing integration suite — this task must not change behaviour

**Interfaces:**
- Consumes: `MobAction`, `MobSystem.Apply` from Task 2; `CombatOutcome` from Task 1.
- Produces: `private MobAction Decide(ServerPlayer mob, MobBrain brain, float dt, uint tick, ICollection<ServerPlayer> actors)`,
  and `Step` reduced to `Decide` + `Apply` + the stuck-progress check.

- [ ] **Step 1: Rename `Step` to `Decide` and change its return type**

In `Server/MobSystem.cs`, change the declaration at line 305 from:

```csharp
        public void Step(
            ServerPlayer mob,
            float dt,
            uint tick,
            ICollection<ServerPlayer> actors)
```

to:

```csharp
        /// <summary>
        /// What this actor does this tick, as a value. Reads the brain and the blackboard; writes
        /// NEITHER the actor's State/Yaw/Pitch/LastIntent NOR its position. Apply does that, once.
        ///
        /// It still mutates the BRAIN — navigation destinations, cover claims, contact memory — and
        /// that is correct: the brain is the blackboard, and deciding is what updates it.
        /// </summary>
        private MobAction Decide(
            ServerPlayer mob,
            float dt,
            uint tick,
            ICollection<ServerPlayer> actors)
```

- [ ] **Step 2: Add the new `Step` that calls both**

Add immediately above `Decide`:

```csharp
        public void Step(
            ServerPlayer mob,
            float dt,
            uint tick,
            ICollection<ServerPlayer> actors)
        {
            var action = Decide(mob, dt, tick, actors);
            Apply(mob, action, dt);

            // After the move, because it measures whether the move achieved anything.
            if (brains.TryGetValue(mob.Id, out var brain)
                && brain.Navigation.Progress.Update(
                    mob.Position,
                    tick,
                    expectedToTravel: !action.HoldingObjective,
                    action.TerrainProgress))
                stuckMobs.Enqueue(mob.Id);
        }
```


- [ ] **Step 3: Convert the grenade exit (was lines 362-369)**

Replace:

```csharp
            if (grenadeCombat.TryThrow(mob, brain, squad, actors, tick))
            {
                brain.DebugIntent = "GRENADE";
                brain.Navigation.Progress.Reset();
                mob.State = PlayerStateFlags.Shooting;
                mob.LastIntent = Vector3.Zero;
                StepSolver(mob, Vector3.Zero, dt);
                return;
            }
```

with:

```csharp
            if (grenadeCombat.TryThrow(mob, brain, squad, actors, tick))
            {
                brain.DebugIntent = "GRENADE";
                brain.Navigation.Progress.Reset();
                return new MobAction { Shooting = true };
            }
```

- [ ] **Step 4: Convert the combat exit (was lines 572-579)**

Replace:

```csharp
                mob.State = mob.State
                    .With(PlayerStateFlags.Moving, combatIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, combatJump)
                    .With(PlayerStateFlags.Sprinting, sprinting)
                    .With(PlayerStateFlags.Crouching, crouching);
                mob.LastIntent = combatIntent;
                StepSolver(mob, combatIntent, dt);
                return;
```

with:

```csharp
                // Combat's own flags carried explicitly rather than by merging onto whatever
                // mob.State happened to hold. This is the merge that used to be implicit in
                // `mob.State.With(...)` and is the reason a stale flag could survive a tick.
                return new MobAction
                {
                    Intent = combatIntent,
                    Jump = combatJump,
                    Sprint = sprinting,
                    Crouch = crouching,
                    Shooting = combatOutcome.Flags.HasFlag(PlayerStateFlags.Shooting),
                    Aiming = combatOutcome.Flags.HasFlag(PlayerStateFlags.Aiming),
                    Reloading = combatOutcome.Flags.HasFlag(PlayerStateFlags.Reloading),
                    Yaw = combatOutcome.Yaw,
                    Pitch = combatOutcome.Pitch,
                    TurnTo = combatOwnsTick,
                };
```

Then delete the interim application block added in Task 1 Step 5 (the `if (combatOwnsTick) { mob.Yaw = … }`),
since the action now carries it. Keep `bool combatOwnsTick = combatOutcome.OwnsTick;`.

The `sprinting` and `crouching` locals above this point read `mob.State.HasFlag(PlayerStateFlags.Shooting)`
and `PlayerStateFlags.Reloading`. Change both to read `combatOutcome.Flags` instead — that is where
those flags now live, and reading `mob.State` would be reading last tick's answer:

```csharp
                bool crouching = brain.AtCover
                    && !bounding
                    && (combatOutcome.Flags.HasFlag(PlayerStateFlags.Reloading)
                        || ShouldCrouchAtCover(brain, tick));
```

```csharp
                bool sprinting = combatIntent != Vector3.Zero
                    && !crouching
                    && (decision is ActorIntent.Bound
                        || !combatOutcome.Flags.HasFlag(PlayerStateFlags.Shooting)
                            && (!brain.AtCover || !mayFire));
```

- [ ] **Step 5: Convert the entrenching exit (was lines 604-611)**

Replace:

```csharp
                mob.State = mob.State
                    .With(PlayerStateFlags.Moving, entrenchIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, entrenchJump)
                    .With(PlayerStateFlags.Sprinting, false)
                    .With(PlayerStateFlags.Crouching, entrenchCrouch);
                mob.LastIntent = entrenchIntent;
                StepSolver(mob, entrenchIntent, dt);
                return;
```

with:

```csharp
                timingEntrenchStopwatchTicks += Stopwatch.GetTimestamp() - entrenchStarted;
                return new MobAction
                {
                    Intent = entrenchIntent,
                    Jump = entrenchJump,
                    Crouch = entrenchCrouch,
                    TerrainProgress = true,
                };
```

The added `timingEntrenchStopwatchTicks` line fixes a real leak: the existing early `return` at 610
skips the accumulation at line 645, so every tick spent entrenching was missing from `ai stats`.

- [ ] **Step 6: Convert the entrenched exit (was lines 639-645)**

Replace:

```csharp
                mob.State = PlayerStateFlags.None
                    .With(PlayerStateFlags.Moving, coverIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, coverJump)
                    .With(PlayerStateFlags.Crouching, tucked);
                mob.LastIntent = coverIntent;
                StepSolver(mob, coverIntent, dt);
                return;
```

with:

```csharp
                timingEntrenchStopwatchTicks += Stopwatch.GetTimestamp() - entrenchStarted;
                return new MobAction
                {
                    Intent = coverIntent,
                    Jump = coverJump,
                    Crouch = tucked,
                };
```

- [ ] **Step 7: Convert the path-following exit (was lines 808-836)**

Replace everything from `mob.State = PlayerStateFlags.None` through the closing brace of the method
with:

```csharp
            // The follower turns toward where it is going; a digging man keeps his heading and
            // pitch, and an idle man on his objective scans. All three used to be `mob.Yaw = ...`
            // writes at this point and are now one answer.
            float followYaw = mob.Yaw;
            float followPitch = digging ? mob.Pitch : 0f;
            if (intent != Vector3.Zero)
                followYaw = RotateYawTowards(
                    mob.Yaw,
                    MathF.Atan2(intent.X, intent.Z),
                    TurnRadiansPerSecond * dt);
            else if (!digging && heardGunshot)
                followYaw = YawTowardHorizontal(mob, brain.HeardPosition, dt);
            else if (!digging && holdingObjective)
                followYaw = NormalizeRadians(mob.Yaw + IdleScanRadiansPerSecond * dt);

            return new MobAction
            {
                Intent = intent,
                Jump = jump,
                HoldingObjective = holdingObjective,
                TerrainProgress = terrainProgress,
                // Shooting reads as "actuating the held item", which is what the client's view of a
                // shovel swings on. Digging is the tool's version of pulling the trigger.
                Shooting = digging,
                Yaw = followYaw,
                Pitch = followPitch,
                TurnTo = true,
            };
        }
```

`FaceHorizontalTarget(mob, brain.HeardPosition, dt)` currently writes `mob.Yaw`. Add a
value-returning sibling beside it and leave the original in place for any other caller:

```csharp
        /// <summary>The yaw <see cref="FaceHorizontalTarget"/> would have written. Deciders take
        /// this one; anything that still writes directly takes the other.</summary>
        private static float YawTowardHorizontal(ServerPlayer mob, Vector3 target, float dt)
        {
            var delta = target - mob.Position;
            delta.Y = 0f;
            if (delta.LengthSquared() < 1e-6f) return mob.Yaw;
            return RotateYawTowards(
                mob.Yaw,
                MathF.Atan2(delta.X, delta.Z),
                TurnRadiansPerSecond * dt);
        }
```

- [ ] **Step 8: Verify the whole thing, then stop for review**

Run:
```bash
dotnet build DemiurgeSharp.slnx
dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"
dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"
```
Expected: build clean; fast suite green; integration shows **the same three pre-existing failures and
no others**. This task changes no behaviour, so any fourth failure is a defect introduced here — bisect
by reverting one exit at a time rather than by patching the symptom. Do not commit.

Then confirm the structural property by inspection: `grep -n "mob.State\s*=\|mob.LastIntent\s*=\|StepSolver(" Server/MobSystem.cs`
must report only the lines inside `Apply`.

---

### Task 4: Fold entrenchment into the decision

**Files:**
- Modify: `Server/Ai/ActorIntent.cs`
- Modify: `Server/MobSystem.cs` (the `decision` ternary and the two entrench branches)
- Test: `Server.Tests/MobDecideActTests.cs`

**Interfaces:**
- Consumes: `ActorIntent` from Task 3's `Decide`.
- Produces: `ActorIntent.HoldFightingPosition` case with `DebugLabel => "DUGIN"`.

**Why:** with Task 3 done, `Decide` still decides `ActorIntent.PursueObjective` and then falls through
into `brain.Entrenching` / `brain.Entrenched`, which move the actor somewhere else. The decision is
still not the decision. This is the actual bug behind "systems overwrite the AI's current task".

- [ ] **Step 1: Write the failing test**

Add to `Server.Tests/MobDecideActTests.cs`:

```csharp
/// <summary>
/// The label an NPC carries must be what it actually did. `ai track states` reads DebugIntent, and
/// an entrenched man with no live contact used to report OBJECTIVE while cycling between his foxhole
/// centre and his peek station — the decision said one thing and two branches below it did another.
/// </summary>
[Fact]
public void AnEntrenchedManReportsBeingDugInRatherThanPursuingHisObjective()
{
    var intents = new ActorIntent[]
    {
        new ActorIntent.HoldFightingPosition(),
        new ActorIntent.PursueObjective(),
    };

    Assert.Equal("DUGIN", intents[0].DebugLabel);
    Assert.NotEqual(intents[0].DebugLabel, intents[1].DebugLabel);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~AnEntrenchedManReportsBeingDugIn"`

Expected: FAIL to compile — `ActorIntent.HoldFightingPosition` does not exist.

- [ ] **Step 3: Add the case**

In `Server/Ai/ActorIntent.cs`, add beside the other cases:

```csharp
    /// <summary>
    /// Working a fighting position: digging one, or cycling between its protected centre and its
    /// peek station once it is dug.
    ///
    /// A case rather than a branch below the decision, which is what it used to be. `Entrenching`
    /// and `Entrenched` were tested AFTER an ActorIntent had already been chosen, so an actor could
    /// be told to pursue its objective and then spend the tick in its hole — the decision was not
    /// the decision, which is the exact failure this union exists to prevent.
    /// </summary>
    internal sealed record HoldFightingPosition : ActorIntent
    {
        internal override string DebugLabel => "DUGIN";
    }
```

- [ ] **Step 4: Make the decision account for it**

In `Server/MobSystem.cs`, change the `decision` ternary to test entrenchment state before falling
through to `PursueObjective`:

```csharp
            ActorIntent decision =
                bounding && !mustEntrench ? new ActorIntent.Bound(order.Destination, order.Bearing)
                : combatOwnsTick && mustEntrench ? new ActorIntent.Entrench()
                : combatOwnsTick ? new ActorIntent.SeekCover(MayAdvance: true)
                // Below here combat does not own the tick. A man who is in or building a hole works
                // it; only a man with neither goes back to his objective.
                : brain.Entrenching || brain.Entrenched ? new ActorIntent.HoldFightingPosition()
                : new ActorIntent.PursueObjective();
```

Update the comment block directly above the ternary at the same time. It currently explains the OLD
ordering ("PursueObjective next, because with no combat there is nothing to entrench against"), which
is no longer what the code does; the reason each arm sits where it does is load-bearing and a stale
explanation of it is worse than none. Keep the Bound-first paragraph verbatim — that one is unchanged
and records a measured bug — and rewrite only the arms below it.

Then change the two branches that follow the combat block from
`if (brain.Entrenching)` and `if (brain.Entrenched)` to test the decision instead:

```csharp
            if (decision is ActorIntent.HoldFightingPosition && brain.Entrenching)
```

```csharp
            if (decision is ActorIntent.HoldFightingPosition && brain.Entrenched)
```

Both keep their existing bodies from Task 3.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~MobDecideActTests"`

Expected: PASS, 3 tests.

- [ ] **Step 6: Verify behaviour in the real scenarios, then stop for review**

Run:
```bash
dotnet build DemiurgeSharp.slnx
dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"
dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"
```
Expected: build clean; fast suite green; the same three pre-existing integration failures.

This is the first task that CAN change behaviour — an actor that used to be labelled `OBJECTIVE`
while entrenched is now labelled `DUGIN`, and the ordering of the ternary changed. If an integration
scenario moves, report which and stop; do not tune the ternary to make a test pass, because the
ordering is load-bearing and documented in the comment above it. Do not commit.

---

### Task 5: Ask Sebastian to look

**Files:** none.

- [ ] **Step 1: Report what to look at**

This plan's whole point is behaviour a test cannot see. Ask him to run
`dotnet run --launch-profile singleplayer`, then in the terminal:

```
ai track states ids
```

and confirm:

- an NPC's label matches what it is visibly doing, in particular that a man in a foxhole reads
  `DUGIN` and not `OBJECTIVE`;
- labels are stable rather than flickering between two values on consecutive ticks — flicker means
  two branches are still fighting;
- `ai stats` reports non-zero entrench time, which it could not before Task 3 Step 5.

Do not screenshot the game; ask him to look.

---

## Deliberately not in this plan

- **Scored arbitration.** Replacing the priority ternary with "price `HoldAndFire`, `RepositionTo`
  and `Entrench` in `CombatValue` and take the max" is the outstanding stage-1 item from
  `docs/superpowers/specs/2026-08-04-ai-overhaul-design.md`. It becomes a local change once `Decide`
  is a function returning one intent, which is what this plan delivers. Doing both at once would mean
  a behaviour change and a refactor in the same diff, with no way to tell which moved a test.
- **Moving anything onto a thread.** See the top of this document.
- **`GrenadeBehavior` and `CoverBehavior` write audits.** Neither writes actor state today
  (`grep -n "mob\.State\|\.LastIntent" Server/Ai/GrenadeBehavior.cs Server/Ai/CoverBehavior.cs`
  returns only reads), so there is nothing to convert. Re-check before adding to them.
- **The three failing integration tests.** Two are a stale `flags.Length == 4` assertion and one is a
  pit-excavation regression; both are tracked in `docs/TODO.md` and are not this plan's business.
