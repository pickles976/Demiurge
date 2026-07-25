# Scripts and the update loop (Stride 4.3.0.2507)

Reference for `ScriptComponent` and friends. Every claim below was read out of IL. Re-check with:

```bash
ilspycmd -t 'Stride.Engine.Processors.ScriptSystem' ~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/Stride.Engine.dll
ilspycmd -t 'Stride.Engine.EntityProcessor`2' ~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/Stride.Engine.dll   # backtick generics need single quotes
```

Assemblies: `~/.nuget/packages/<pkg>/4.3.0.2507/lib/net10.0/`. XML docs ship alongside as `Stride.*.xml`.

---

## 1. `Enabled` does not exist on scripts, and nothing would honour it

`ScriptComponent : EntityComponent` — **not** `ActivableEntityComponent` (`Stride.Engine.ScriptComponent`, Stride.Engine.dll).
`Enabled` is declared *only* on `ActivableEntityComponent` (Stride.Engine.dll), which scripts do not derive from.

```csharp
myScript.Enabled = false;   // does not compile for a SyncScript
```

If it does compile somewhere, the subclass declared its own unrelated member and the engine never reads it. Even a
hypothetical `Enabled` would change nothing — there is no gate anywhere on the path:

- `ScriptSystem.Update` iterates `syncScriptsCopy` and calls `Scheduler.Schedule(node, ScheduleMode.Last)`
  unconditionally, with no enabled check (`Stride.Engine.Processors.ScriptSystem`, Stride.Engine.dll).
- `ScriptProcessor` overrides only `OnSystemAdd`, `OnEntityComponentAdding`, `OnEntityComponentRemoved`. It has
  **no `Update` override at all** — it is a registration hook, not a driver
  (`Stride.Engine.Processors.ScriptProcessor`, Stride.Engine.dll).
- `EntityProcessor<TComponent,TData>.ProcessEntityComponent` gates only on `forceRemove` and `EntityMatch(entity)`
  (a required-component-type check) — no enabled gate (`Stride.Engine.EntityProcessor\`2`, Stride.Engine.dll).
- `EntityProcessor.Enabled` *does* exist and `EntityManager.Update` honours it — but that is the **processor's**
  switch. Disabling `ScriptProcessor` still would not stop scripts, because scripts are driven by `ScriptSystem`
  (a `GameSystemBase`), not by the processor's `Update`.

**The only global off-switch:** `ScriptSystem` is a `GameSystemBase` with `Enabled = true` set in its constructor, and
`GameSystemCollection.Update` skips systems whose `Enabled` is false (`Stride.Games.GameSystemCollection`,
Stride.Games.dll). So `Game.Script.Enabled = false` halts *every* script in the game.

### How to actually stop one script's per-frame work

1. **Own flag + early return.** Cheapest, keeps all `Start()` state, repo-idiomatic.
   ```csharp
   public bool Paused;
   public override void Update()
   {
       if (Paused) return;
       ...
   }
   ```
2. **`Entity.Remove(script)`** — unschedules the node and calls `Cancel()`. `Entity.Add(script)` later **re-runs
   `Start()`** on the same instance with all fields intact (§5).
3. **Remove the whole entity from the scene** — `ScriptSystem.Remove` fires per script.

---

## 2. Type map

| Type | Base | Override | Notes |
|---|---|---|---|
| `ScriptComponent` | `EntityComponent` | — | abstract; no update hook of its own |
| `StartupScript` | `ScriptComponent` | `virtual void Start()` | holds internal `StartSchedulerNode` |
| `SyncScript` | `StartupScript` | `abstract void Update()` | holds internal `UpdateSchedulerNode`; gets `Start()` too |
| `AsyncScript` | `ScriptComponent` | `abstract Task Execute()` | **not** a `StartupScript` → no `Start()`; exposes `CancellationToken` |

All in Stride.Engine.dll. `ScriptComponent` carries
`[DefaultEntityComponentProcessor(typeof(ScriptProcessor), ExecutionMode = ExecutionMode.Runtime)]`,
`[AllowMultipleComponents]`, `[ComponentOrder(1000)]`, `[ComponentCategory("Scripts")]`.

