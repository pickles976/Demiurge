# Architecture

How DemiurgeSharp is structured as a **code-first** Stride game (no GameStudio editor),
and how to keep early gameplay code shaped so that server-authoritative netcode can be
added later without rewrites.

This is a direction document, not a description of what exists today. The current
`Program.cs` is a sandbox; the patterns below are the target.

## Guiding principle

> Input produces **intent**. Simulation consumes **intent** and a timestep. Animation
> is derived from **resulting state** (velocity), never from input.

Everything else in this document follows from that one rule. It is the single thing that
is expensive to retrofit, so it goes in first — even before there is any networking.

---

## Top-level structure

When this grows past a sandbox, split into three pieces by dependency, not by feature:

```
DemiurgeSharp.Shared/     # references Stride.Core / Stride.Engine — NEVER Stride.Graphics
    Components:  Position, Velocity, Health, PlayerId, ChunkPos, PlayerIntent
    Sim systems: MovementProcessor, CombatSystem, ChunkLogicSystem
    Protocol:    ClientMessage, ServerMessage, snapshot (de)serialization
    World:       fixed-timestep Step(dt)

DemiurgeSharp.Server/     # headless: a tick loop, NO GraphicsCompositor, no Game
    Authoritative World + the Shared sim systems
    Transport (sockets), interest management, persistence

DemiurgeSharp.Client/     # the Stride Game: rendering, camera, UI, audio
    The Shared sim systems (used for local prediction only)
    ClientTransport, interpolation, mesh + animation (see ASSET_LOADING.md)
```

The teeth of this split: **`Shared` must not depend on `Stride.Graphics`.** That is what
lets the server run the exact same `MovementProcessor` headlessly, and what makes client
prediction run identical code to the server. The server never instantiates Stride's
`Game` (which assumes a graphics device); it is a plain fixed-timestep loop.

Durable units of logic are **`GameSystem`** (global per-frame logic + services) and
**`EntityProcessor<T>`** (Stride's ECS "system" — iterates all entities carrying a given
component). Real gameplay lives in these, not in one giant `Update` closure.

---

## The game loop

There are two entry points with different loop shapes. The fixed-step
`world.Step(dt)` in the middle is **the same code on both sides**.

### Server — the loop *is* the program

No Stride `Game`, no rendering. Just a clock.

```
main():
    world = new World(loadShared())
    net   = new ServerTransport(port)
    accumulator = 0
    prev = now()

    while running:
        accumulator += now() - prev;  prev = now()

        net.pump()                       // read sockets -> fill input queues

        while accumulator >= FIXED_DT:   // catch up; may run 0..N times
            world.step(FIXED_DT)         // drain intents, sim, combat, chunks
            accumulator -= FIXED_DT

        net.replicate(world, tick)       // snapshots to each player
        sleep_until_next_tick()          // pace to ~30/60 Hz, don't busy-spin
```

### Client — Stride's render loop, with a fixed-step sim inside it

Stride owns the outer loop (GPU, windowing, vsync). Gameplay does **not** go directly in
`Update` — an accumulator ticks the sim at the same fixed rate as the server, while
rendering runs at display rate.

```
main():
    game = new Game()
    game.Run(start: Start, update: Update)   // Stride owns the outer loop

Start(scene):
    setup render (camera, compositor, lights)
    register systems (ChunkStreaming, Interpolation, ...)
    net.connect()

Update(scene, time):                 // called once per RENDERED frame
    net.pump()                       // receive snapshots -> buffer

    accumulator += time.Elapsed
    while accumulator >= FIXED_DT:    // sim at fixed rate, decoupled from FPS
        sampleInput() -> intent
        predictLocalPlayer(FIXED_DT)  // same shared step the server runs
        net.sendInput(tick)
        accumulator -= FIXED_DT

    interpolateRemoteEntities(renderTime)   // every frame, smooth
    updateAnimationStates()                 // every frame
    // Stride renders after Update returns
```

### Shape to take away

```
            +--------------- fixed timestep ---------------+
SERVER:   own loop -> [ step(dt) ] -> replicate          (authority)
CLIENT:   Stride loop -> Update -> [ step(dt) accumulator ] -> interpolate + render
SHARED:   the same step(dt) on both sides
```

- **Server:** fixed step, then send. That loop is the whole program.
- **Client:** Stride's frame loop is the program; gameplay is a fixed-step accumulator
  *inside* `Update`; rendering/interpolation happens once per frame around it.
- The accumulator is the entire trick that decouples sim rate from frame rate.

---

## Next step: player spawn + control (no backend yet)

This is what to build now. It has exactly the seams netcode needs and nothing more. The
whole abstraction is one data struct (`PlayerIntent`) and one interface
(`IIntentProvider`) — the only two things a backend will ever touch.

### The three pieces

