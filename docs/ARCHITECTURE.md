# Architecture

This document describes the current runtime architecture and the boundaries that are now in code.

## Project boundaries

```text
Common <- Server
Common <- Editor.Core <- Client
Common <- Client
```

- `Common` owns protocol types, deterministic gameplay math, movement, ballistics, voxel data,
  navigation search, and pure AI scoring/planning. It has no Stride dependency.
- `Server` owns authoritative actors, items, projectiles, grenades, terrain edits, flags, AI
  orchestration, respawns, and replication.
- `Client` owns Stride composition, input/prediction, presentation, sound, HUD, and the editor/runtime
  session lifecycle.
- `Editor.Core` owns source documents, history, validation, structures, and runtime baking without
  depending on Stride.

The root client project globs source recursively, so sibling projects must remain excluded in
`DemiurgeSharp.csproj`. Wire enums and serialized component order are append-only protocol.

## Process and session topology

`ClientApplication` is the process composition root. It creates the Stride game once and delegates
runtime/editor ownership to `ClientSessionCoordinator`.

- `RuntimeClientSession` owns networking, streamed terrain, simulation registries, views, gameplay
  scripts, and an optional embedded `ServerHost`.
- `EditorClientSession` owns the source document, preview terrain, editor camera, tools, and
  placement views.
- Fast editor playtest clones the editor terrain into an authoritative embedded server and reuses
  the existing client terrain renderer.
- Normal singleplayer hosts the authored `conquest` source map, reserves a team-1 spawn for the
  player, and creates 16 NPCs per team around authored team spawn clusters.

Singleplayer still uses the normal server, messages, registries, and replication paths, but it does
not use Riptide: it runs on the in-process transport in `Common/Net`, and the embedded server has its
own thread (`ServerHost.StartOnOwnThread`). The client no longer steps it.

Riptide's static message pools remain thread-unsafe; the constraint did not go away, the singleplayer
path stopped depending on it. Computational workers may still run in parallel and must still return
plain data — they may not mutate authoritative gameplay state, and they may not create messages on
the Riptide path.

The in-process transport is deliberately hostile: it drops, duplicates and reorders traffic within
the bounds a real network could produce, so singleplayer exercises delivery assumptions that a
loopback socket never would. See
[the transport parity spec](superpowers/specs/2026-08-02-transport-parity-design.md).

## Authority and data flow

The client follows one-way flow:

```text
network callback -> queued netcode event -> simulation state -> Stride view
```

Network callbacks do not directly mutate entities or UI. Reliable messages guarantee delivery, not
ordering, so each event must be independently applicable.

The server owns all consequential state. Human and NPC infantry converge below decision-making:

```text
human input packet ─┐
                    ├─> PlayerMovement.Step -> authoritative actor state -> replication
NPC movement intent ┘

human fire request ─┐
                    ├─> WeaponSystem -> projectile simulation -> damage/terrain occlusion
NPC fire decision ──┘
```

NPCs are `ServerPlayer` actors. AI chooses direction, state flags, aim, equipment, and requests; it
does not own a second movement, weapon, reload, grenade, damage, or digging implementation.

## AI layers

The tactical stack is deliberately split by decision rate and scope:

```text
SquadFormation (team, 1 Hz, pure)      -- who is in which squad, from live proximity
CommanderAi (team, 1 Hz)
  -> StrategicObjectivePlanner (pure flag ranking/allocation)
  -> SquadBlackboard (<=6 NPCs, roster/centre/contacts/claims/objective/orders)
  -> SquadTactics (squad, 2 Hz, pure)  -- base-of-fire vs bound, flank sides, envelope positions
  -> MobSystem + MobBrain (per-unit arbitration and memory)
  -> Perception / CombatBehavior / CoverBehavior / GrenadeBehavior
  -> NavigationAgent
  -> shared authoritative gameplay systems

Common/Ai (pure, headlessly testable) -- the currency the layers above decide in:
  CombatValue        -- net health points per second: dealt minus taken/aggression
  WeaponEffectiveness-- what a weapon is worth at a range, including burst length
  ThreatRanking      -- tested threat upper bound; runtime wiring remains in AI_TODO.md
  Exposure           -- SelfExposure vs TargetExposure as distinct types
  CoverScore, WedgeFormation, StrategicObjectivePlanner, ContactMemory
```

