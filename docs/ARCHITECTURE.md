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

Singleplayer still uses the normal server, messages, registries, and replication paths. The embedded
server is stepped from the client update thread because Riptide's static message pools are not
thread-safe. Computational workers may run in parallel, but they must return plain data to the main
thread; they may not create Riptide messages or mutate authoritative gameplay state.

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
  -> SquadBlackboard (<=4 NPCs, roster/centre/contacts/claims/permits/objective/orders)
  -> SquadTactics (squad, 2 Hz, pure)  -- base-of-fire vs bound, flank sides, envelope positions
  -> MobSystem + MobBrain (per-unit arbitration and memory)
  -> Perception / CombatBehavior / CoverBehavior / GrenadeBehavior
  -> NavigationAgent
  -> shared authoritative gameplay systems
```

- `SquadFormation` re-groups each team's living NPCs by proximity once per second. Membership is not
  fixed at spawn: a separated unit joins the squad it is actually fighting beside, over-strength squads
  shed outliers, and under-strength squads merge.
- `CommanderAi` assigns squads to flags. Squads execute capture, defense, and reinforcement locally.
  It costs travel from `SquadBlackboard.Centre`, which is recomputed from live member positions.
- `SquadBlackboard` delays shared contacts, leases cover locations, rotates engagement/advance
  permits, reserves grenade throws, and carries the squad objective, roster, centre, and tactical
  orders. Leases are released on the granting board when a member changes squad.
- `SquadTactics` maps the squad's primary believed threat onto a role per member: one man per flank
  bounds while the rest form the base of fire, and nobody moves until somebody is set. Leapfrog is
  emergent — the man farthest from the threat bounds next — rather than a hand-off state machine.
- `MobBrain` owns only private decision state: contact memory, aim/reaction state, incoming-fire
  response, cover state, gunshot investigation, flank side, bound progress, and one `NavigationAgent`.
- `MobSystem` supplies the global budgets and tick orchestration, and clears a brain on respawn. Cover
  queries are event-driven and globally limited; perception checks at most one enemy per NPC per tick,
  casting one LOS ray at centre mass and a second at the head only when centre mass is blocked.
- Accepted enemy gunshots within 60 m create investigation goals. A projectile passing within 2 m
  creates a two-second incoming-fire stimulus at the firing position, prompting cover selection or
  emergency dirt digging without continuously tracking the live shooter.

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