```
// 1. THE SEAM -- a plain serializable struct.
//    Today produced by keyboard; later this IS your network input message.
struct PlayerIntent {
    Vector2 Move;     // already normalized, -1..1 per axis
    float   YawDelta; // look this frame
    bool    Jump;
}

// 2. THE SWAPPABLE SOURCE -- where an intent comes from.
//    One impl today. Netcode adds more without touching anything below.
interface IIntentProvider {
    PlayerIntent Poll(float dt);
}

class LocalInputProvider : IIntentProvider {
    Input, sensitivity
    Poll(dt):
        move = (axis(D,A), axis(W,S))                 // raw keys
        return PlayerIntent {
            Move     = clampLen(move, 1),
            YawDelta = Input.MouseDelta.X * sensitivity,
            Jump     = Input.IsKeyPressed(Space),
        }
}

// 3. THE CONSUMER -- knows nothing about input OR network.
//    This stays identical when you add a backend.
class PlayerController : SyncScript {
    IIntentProvider Intents      // injected at spawn
    Body                         // CharacterComponent / physics mover
    AnimationComponent Anim
    string animState = "idle"

    Update():
        Apply(Intents.Poll(dt), dt)

    // (intent, dt) -> new state. This is your future fixed-step sim unit.
    Apply(PlayerIntent intent, float dt):
        Entity.Transform.Rotation *= yaw(intent.YawDelta)
        var dir = transformByYaw(intent.Move)          // move relative to facing
        Body.SetVelocity(dir * Speed)
        if (intent.Jump && Body.IsGrounded) Body.Jump()

        DriveAnimation(Body.Velocity)                  // <- from STATE, not input

    DriveAnimation(velocity):
        var s = horizontalSpeed(velocity)
        var want = s < 0.1 ? "idle" : s < RunSpeed ? "walk" : "run"
        if (want != animState) { Anim.Crossfade(want, 0.15s); animState = want }
}
```

### Spawning

```
Entity SpawnPlayer(scene, IIntentProvider intents, Vector3 pos):
    e = new Entity("Player")
    e.Add(new ModelComponent(Load<Model>("models/girl_mechanic/scene")))

    anim = new AnimationComponent()
    e.Add(anim)
    anim.Animations.Add("idle", Load("..._Girl_Idle"))
    anim.Animations.Add("walk", Load("..._Girl_walk"))
    anim.Animations.Add("run",  Load("..._Girl_run"))

    e.Add(makeCharacterBody())                          // physics mover
    e.Add(new PlayerController { Intents = intents, Body = ..., Anim = anim })
    e.Transform.Position = pos
    e.Scene = scene
    return e
```

(Animation clips are produced by the asset pipeline — see [ASSET_LOADING.md](ASSET_LOADING.md).)

### Wiring it up today (no backend)

```
Start(scene):
    setupCameraAndLights(scene)
    var player = SpawnPlayer(scene, new LocalInputProvider(Input), spawnPos)
    attachFollowCamera(player)        // separate concern; keep it dumb for now
```

You play the game with `LocalInputProvider`. No netcode anywhere.

### Why this abstracts cleanly later (without building any of it now)

| Piece | Today | When you add netcode |
|---|---|---|
| `PlayerIntent` | filled from keys | **becomes the `ClientMessage.Input` payload** — already serializable, no rework |
| `IIntentProvider` | `LocalInputProvider` | server swaps in `NetworkIntentProvider` (reads socket); client keeps local for **prediction** |
| `Apply(intent, dt)` | runs in `Update` at frame dt | runs in `world.step(FIXED_DT)` on both sides — **same code = reconciliation works** |
| `DriveAnimation(velocity)` | reads local body velocity | reads **replicated** velocity → remote players animate with zero input, automatically |

### Hold to these two rules now; everything else is mechanical later

1. **Input only ever writes a `PlayerIntent`** — it never moves the transform or sets
   animation directly. `LocalInputProvider` is the *only* type that mentions `Keys`.
2. **Animation is derived from resulting motion (velocity/state), never from input.**
   Animate off key-presses and remote players won't animate once netcode exists.

### Deliberately NOT doing yet (avoid over-engineering)

- Don't make `Apply` take a fixed timestep — frame dt is fine pre-netcode.
- Don't split into Shared/Server/Client projects yet.
- Don't add tick counters, prediction, or interpolation.

These are mechanical changes that the seams above make easy. The expensive part — the
input → intent → state → animation decoupling — is the part being put in now.

---

## Roadmap

The phased build-out (singleplayer first, then peel rendering off simulation, then a
headless server, then replication, then chunk streaming, combat, interpolation, interest
management, persistence) is tracked in [scratchpad/NETWORKING.md](scratchpad/NETWORKING.md).
Terrain/chunk meshing approaches are in [scratchpad/TERRAIN.md](scratchpad/TERRAIN.md).