`AllowMultipleComponents` means several scripts of the same type may sit on one entity, and `Entity.Get<T>()` returns
only the first of them.

---

## 3. `ScriptSystem.Update` — exact order of operations

Source: `ScriptSystem.Update` (Stride.Engine.dll). `UpdateBit = 0x1_0000_0000L`.

1. `scriptsToStartCopy += scriptsToStart`; `scriptsToStart.Clear()`
2. `syncScriptsCopy += syncScripts`
   → both snapshots are taken up front, so adding/removing scripts during the frame is re-entrancy-safe
3. For each pending script:
   - `StartupScript` → `StartSchedulerNode = Scheduler.Add(Start, Priority, token: script, ProfilingKey)`
     (enqueued `ScheduleMode.Last`, priority = the raw `int`, **no** UpdateBit)
   - `AsyncScript` → `MicroThread = AddTask(Execute, Priority | UpdateBit)`
4. For each sync script: `node.Value.Priority = Priority | UpdateBit`; `Scheduler.Schedule(node, ScheduleMode.Last)`
   → the component's `Priority` is re-read **every frame**
5. `Scheduler.Run()` — drains the whole queue; `Start()`, `Update()` and async resumptions interleave purely by priority
6. Null out `StartSchedulerNode`s, clear `IsLiveReloading`, clear both copies

`Scheduler.Run()` (`Stride.Core.MicroThreading.Scheduler`, Stride.Core.MicroThreading.dll) dequeues from a
`PriorityNodeQueue<SchedulerEntry>` until empty, then does `while (FrameChannel.Balance < 0) FrameChannel.Send(0)`.
That send happens *after* the drain loop has already broken — which is exactly why `Script.NextFrame()` waiters
resume in the **next** frame's `Run()`.

---

## 4. `Priority` — how it really orders things

`SchedulerEntry.CompareTo` compares `(long Priority, then SchedulerCounter)`, lower first
(`Stride.Core.MicroThreading.SchedulerEntry`, Stride.Core.MicroThreading.dll).

Since `int.MaxValue < UpdateBit`, for **non-negative** priorities all `Start()` entries sort before all `Update()`
entries in the same frame. That is the whole point of the bit.

### Footgun: negative `Priority` collapses the UpdateBit

`Priority` is an `int`; `Priority | 0x100000000L` widens it with **sign extension** first:

```
(-1) | 0x1_0000_0000L  ==  -1        // 0xFFFF_FFFF_FFFF_FFFF | UpdateBit is still all-ones
```

So a `SyncScript` with `Priority = -1` gets scheduler priority `-1` and its `Update()` runs **before every `Start()`
of priority ≥ 0** that frame. Keep script priorities non-negative.

### Same-priority order is arbitrary

Ties break on `SchedulerCounter`, which is assigned in the order `Schedule()` was called — i.e. the enumeration order
of `HashSet<SyncScript> syncScripts`. Treat equal-priority ordering as unspecified; never use it for cross-script
data flow. Give the producer a lower `Priority` than the consumer instead.

### Other priority notes

- `ScriptSystem.PriorityScriptComparer` is declared but **never referenced** — dead code. Do not infer semantics from it.
- `SyncScript`/`StartupScript` do not override `PriorityUpdated()`. A sync priority change is picked up in step 4 of
  the next frame.
- `AsyncScript.PriorityUpdated` sets `MicroThread.Priority = Priority` **without** the UpdateBit — changing an async
  script's priority at runtime silently moves its microthread into the "startup band".

---

## 5. Adding and removing scripts at runtime

The whole chain is synchronous, inside the `Entity.Add` / `Entity.Remove` call:

```
EntityComponentCollection.InsertItem|RemoveItem
  → Entity.OnComponentChanged
  → EntityManager.NotifyComponentChanged
  → CheckEntityComponentWithProcessors
  → ScriptProcessor.OnEntityComponentAdding|OnEntityComponentRemoved
  → ScriptSystem.Add|Remove
```
(`Stride.Engine.EntityComponentCollection`, `Stride.Engine.Entity`, `Stride.Engine.EntityManager`, Stride.Engine.dll)

