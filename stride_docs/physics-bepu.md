# Bepu physics in Stride (DemiurgeSharp reference)

Verified against decompiled `Stride.BepuPhysics` 4.3.0.2507, `Stride.BepuPhysics.Debug`
4.3.0.2507, `Stride.CommunityToolkit.Bepu` 1.0.0-preview.62, `Stride.CommunityToolkit`
1.0.0-preview.62, plus this repo's usage. Assemblies are cited inline; **`Stride.BepuPhysics`**
(the engine integration) and **`Stride.CommunityToolkit.Bepu`** (convenience helpers) are
distinguished everywhere.

## Wrong API warning: legacy Bullet

Most Stride physics material on the web — and most LLM recall — describes the **legacy Bullet
integration**: `Stride.Physics`, `RigidbodyComponent`, `StaticColliderComponent`,
`PhysicsComponent`, `this.GetSimulation().Raycast(...)`, `CollisionEnded` events,
`ColliderShapes` lists on the component. **None of that exists in this repo.** `grep -rn
"Stride.Physics" Client Common Server` returns nothing. If a snippet mentions
`RigidbodyComponent` or `PhysicsColliderShape`, it is the wrong API — translate it to the types
below.

## Packages and wiring

`DemiurgeSharp.csproj` references only `Stride.CommunityToolkit.Bepu 1.0.0-preview.62`.
`Stride.BepuPhysics` and `Stride.BepuPhysics.Debug` 4.3.0.2507 arrive **transitively**
(`obj/project.assets.json`, the `Stride.CommunityToolkit.Bepu/1.0.0-preview.62` dependency
block). The debug renderer therefore needs no csproj change.

Nothing registers physics explicitly: `BepuConfiguration` is an `IService` created on demand by
`GetSimulation()`, and it installs `PhysicsGameSystem` itself.

## Component model

All in `Stride.BepuPhysics` (Stride.BepuPhysics.dll).

| | type | what it is |
|---|---|---|
| dynamic | `BodyComponent` | default; mass/inertia from its shapes, moved by gravity, impulses, contacts |
| kinematic | `BodyComponent { Kinematic = true }` | zeroed `LocalInertia` (infinite mass); pushes others, is pushed by nothing |
| static | `StaticComponent` | no mass, no velocity; only `Position`, `Orientation`, `ContinuousDetection` |

`CollidableComponent : EntityComponent` is the abstract base of all three. Notable members and
their defaults:

- `required ICollider Collider` — the shape (see next section).
- `BepuSimulation? Simulation` — null until attached.
- `SpringFrequency` 30, `SpringDampingRatio` 3, `FrictionCoefficient` 1,
  `MaximumRecoveryVelocity` 1000.
- `CollisionLayer`, `CollisionGroup` — filtering, see below.
- `IContactHandler? ContactEventHandler` — assignment registers/unregisters the handler.
- `Vector3 CenterOfMass` (read-only; `Vector3.Zero` until attached).
- `ISimulationSelector SimulationSelector` = `SceneBasedSimulationSelector.Shared`.
- `RayCast(in origin, in dir, maxDistance, out HitInfo)` / `RayCastPenetrating(...)` against
  **this collidable only**.

`BodyComponent` adds: `Kinematic`, `Gravity` (true), `SleepThreshold` (0.01f squared velocity;
negative means it can never sleep), `MinimumTimestepCountUnderThreshold` (32),
`Awake`, `LinearVelocity`, `AngularVelocity` (axis-angle, radians/s, length = rate),
`PreviousLinearVelocity` / `PreviousAngularVelocity`, `Position` / `Orientation`,
`BodyInertia`, `SpeculativeMargin`, `ContinuousDetectionMode` (`Discrete`),
`InterpolationMode` (`None`), `Constraints`.

`CharacterComponent : BodyComponent` also exists (`Speed` 10, `JumpForce` 10, `Move(dir)`,
`TryJump()`, `IsGrounded`, implements `ISimulationUpdate` + `IContactHandler`). **This repo does
not use it** — player movement is server-authoritative and hand-rolled, see the multiplayer note.