- `SquadFormation` re-groups each team's living NPCs by proximity once per second. Membership is not
  fixed at spawn: a separated unit joins the squad it is actually fighting beside, over-strength squads
  shed outliers, and under-strength squads merge.
- `CommanderAi` assigns squads to flags and may reserve one separate resource objective per squad.
  Resource assignments name one operator rather than redirecting the whole squad: useful firearm
  pickups are priced by `EquipmentValue`, while mortars require a live contact inside the authored
  fire sector and no friendly inside the blast-plus-dispersion safety radius.
  Squads execute capture, defense, and reinforcement locally while the selected operator acquires or
  works the resource.
  It costs travel from `SquadBlackboard.Centre`, which is recomputed from live member positions.
- `SquadBlackboard` delays shared contacts, leases cover locations, reserves grenade throws, and
  carries the squad objective, roster, centre, and tactical orders. Leases are released on the
  granting board when a member changes squad.
- `SquadTactics` jointly scores holding and moving against the squad's primary believed threat.
  Movers commit to distinct bearings while at least one base-of-fire member suppresses; a timed-out
  mover releases the rotation rather than freezing it.
- `MobBrain` owns only private decision state: contact memory, aim/reaction state, incoming-fire
  response, cover state, gunshot investigation, flank side, bound progress, and one `NavigationAgent`.
- `MobSystem` supplies the global budgets and tick orchestration, and clears a brain on respawn. Cover
  queries are event-driven and globally limited; perception checks at most one enemy per NPC per tick,
  casting one LOS ray at centre mass and a second at the head only when centre mass is blocked.
- Accepted enemy gunshots within 60 m create investigation goals. A projectile passing within 2 m
  creates a two-second incoming-fire stimulus at the firing position, prompting cover selection or
  emergency dirt digging without continuously tracking the live shooter.
- A pinned NPC that cannot move, is not already protected, is not digging, and is actively engaging
  beyond 30 m may go prone. Standing up imposes a four-second re-entry penalty, so fluctuating
  suppression cannot produce prone/stand spam. Prone is a real stance in shared geometry:
  perception/fire origins move down, hit detection uses a horizontal capsule along the actor's yaw,
  and head/blast/suppression probes follow the lowered body.

### The combat currency

This section used to end by naming the currency as the open question — seconds worked for movement
because execution time is a movement's honest cost, and combat had no equally obvious equivalent.
**It has one: net health points per second**, in `Common/Ai/CombatValue.cs`. Holding, closing,
flanking, entrenching and suppressing all produce or prevent damage over time, so they can be
compared without anybody deciding in advance which a rifleman should prefer. It also bridges to
navigation, which already prices routes in estimated seconds: a manoeuvre costing eight seconds
costs eight seconds of forgone `dealt`, plus whatever is `taken` in transit.

`aggression` is the single global tuning scalar, dividing the taken term. Raise it and the whole
force closes and flanks; lower it and it holds and digs.

What the currency has already replaced:

- **Weapon identity is gone from the AI.** `MaxEngagementRangeFor`, `PrefersToHoldFire` and
  `ShouldAdvance` are deleted; no file under `Server/Ai` or `MobSystem` names `ItemType.Sks` or
  `ItemType.Ppsh`. `CombatBehavior` asks `WeaponEffectiveness.Best(...).DamagePerSecond <= 0f`, so a
  man whose weapon cannot pay at this range sets `ShouldCloseDistance` — the SMG closing and the
  rifle holding is now arithmetic rather than two written behaviours.
- **Fire discipline is a scored burst length.** `WeaponEffectiveness` prices the extra damage,
  recoil, ammunition, and assessment delay of each additional round. No AI branch names a weapon to
  choose its burst.