**`ScriptSystem.Add`** calls `script.Initialize(Services)` (sets `Services`, `Game`, the graphics device service),
adds to `registeredScripts` **and** `scriptsToStart`, and for a `SyncScript` creates a **new** `UpdateSchedulerNode`
via `Scheduler.Create` (created, not yet enqueued).

**Re-adding the same instance therefore re-runs `Start()`.** The constructor does not re-run, so every field keeps the
value it had when the script was removed. If `Start()` must be idempotent, guard it yourself:

```csharp
private bool started;
public override void Start()
{
    if (started) return;
    started = true;
    ...
}
```

**`ScriptSystem.Remove`** — if the script had already started (i.e. it was *not* still pending in `scriptsToStart`),
it calls `script.Cancel()`, whose base implementation disposes the script's `ObjectCollector` (`Collector`). An
`AsyncScript`'s microthread is cancelled. A `SyncScript`'s node is `Unschedule`d and set to `null`.

**Timing.** Add during frame N (from inside some `Update`): the snapshots were already taken, so `Start()` and the
first `Update()` both land in frame N+1 — `Start()` first, per §4. Remove during frame N before that script's node has
been dequeued: `Unschedule` pulls it out of the queue, so it does not update that frame.

**Detached entities.** `Entity.OnComponentChanged` does `EntityManager?.NotifyComponentChanged(...)`. An entity that is
not in a scene has no `EntityManager`, so adding a script to it **registers nothing** until the entity is added to the
scene.

---

## 6. Reaching services and other components

`Services` and `Game` are assigned in `ScriptComponent.Initialize`, which runs from `ScriptSystem.Add` — so they are
**null in the constructor and in field/property initializers**. Do lookups in `Start()`.

Lazily-cached properties on `ScriptComponent` (Stride.Engine.dll), all resolved via `GetSafeServiceAs<T>()`:
`Content`, `Input`, `Script`, `SceneSystem`, `EffectSystem`, `DebugText`, `Audio`, `SpriteAnimation`, `Streaming`,
`GameProfiler`; plus `GraphicsDevice`, `Log`, `Collector`, `ProfilingKey`, `Services`, `Game`.

- `GetSafeServiceAs<T>()` **throws `ServiceNotFoundException`** when the service is missing.
- `Services.GetService<T>()` returns `null` instead (`Stride.Core.ServiceRegistryExtensions`, Stride.Core.dll).

Components: `Entity.Get<T>()`, `Get<T>(index)`, `GetAll<T>()`, `GetOrCreate<T>()`, `Add`, `Remove<T>()`,
`RemoveAll<T>()` (`Stride.Engine.Entity`, Stride.Engine.dll).

**House style in this repo:** dependencies are injected through object initializers at construction, not pulled from
`Services`.

```csharp
// Client/View/NetObjectScript.cs
public class NetTransformScript : SyncScript
{
    public required NetObject Object { get; init; }
    ...
}

// Client/Program.cs:238-242
cameraEntity.Add(new LocalPlayerController { CameraEntity = cameraEntity, Registry = registry });
cameraEntity.Add(new AimLineScript { Registry = registry });
```

Sibling-component lookups: `Client/View/AimLineScript.cs:19` (`Entity.Get<CameraComponent>()`),
`Client/View/LocalPlayerController.cs:31` (`CameraEntity.Get<ThirdPersonCameraScript>()`).

---

## 7. Delta time

`Game.UpdateTime` is a `Stride.Games.GameTime` (Stride.Games.dll): `Elapsed`, `Total`, `FrameCount`, `FramePerSecond`,
`TimePerFrame`, `FramePerSecondUpdated`, `WarpElapsed`, `Factor`.

```csharp
float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
```