Nesting: `BodyComponent.AttachInner` calls `FindParentBody` / `SetParentForChildren`, so a body on
an entity parented under another body-bearing entity automatically becomes its physics child.

## Colliders

Namespace `Stride.BepuPhysics.Definitions.Colliders` (Stride.BepuPhysics.dll).

**The single most important shape gotcha:** `Collider` is typed `ICollider`, and only
`CompoundCollider`, `MeshCollider` and `EmptyCollider` implement `ICollider`. `BoxCollider`,
`SphereCollider`, `CapsuleCollider`, `CylinderCollider`, `TriangleCollider` and
`ConvexHullCollider` all derive from **`ColliderBase`**, which is *not* an `ICollider`. A single
box therefore still has to be wrapped in a `CompoundCollider`. Worked example,
`Client/Program.cs:333`:

```csharp
new BodyComponent
{
    Collider = new CompoundCollider
    {
        Colliders =
        {
            // Roughly humanoid; entity origin is at the feet, so lift the
            // box's center to half its height.
            new BoxCollider { Size = new Vector3(0.6f, 1.8f, 0.6f),
                              PositionLocal = new Vector3(0, 0.9f, 0) }
        }
    }
}
```

`ColliderBase` gives every primitive shape three things:

- `float Mass` = 1f. **Mass lives on the collider, not the body.** `AttachInner` feeds the
  compound builder's accumulated inertia into `BodyDescription.CreateDynamic`, so a compound's
  mass is the sum of its children's `Mass` and the inertia tensor is derived from the shapes.
  There is no `BodyComponent.Mass` to set.
- `Vector3 PositionLocal` (serialized alias `LinearOffset`) and `Quaternion RotationLocal` —
  the shape's offset **relative to the entity origin**. GLTF humanoids in this repo have their
  origin at the feet, hence the `Y = 0.9f` lift above; forget it and the collider straddles the
  floor.

Shape defaults: `BoxCollider.Size` (1,1,1) · `SphereCollider.Radius` 0.5 ·
`CapsuleCollider.Radius` 0.35 / `Length` 0.5 · `CylinderCollider.Radius` 0.5 / `Length` 1 ·
`TriangleCollider.A/B/C` · `ConvexHullCollider.Hull` (required `DecomposedHulls`, cached in a
`ConditionalWeakTable`).

`MeshCollider : ICollider` is assigned directly (not inside a compound): `required Model Model`,
`Mass` 1, `Closed` true ("Physics assume that the mesh's surface doesn't have any holes"),
world scale resolved by `MeshCollider.ComputeMeshScale`.

`Stride.CommunityToolkit.Bepu.Colliders` adds more `ColliderBase` shapes: `ConeCollider`,
`TorusCollider`, `TriangularPrismCollider`, `TeapotCollider`.

Every shape property setter carries the XML remark *"Changing this value will reset some of the
internal physics state of this body"* — build the shape before attaching, don't tweak per frame.

## BepuSimulation

Get it with `entity.GetSimulation()` — extension class `BepuSimulationExtensions`, **global
namespace, in `Stride.BepuPhysics.dll`** (not the CommunityToolkit). It resolves
`services.GetOrCreate<BepuConfiguration>()` and then `SceneBasedSimulationSelector.Shared.Pick(...)`.
The repo caches it once in `Start()` (`Client/Program.cs:223`):

```csharp
camera = rootScene.GetCamera();
simulation = camera?.Entity.GetSimulation();
```

`BepuConfiguration` (`Stride.BepuPhysics`) holds `List<BepuSimulation> BepuSimulations`. This repo
has no Bepu entry in the game settings, so at startup it logs *"Creating a default configuration
for Bepu as none were set up in your game's settings."* and creates exactly one simulation — that
warning is expected, not a bug. `SceneBasedSimulationSelector` picks: single simulation → it;
else first whose `AssociatedScene` matches the entity's scene; else first with a null scene; else
the first in the list.

Configurable state and defaults:

| member | default | note |
|---|---|---|
| `FixedTimeStep` | 16.667 ms (166666 ticks) | 60 Hz; `FixedTimeStepSeconds` is the lossy double view |
| `TimeScale` | 1 | stacks with `GameTime.Factor` |
| `MaxStepPerFrame` | 3 | death-loop guard |
| `PoseGravity` | `(0, -9.8, 0)` | `StridePoseIntegratorCallbacks` |
| `PoseLinearDamping` / `PoseAngularDamping` | 0.05 / 0.05 | fraction of velocity removed per unit time |
| `SolverIteration` | 8 | `Simulation.Solver.VelocityIterationCount` |
| `SolverSubStep` | 1 | `Simulation.Solver.SubstepCount` |
| `SoftStartDuration` / `SoftStartSubstepFactor` | 1 s / 4 | extra substeps at startup; `ResetSoftStart()` |
| `ThreadCount` | -1 | auto: `ProcessorCount - 2` when > 4 cores |
| `Enabled` / `ParallelUpdate` / `Deterministic` | true / true / false | |
| `UsePerBodyAttributes` | true | required for `BodyComponent.Gravity` to matter |
| `CollisionMatrix` | `CollisionMatrix.All` | public field, see filtering |

Also: `GetComponent(BodyHandle)` / `GetComponent(StaticHandle)` /
`GetComponent(CollidableReference)`, `await sim.NextUpdate()` / `await sim.AfterUpdate()`, and a
raw `Simulation` property documented as *"inherently unsupported and unsafe"*.

For per-tick gameplay code, implement `ISimulationUpdate` (`Stride.BepuPhysics.Components`) on a
component: `SimulationUpdate(BepuSimulation sim, float dt)` runs right before each physics tick,
`AfterSimulationUpdate` right after. That runs at the fixed physics rate, unlike `SyncScript.Update`.

## Raycasting, sweeps and overlaps

On `BepuSimulation` (`Stride.BepuPhysics`), all taking a trailing
`CollisionMask collisionMask = CollisionMask.Everything`:

- `bool RayCast(in Vector3 origin, in Vector3 dir, float maxDistance, out HitInfo result, ...)` —
  closest hit. `dir` must be **normalized** (XML: "The normalized direction the ray is facing").
- `RayCastPenetrating(origin, dir, maxDistance, Span<HitInfoStack> buffer, ...)` → enumerator, and
  an `ICollection<HitInfo>` overload. Hits are **not sorted**; an undersized buffer keeps the
  closest ones. The collection overload appends, it does not clear.
- `SweepCast<TShape>(in TShape shape, in RigidPose pose, in BodyVelocity velocity, float maxDistance,
  out HitInfo, ...)` plus a `SweepCastPenetrating` pair.
- `Overlap<TShape>(in TShape shape, in RigidPose pose, ...)` → collidables;
  `OverlapInfo<TShape>(...)` → per-sub-shape `OverlapInfo`.

`TShape` is a **raw Bepu shape** (`BepuPhysics.Collidables.IConvexShape`: `Box`, `Sphere`,
`Capsule`, …), *not* a Stride `*Collider` class, and the pose is `BepuPhysics.RigidPose`. The
`ToBepu()` / `ToStride()` / `ToNumeric()` converters in `BepuAndStrideExtensions`
(global namespace, `Stride.BepuPhysics.dll`) bridge the two vector/quaternion families.

Results:

```csharp
public readonly record struct HitInfo(Vector3 Point, Vector3 Normal, float Distance,
                                      CollidableComponent Collidable, int ChildIndex);
public readonly record struct OverlapInfo(CollidableComponent Collidable,
                                          Vector3 PenetrationDirection, float PenetrationLength);
```

`HitInfo` sorts by `Distance` (`IComparable<HitInfo>`). Get back to gameplay through
`hitInfo.Collidable.Entity` — `CollidableComponent` is an `EntityComponent`.

### CommunityToolkit mouse picking

`Stride.CommunityToolkit.Bepu.CameraComponentExtensions` provides:

```csharp
bool Raycast(this CameraComponent camera, Vector2 screenPosition, float maxDistance,
             out HitInfo hit, CollisionMask collisionMask = CollisionMask.Everything);
bool RaycastMouse(this CameraComponent camera, ScriptComponent component, float maxDistance,
                  out HitInfo hit, CollisionMask collisionMask = CollisionMask.Everything);
```