- **Squad allocation is joint.** `SquadTactics` scores each member holding versus assaulting — the
  latter averaged over destination and transit, with the threat suppressed because the base of fire
  will be shooting — and picks the assignment maximising the squad total. Covering fire paying for
  the bound it enables is what stops every man independently concluding that moving is dangerous.
- **The sound perception bound is not wired yet.** `ThreatRanking` is pure and tested, but runtime
  perception still advances round-robin and combat selects the nearest remembered contact. This is
  tracked in [`AI_TODO.md`](../AI_TODO.md).

### What is still an ordered chain

Per-unit action selection in `MobSystem` has NOT been converted. It is a priority ternary over
`ActorIntent`:

```csharp
bounding && !mustEntrench         ? Bound
  : combatOwnsTick && mustEntrench ? Entrench
  : combatOwnsTick                 ? SeekCover
  : entrenching || entrenched      ? HoldFightingPosition
  :                                  PursueObjective
```

`mustEntrench` is priced in `CombatValue.Taken`, but against a fixed threshold rather than against
the alternatives, so nothing here compares candidates. The tell is that **`ActorIntent.HoldAndFire`
is declared and never constructed anywhere** — it is one of the three actions the scored form wants
(`HoldAndFire`, `RepositionTo(p)`, `Entrench`) and there is nothing to construct it from.

The currency exists, is pure, is tested headlessly, and is already used a layer up in
`SquadTactics`. The remaining integration work is tracked in [`AI_TODO.md`](../AI_TODO.md).

## Navigation boundary

`Common/Navigation` is deterministic, synchronous, and world-agnostic beyond `ChunkMap`.
`Server/Ai/NavigationSystem` owns asynchronous scheduling and `NavigationAgent` owns each NPC's
request/path lifecycle.

Navigation workers receive immutable request values and read terrain optimistically. Completed paths
are stamped with the revisions of the chunks crossed by their corridor plus an apron. The server
installs a path only if those revisions still match. Reconstruction also rechecks standability, so
an edit racing a worker produces a failed/retried request instead of an exception.

The worker pool is bounded to half the logical processors, clamped to 1–8. Requests are prioritized,
superseded work is cancellable, and squad members reuse a shared long objective trunk while retaining
local connectors, formation exits, jumps, digging, and obstacle recovery.

See [NAVIGATION.md](NAVIGATION.md) for the full lifecycle and diagnostics.

## Match events and presentation

`ActivityFeedSystem` converts authoritative kills, flag captures/neutralizations, and stuck-NPC
deletions into reliable `ActivityFeedData`. `NetworkManager` queues those messages and the HUD
renders a short-lived event feed.

Tracers, impacts, explosions, ragdolls, and the kill camera are client presentation. Their placement
comes from authoritative events, but they do not decide hits or terrain damage. Depth testing keeps
line effects and impact/explosion placeholders from appearing through terrain.

The client owns its display configuration in code. It disables generated `GameSettings`, requests a
1920x1080 back buffer before `game.Run`, and uses SDL borderless desktop fullscreen. Changing
fullscreen/back-buffer state from the post-device start callback can reintroduce the SDL
resize/device-reset loop documented in [CLAUDE.md](../CLAUDE.md#performance-targets).

## Verification boundaries

- Pure behavior math and search belong in `Common.Tests`.
- Server orchestration boundaries and systems belong in `Server.Tests`.
- `ConquestNavigationBenchmarkTests` is tagged `Category=Benchmark` and is excluded from ordinary
  test runs.
- Full-system scenarios such as `MobDigEscapeIntegrationTests` are tagged `Category=Integration`
  and run only during feature-specific validation.
- Use `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"` for the
  fast suite. While validating NPC pathfinding, use the explicit integration-test command from
  `CLAUDE.md`.
- Use singleplayer `conquest` for the integrated 32-NPC scale test and `ai stats` for the current
  one-second AI/navigation window.