Used at `Client/View/ItemScripts.cs:21`, `Client/View/PlayerCamera.cs:39`, `Client/View/PlayerViewScript.cs:37`,
`Client/Rendering/TracerManager.cs:68`.

`Factor` scales `WarpElapsed` only — `Elapsed` is unscaled real elapsed time.

---

## 8. Where scripts sit in the frame

`GameBase.Update` → `GameSystems.Update(gameTime)` (`Stride.Games.GameBase`, `Stride.Games.GameSystemCollection`,
Stride.Games.dll). Systems sort by `UpdateOrder` (all default 0) using `UpperBound` insertion, so the registration
order in `Stride.Engine.Game` holds:

```
InputSystem → ScriptSystem → GameFontSystem → SpriteAnimationSystem → DebugTextSystem
→ ProfilingSystem → EffectSystem → StreamingManager → SceneSystem → AudioSystem → VRDeviceSystem
```

`SceneSystem.Update` → `SceneInstance.Update`, and `SceneInstance : EntityManager`, so it runs every enabled
`EntityProcessor.Update` in `Order` (`Stride.Engine.SceneSystem`, `Stride.Engine.SceneInstance`,
`Stride.Engine.EntityManager`, Stride.Engine.dll). Consequences:

- Input is already polled for this frame when `Update()` runs.
- **Entity processors — transform, model-node-link, animation, physics — run *after* all scripts in the same frame.**
  A transform written in `Update()` is consumed later that frame; a transform *read* in `Update()` is last frame's
  processor output.
- `ScriptProcessor.Order = -100000`.

---

## 9. Async scripts and microthreads

Microthreads are cooperative continuations on the main thread, not OS threads.

- `Script.NextFrame()` — resumes in the next frame's `Scheduler.Run()` (§3).
- `Scheduler.Yield()` — reschedules as last within the current frame.
- `Script.AddTask(Func<Task>, long priority = 0)` — ad-hoc microthread; note the priority here is raw, no UpdateBit.
- `Script.WhenAll(params MicroThread[])`.
- `Scheduler.NextFrame()` throws `"NextFrame cannot be called out of the micro-thread context."` if called outside a
  microthread.

This repo currently uses no `AsyncScript` — every script under `Client/View/` and `Client/Rendering/` is a `SyncScript`.

---

## 10. Exceptions — correcting the folk belief

`Scheduler.Run` wraps `arg.Action()` in a try/catch and raises `ActionException` →
`ScriptSystem.HandleSynchronousException`, which:

1. logs to the `"ScriptSystem"` logger, then
2. `if (Scheduler.PropagateExceptions) ExceptionDispatchInfo.Capture(e).Throw();`

`PropagateExceptions` is set to `true` in the `Scheduler` constructor and has an `internal` setter — there is no
public way to turn it off. **So an exception thrown from `Start()` or `Update()` rethrows out of `ScriptSystem.Update`
and takes down the update loop.** The `syncScripts.Remove(...)` / `registeredScripts.Remove(...)` lines that follow
the throw are unreachable in the default configuration: a throwing script is *not* quietly unregistered and left
running-but-skipped. It crashes the game.

For `AsyncScript`, exceptions go through `MicroThread.SetException` in `Scheduler.Run` and are rethrown unless the
microthread carries `MicroThreadFlags.IgnoreExceptions`.

---

## 11. Gotcha checklist

- No `Enabled` on scripts; `Game.Script.Enabled = false` is the only engine-level switch (§1).
- Negative `Priority` sign-extends and destroys the start/update banding (§4).
- Equal `Priority` → arbitrary order (§4).
- `AsyncScript` priority changes drop the UpdateBit (§4).
- `Services` / `Game` are null before `Start()` (§6).
- Re-adding a removed script re-runs `Start()` but not the constructor; fields survive (§5).
- Adding a script to an entity that is not in a scene registers nothing (§5).
- `AllowMultipleComponents` is on: `Entity.Get<T>()` returns only the first of several same-typed scripts (§2).
- Entity processors run after scripts within a frame (§8).
- An unhandled exception in a script crashes the update loop (§10).