Under the hood it is two lines: `GetPickRay(camera, screenPosition)` (from
`Stride.CommunityToolkit.Engine.CameraComponentExtensions`, the *core* toolkit) followed by
`camera.Entity.GetSimulation().RayCast(ray.Position, ray.Direction, maxDistance, out hit, mask)`.
`GetPickRay` → `CalculateRayFromScreenPosition` → `ScreenPointToRay`, which does:

```csharp
ndc.X = screenPosition.X * 2f - 1f;
ndc.Y = 1f - screenPosition.Y * 2f;      // y=0 maps to NDC +1, i.e. the TOP of the screen
// unproject at z=0 and z=1 through Matrix.Invert(camera.ViewProjectionMatrix), normalize far-near
```

Coordinate space and units:

- `screenPosition` is **Stride-normalised 0..1 with (0,0) at the top-left**, which is exactly what
  `game.Input.MousePosition` returns — pass it straight through, as `Client/Program.cs:263` does.
  **The shipped XML docs on `CalculateRayFromScreenPosition` and `ScreenPointToRay` say
  "(0,0) at the bottom-left". That is wrong; the arithmetic `1f - y * 2f` is top-left.** Trust the
  code.
- `maxDistance` is in world units along the ray (the repo passes `100f`).
- It reads `camera.ViewProjectionMatrix` as-is. If the camera moved this frame and
  `CameraComponent.Update()` has not run, the ray is one frame stale.

Repo usage (`Client/Program.cs:263-277`):

```csharp
var hitResult = camera.Raycast(game.Input.MousePosition, 100f, out HitInfo hitInfo);
if (hitResult)
{
    var rigidBody = hitInfo.Collidable.Entity.Get<BodyComponent>();
    if (rigidBody != null)
    {
        rigidBody.Awake = true;                              // required, see below
        rigidBody.ApplyImpulse(new Vector3(0, 3, 0), Vector3.Zero);
    }
}
```

Right below it, `Client/Program.cs:281-286` shows the **non-physics** alternative for entities with
no collidable: `camera.GetPickRay(mouse)` + `ModelComponent.BoundingBox.Intersects(ref ray)`.

## Collision layers, masks and groups

- `enum CollisionLayer : uint { Layer0 … Layer31 }` — one per collidable
  (`CollidableComponent.CollisionLayer`).
- `[Flags] enum CollisionMask : uint { None, Layer0 = 1 … Layer31 = 0x80000000, Everything }` —
  a set of layers; every query method takes one.
- `CollisionLayersExtension`: `mask.IsSet(layer)`, `layer.IsSetIn(mask)`, `layer.ToMask()`.
- `BepuSimulation.CollisionMatrix` (public field, `Stride.BepuPhysics.Definitions.CollisionMatrix`,
  default `All`) decides which layers collide: `Get(l1, l2)`, `Set(l1, l2, shouldCollide)`,
  `Get(layer) → CollisionMask`. It is 528 bits / 17 ints; XML warns it is "VERY large, prefer
  referencing it directly instead of passing it around". The 32 `Layer0`…`Layer31` properties on
  `BepuSimulation` are per-row views of it.
- `CollisionGroup` (Id + `IndexA/B/C`) refines filtering *after* layers: objects sharing a non-zero
  `Id` do not collide when the absolute difference of their indices is **less than two**. Chain
  example from the XML docs: A/B/C all `Id = 1`, `IndexA` = 0/1/2 → A and C collide with each
  other but neither collides with B.
- `BepuSimulation.ShouldPerformPhysicsTest(mask, collidable)` answers the filter question directly.

## Impulses, velocity and sleeping

`BodyComponent` (`Stride.BepuPhysics`) offers exactly three impulse methods:

```csharp
void ApplyImpulse(Vector3 impulse, Vector3 impulseOffset);  // offset = world-space, from center of mass
void ApplyLinearImpulse(Vector3 impulse);
void ApplyAngularImpulse(Vector3 impulse);
```

**There is no `ApplyForce`.** Don't look for one — use impulses, set `LinearVelocity` /
`AngularVelocity` directly, or use a constraint.

**All three carry the XML remark "Does not wake the body up."** A body that has gone to sleep
silently ignores the impulse, which is why the repo sets `rigidBody.Awake = true;` first
(`Client/Program.cs:274`). Sleeping is governed by `SleepThreshold` (0.01f squared combined
velocity; set it negative to guarantee the body never sleeps on its own) and
`MinimumTimestepCountUnderThreshold` (32 ticks). Setting `Awake = false` forces the body **and any
constraint-connected bodies** asleep.

Moving a body without impulses:

- `SetTargetPose(position, orientation)` — collides on the way; overwrites `LinearVelocity` and
  `AngularVelocity`, so set those *after* the call if at all.
- `Teleport(position, orientation)` / `SetPose(...)` — ignores overlaps; you must ensure the
  destination is clear or the body gets stuck in geometry.
- `BodyComponent.Position` is **not** `Transform.Position`: it is offset by `CenterOfMass`
  (the simulation writes the transform back as `pose.Position - rotate(CenterOfMass)`).

## Contact events

`Stride.BepuPhysics.Definitions.Contacts` (Stride.BepuPhysics.dll).

**`IContactEventHandler` is `[Obsolete]`** — "superseded by `IContactHandler`". Several of its
methods (`OnContactAdded`, `OnContactRemoved`, `OnPairCreated`, `OnPairUpdated`, `OnPairEnded`) are
`[Obsolete(..., error: true)]` and documented as "will never be called". Write new code against
`IContactHandler`:

```csharp
public interface IContactHandler
{
    bool NoContactResponse { get; }
    void OnStartedTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold> { }
    void OnTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold> { }
    void OnStoppedTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold> { }
}
```

Subscribe by assigning the handler to the collidable; the setter unregisters any previous handler
and refreshes material properties:

```csharp
body.ContactEventHandler = new MyHandler();
```

`Contacts<TManifold>` is a **`readonly ref struct`**: `EventSource` (the collidable the handler is
attached to), `Other`, `Simulation`, `IsSourceOriginalA` (whether normals are flipped from the
source's point of view), `Groups` (one `ContactGroup` per compound child hit), and it enumerates
to `Contact<TManifold>` skipping negative-depth contacts. `ComputeImpactForce(contact)` derives an
impact force from both bodies' `Previous*Velocity` and inverse masses divided by
`FixedTimeStepSeconds`. Because it is a ref struct you cannot stash it — copy the values you need.
Handlers run on physics worker threads (the obsolete overloads carry a `workerIndex`).

Ready-made trigger volume: `Stride.BepuPhysics.Definitions.Trigger : IContactHandler` —
`NoContactResponse => true` plus `OnEnter` / `OnLeave` events of type `TriggerDelegate`.

`OnTouching` and `OnStoppedTouching` do not fire for sleeping pairs.

## Debug visualisation

`Stride.BepuPhysics.Debug` (transitive, already available):

- `DebugRenderComponent : SyncScript` — `bool Visible` and `Keys Key` defaulting to **F11**
  (enum value 100, matches `Stride.Input.Keys.F11`). Its `Update()` toggles `Visible` on key press.
- `DebugRenderProcessor.OnSystemAdd` auto-adds a `SinglePassWireframeRenderFeature` to
  `SceneSystem.GraphicsCompositor.RenderFeatures` if one isn't present, hooks
  `CollidableProcessor.OnPostAdd`/`OnPreRemove`, and draws one `WireFrameRenderObject` per
  collidable. `Mode` is `SynchronizationMode.Physics` (read the physics pose — reveals
  entity/physics desync) or `.Entity`.
- Adding it to **one** entity enables wireframes for every Bepu collidable in the scene.

The CommunityToolkit wrapper `Stride.CommunityToolkit.Bepu.DebugRenderComponentScript` waits until
`SceneSystem.SceneInstance.VisibilityGroups.Count > 0`, then adds a `DebugRenderComponent` with the
script's `Visible` value:

```csharp
// in Start()
new Entity("PhysicsDebug") { new DebugRenderComponentScript { Visible = true } }.Scene = rootScene;
```

Sleeping bodies are drawn in a lighter colour. `CollidableGizmoScript` is a related CTK helper.

**UNVERIFIED:** whether any of this renders on this Linux/Vulkan setup. It was not run. The
wireframe path is a custom `RootRenderFeature` backed by an `.sdsl` single-pass wireframe mixin,
and this repo has already had to disable `ParticleEmitterRenderFeature` (AccessViolation on
Vulkan), `FastTextRenderer`/`DebugTextSystem`/`AddProfiler` (use-after-unmap), and SSR
(`R11G11B10_Float` unsupported) — see the comments in `Client/Program.cs` `Start()`. Treat it as
untested; per project convention, enable it and ask Sebastian to look rather than screenshotting.

## Where physics does and doesn't belong here

Factual, from what the repo does today:

- **Client only.** `Client/Program.cs` is the sole consumer: the cached `BepuSimulation`
  (`:51`, `:223`), the LMB raycast + impulse (`:263-277`), and `CreateDummy()`'s `BodyComponent`
  (`:328-350`). `game.Add3DGround()` (CTK `GameExtensions.CreateGround`) attaches a
  `StaticComponent` with a `CompoundCollider` — that is the floor the dummy rests on.
- **Deliberately collider-free views.** `Client/View/ObjectViewFactory.cs:23-24` creates networked
  crates with `game.Create3DPrimitive(PrimitiveModelType.Cube, new() { IncludeCollider = false })`.
  `Bepu3DPhysicsOptions` (`Stride.CommunityToolkit.Bepu`) otherwise defaults to a dynamic
  `BodyComponent` + empty `CompoundCollider`, and `EntityExtensions.AddBepuPhysics` appends a shape
  matching the primitive; with `IncludeCollider = false` the component is still added but stays
  shapeless.
- **Server and shared code contain no physics.** `grep -rn "Bepu" Common Server` returns zero hits.
  Movement is `Common/PlayerMovement.Step` — a pure `System.Numerics` function run by both client
  prediction and server authority. Shooting is `Server/WeaponSystem.Raycast` (`:100`), an analytic
  ray/sphere test over `ServerObject`s and lag-compensated player positions via
  `Common/GunMath.HitDistance`.
- Consequence: anything a Bepu body decides is local to one client and is not replicated by any
  code in this repo.

## Gotchas

1. **Primitive colliders are `ColliderBase`, not `ICollider`** — wrap even a single `BoxCollider`
   in a `CompoundCollider`. Only `CompoundCollider`, `MeshCollider`, `EmptyCollider` implement
   `ICollider`.
2. **Mass is on `ColliderBase.Mass`** (default 1), not on the body. Compound mass = sum of children.
3. **Collider offsets are relative to the entity origin.** Feet-origin GLTF ⇒
   `PositionLocal.Y = height / 2`.
4. **`ApplyImpulse` does not wake a sleeping body** — set `Awake = true` first.
5. **No `ApplyForce` exists.** Impulses, velocities or constraints only.
6. **`BodyComponent.Position` ≠ `Transform.Position`** — offset by `CenterOfMass`.
7. **`InterpolationMode` defaults to `None`**, so props visibly step at the 60 Hz physics rate.
   Set `Interpolated` (one tick of latency, smooth) or `Extrapolated` (lower latency, jerkier).
8. **CTK camera raycast takes 0..1 top-left screen coords** (= `Input.MousePosition`); the shipped
   XML comment claiming bottom-left is wrong.
9. **`Awake = false` also sleeps constraint-connected bodies.**
10. **Mutating `Collider`, `Mass`, `PositionLocal`, `RotationLocal`, `Size`, … "resets some of the
    internal physics state of this body."** Configure shapes before attaching.
11. **`SweepCast` / `Overlap` need raw Bepu `IConvexShape`s and a `RigidPose`**, not Stride
    collider classes. Convert with `ToBepu()` / `ToStride()`.
12. **Nested `BodyComponent`s become physics parent/child automatically** via `FindParentBody`.
13. **No Bepu game-settings configuration exists here**, so one default simulation is created and
    `BepuService` logs a warning at startup. Expected.
14. **Legacy Bullet (`Stride.Physics`, `RigidbodyComponent`) is the wrong API for this repo** — see
    the top of this document.
