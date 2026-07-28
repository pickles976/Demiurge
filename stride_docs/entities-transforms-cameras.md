# Entities, Transforms, Cameras (Stride 4.3.0.2507)

Reference for this repo. Every claim below is from decompiled engine source, engine XML docs, or
repo usage. To re-check anything:

```bash
ilspycmd -t Stride.Engine.TransformComponent ~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/Stride.Engine.dll
ilspycmd -l class ~/.nuget/packages/stride.communitytoolkit/1.0.0-preview.62/lib/net10.0/Stride.CommunityToolkit.dll
```

XML docs sit beside each dll as `Stride.*.xml`. Single-quote backtick generics in bash.

**Three packages, constantly confused.** Each helper below is tagged:

| Tag | Package | Path |
| --- | --- | --- |
| **[core]** | Stride.Engine / Stride.Core.Mathematics | `~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/` |
| **[toolkit]** | Stride.CommunityToolkit | `~/.nuget/packages/stride.communitytoolkit/1.0.0-preview.62/lib/net10.0/` |
| **[toolkit.bepu]** | Stride.CommunityToolkit.Bepu | `~/.nuget/packages/stride.communitytoolkit.bepu/1.0.0-preview.62/lib/net10.0/` |

---

## 1. The four things that will actually bite you

1. **`WorldMatrix` inside `SyncScript.Update()` is last frame's value.** It is recomputed in the
   *Draw* phase. See §4.
2. **`ViewMatrix` / `ViewProjectionMatrix` are also last frame's** inside `Update()`, for the same
   reason. See §7.
3. **A wrong `ModelNodeLinkComponent.NodeName` fails silently** — the item snaps to the model's
   origin instead of the bone. No log, no throw. See §5.
4. **`Quaternion a * b` means "apply a, then b"** — reversed from Unity/GLM. See §6.

---

## 2. Entity and EntityComponent

`Entity` — sealed, `Stride.Engine.Entity` **[core]**.

```csharp
// Collection-initializer syntax works because Entity : IEnumerable<EntityComponent> + Add(EntityComponent).
var entity = new Entity($"Player_{player.Id}")
{
    new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/cat_orange.gltf")),
    new PlayerViewScript { Player = player, Registry = registry },
    animations,
};
entity.Transform.Position = player.Position.ToStride();
entity.Scene = scene;                       // this is what puts it in the world
```

(`Client/View/PlayerViewFactory.cs:32-39`.)

- Every public constructor —
  `Entity(string name = null, Vector3 position = default, Quaternion? rotation = null, Vector3? scale = null)`
  — creates and adds a `TransformComponent`. Only the private deserialization ctor skips it, so
  `entity.Transform` is never null for entities you construct.
- Component access: `Get<T>()`, `Get<T>(int index)` (negative index counts from the end),
  `GetAll<T>()`, `GetOrCreate<T>()`, `Add(component)`, `Remove<T>()`, `Remove(component)`,
  `RemoveAll<T>()`.
- `EntityManager` is the `SceneInstance` processing the entity — null until it is in a live scene.

### Entering and leaving the world

`entity.Scene = scene` and `scene.Entities.Add(entity)` are the **same operation** — the `Scene`
setter removes from the old `Scene.Entities` and adds to the new one.

Two sharp edges in the `Entity.Scene` property:

- **Getter returns `this.FindRoot().SceneValue`.** A child entity reports its *root's* scene. It is
  not a "which scene is my immediate owner in" test.
- **Setter throws if the entity has a parent:**
  `InvalidOperationException("This entity is another entity's child. Detach it before changing its scene.")`
  Setting it to `null` is allowed and detaches from the parent instead.

`Client/View/PlayerViewFactory.cs:46-50` does both a `scene.Entities.Remove(...)` and
`playerEntity.Scene = null`; the second is a harmless no-op after the first.

---

## 3. Parenting

`AddChild`, `RemoveChild`, `GetChild`, `GetParent`, `SetParent`, `FindChild`, `FindRoot` are
**extension methods** in `Stride.Engine.EntityTransformExtensions` **[core]** — not members of
`Entity`. `AddChild(p, c)` is literally `p.Transform.Children.Add(c.Transform)`.

```csharp
parent.AddChild(child);            // == parent.Transform.Children.Add(child.Transform)
child.Transform.Parent = other;    // re-parent: removes from old Children, adds to new
```

**`Transform.Children.Add` throws if the child already has a parent:**
`InvalidOperationException("This TransformComponent already has a Parent, detach it first.")`
(`TransformComponent.TransformChildrenCollection.OnTransformAdded`). Re-parenting must go through
`Transform.Parent = x`, which does the remove-then-add for you.

Searching a hierarchy: `Stride.CommunityToolkit.Engine.EntitySearchExtensions` **[toolkit]** adds
`GetComponentInChildren<T>`, `GetComponentsInDescendants<T>`, `GetComponentsInParent<T>`.

---

## 4. TransformComponent

`Stride.Engine.TransformComponent` **[core]**.

`Position`, `Rotation`, `Scale`, `LocalMatrix`, `WorldMatrix` are **public fields**, not properties.
Ctor defaults: `Scale = Vector3.One`, `Rotation = Quaternion.Identity`, `UseTRS = true`.

- `Position` / `Rotation` / `Scale` are **local** — relative to the parent (or, for a root entity,
  to `Scene.WorldMatrix`).
- `UpdateLocalMatrix()` → `Matrix.Transformation(Scale, Rotation, Position)`, only when `UseTRS`.
- `WorldMatrix = LocalMatrix * Parent.WorldMatrix` — literally
  `Matrix.Multiply(ref LocalMatrix, ref Parent.WorldMatrix, ref WorldMatrix)`.
  This is **row-vector convention**: child on the left, parent on the right, points transform as
  `v * M`. With no parent it composes against `Scene.WorldMatrix`; with no scene, `WorldMatrix = LocalMatrix`.
- If `TransformLink` is non-null (set by `ModelNodeLinkProcessor`, §5) it **replaces the parent**:
  `WorldMatrix = LocalMatrix * link.ComputeMatrix(...)`. The `Parent` branch is never reached.

### THE TIMING RULE

`TransformProcessor` (`Stride.Engine.Processors.TransformProcessor`, `Order = -200`) recomputes every
`WorldMatrix` in its **`Draw(RenderContext)`** override — *not* in `Update`.

The frame runs: `ScriptSystem.Update` (all your `SyncScript.Update()` bodies) → … →
`SceneSystem.Update` → then the Draw phase, where `SceneSystem.Draw` → `SceneInstance.Draw` →
processors' `Draw` → `GraphicsCompositor.Draw`. `ScriptSystem` is registered before `SceneSystem` in
`Stride.Engine.Game`'s `GameSystems` list.

**Therefore: inside `Update()`, `WorldMatrix` holds the value computed during the *previous* frame's
Draw. Writing `Transform.Position` this frame does not change `WorldMatrix` until Draw.**

```csharp
// WRONG inside SyncScript.Update() — muzzle is one frame behind.
Entity.Transform.Position = newPos;
var muzzle = Entity.Transform.WorldMatrix.TranslationVector;

// RIGHT — force the recompute first. Walks up the parent chain, so O(depth), cheap for one entity.
Entity.Transform.Position = newPos;
Entity.Transform.UpdateWorldMatrix();
var muzzle = Entity.Transform.WorldMatrix.TranslationVector;
```

`UpdateWorldMatrix()` = `UpdateLocalMatrix()` then `UpdateWorldMatrixInternal(recursive: true)`,
which refreshes ancestors on the way up. It does **not** refresh descendants.

If the entity is a scene root with no `TransformLink`, reading `Position` / `Rotation` directly is
equivalent and free — that is why `ItemAttachScript`'s root-following branch can read
`owner.Transform.Position` (`Client/View/ItemScripts.cs:73`): `Player_{id}` entities are added via
`entity.Scene = scene`, so they are roots.

### Processor ordering inside Draw

| Processor | Order | Does |
| --- | --- | --- |
| `ModelNodeLinkProcessor` | -300 | assigns / refreshes `Transform.TransformLink` |
| `TransformProcessor` | -200 | recomputes all `LocalMatrix` + `WorldMatrix` |
| `CameraProcessor` | -10 | binds cameras to compositor slots |

`TransformProcessor.UpdateTransformations` walks plain roots first, then does a **second pass** over
bone-linked transforms — so a bone-linked child reads an already-posed skeleton.

### Getting world position / rotation out of a child

All of these read `WorldMatrix` and **none of them refresh it** — apply the timing rule first.

```csharp
var worldPos = transform.WorldMatrix.TranslationVector;
transform.WorldMatrix.Decompose(out var scale, out var rot, out var pos);

// Stride.Engine.EntityTransformExtensions [core]:
transform.GetWorldTransformation(out var pos, out var rot, out var scale);
var world = transform.LocalToWorld(localPoint);
var local = transform.WorldToLocal(worldPoint);
```

---

## 5. Bone attachment

### What the engine offers

`ModelNodeLinkComponent` **[core]**:

- `Target` — the `ModelComponent` whose skeleton to read. **If null, falls back to the parent
  entity's `ModelComponent`.**
- `NodeName` — the bone/joint name.
- `IsValid` — `ValidityCheck` rejects self-targets, target-is-own-descendant, and cyclic parents.

Engine XML doc, verbatim: *"Stride does not support as target entities that themself linked to
another bone."* → **bone links do not chain.**

`ModelNodeLinkProcessor.Draw` (Order -300) creates a `ModelNodeTransformLink` and assigns it to
`entity.Transform.TransformLink`. Removing the component nulls the link back out.

The link supplies the **parent** matrix only. The child's own `Position` / `Rotation` / `Scale` are
still applied on top as its local matrix — so they become **bone-relative**.

### The silent failure

`ModelNodeTransformLink.ComputeMatrix` resolves the bone by **exact string match** against
`parentModelComponent.Skeleton.Nodes[i].Name`, caches the index, and returns
`skeleton.NodeTransformations[nodeIndex].WorldMatrix`.

```csharp
if (skeleton != null) {
    if (nodeIndex < nodes.Length) { matrix = nodeTransformations[nodeIndex].WorldMatrix; return; }
}
matrix = parentModelComponent.Entity.Transform.WorldMatrix;   // <-- silent fallback
```

**If the name is not found, `nodeIndex` stays `int.MaxValue` and it silently returns the model
entity's own `WorldMatrix`.** No warning, no exception. A typo in `ItemCosmetics.SlotSockets`
presents as *"the item sits at the player's origin"*, never as an error. Dump the gltf's node names
when adding a slot.

### What this repo does

`Client/View/ItemScripts.cs:42-77` + `Client/View/ItemCosmetics.cs`.

```csharp
Entity.Add(new ModelNodeLinkComponent { Target = ownerModel, NodeName = node });
Entity.Transform.Position = socket.Seat;       // bone-relative
Entity.Transform.Rotation = socket.Rotation;   // bone-relative
boneLinked = true;                             // latch; slot changes are despawn/respawn
```

- The item entity **stays at the scene root** — no `AddChild`. The link drives its world transform
  regardless of hierarchy, and root-level entities keep `ObjectViewFactory.DestroyView`'s
  find-by-name working.
- Slot → socket table (`ItemCosmetics.SlotSockets`): `Hand → "right_hand"`,
  `Chest → "torso"`, `Back → "torso"`, `Head → "head"`.
- A null `Node` means "follow the owner's root transform manually", recomputed every frame
  (`Client/View/ItemScripts.cs:73-74`).

> **Flagged aside, not a bug to fix.** The root-following branch writes
> `owner.Transform.Rotation * socket.Rotation`. Under Stride's composition order (§6) that reads as
> *owner first, then socket* — the opposite nesting from the `Vector3.Transform(socket.Seat,
> owner.Transform.Rotation)` line directly above it. It is inert today: every slot in `SlotSockets`
> has a non-null `Node`, so this branch runs only for an unknown slot off the wire, whose fallback
> `Socket` has `Rotation = Quaternion.Identity`. Noted so nobody "discovers" it twice.

---

## 6. Coordinate conventions

**Right-handed, Y up, forward = -Z.** Verified three ways in `Stride.Core.Mathematics` **[core]**:

- `Matrix.Forward => new Vector3(-M31, -M32, -M33)` (XML doc: *"that is -M31, -M32, and -M33"*).
- `Matrix.LookAtRH(eye, target, up)` computes `zaxis = normalize(eye - target)` — camera-space +Z
  points *backwards*, so the camera looks down **-Z**.
- `CameraComponent.Update` builds `Matrix.PerspectiveFovRH` / `Matrix.OrthoRH`.

`Vector3.UnitZ` therefore points *backward*. `ThirdPersonCameraScript` relies on this: it places the
camera at `Target + rotatedForward * Radius + Vector3.UnitY * Height` with `forward = Vector3.UnitZ`
— i.e. behind the target along +Z (`Client/View/PlayerCamera.cs:104-107`).

### Pointing an entity at something

The repo's canonical idiom — build a *view* matrix, invert it to get a *world* rotation
(`Client/View/PlayerCamera.cs:110-112`):

```csharp
// LookAtRH gives a view (world->camera) matrix; invert it to get the camera's world rotation
var lookAt = Matrix.LookAtRH(desiredPosition, Target, Vector3.UnitY);
lookAt.Invert();
var desiredRotation = Quaternion.RotationMatrix(lookAt);
```

### Quaternion composition is REVERSED from Unity/GLM

`Quaternion.Multiply(left, right)` computes
`result.X = x2*w + x*w2 + y2*z - z2*y` (note the cross-term sign). That equals the Hamilton product
`right * left`.

> **In Stride, `a * b` means "apply `a` first, then `b`."**
> Reading left-to-right = order of application. Under Unity/GLM you would write the same intent as
> `b * a`.

Worked example, `Client/Program.cs:323`:

```csharp
// X first (tilt the sun 30° down), THEN Y (spin it 180° about world up).
entity.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-30.0f))
                          * Quaternion.RotationY(MathUtil.DegreesToRadians(-180.0f));
```

Take the light's forward `(0,0,-1)`: `RotationX(-30°)` sends it to `(0,-0.5,-0.866)` (angled down and
forward), then `RotationY(-180°)` flips it to `(0,-0.5,+0.866)` — still shining down, now from the
other side. Swap the two factors and you get a different direction.

This is **consistent with the matrix convention**: `WorldMatrix = LocalMatrix * Parent.WorldMatrix`
(child-then-parent, row-vector), so the quaternion equivalent of a child's world rotation is
`childLocal * parentWorld`. Same left-to-right reading. `ItemCosmetics.HandRotation`
(`Client/View/ItemCosmetics.cs:30`) is `RotationX(π/2) * RotationZ(π)` = X first, then Z.

`Quaternion.RotationY(a)` = `(0, sin(a/2), 0, cos(a/2))`. Angles are **radians**; only
`CameraComponent.VerticalFieldOfView` is in degrees. Use `MathUtil.DegreesToRadians`.

### Rotating a vector by a quaternion

Three equivalent forms, all **[core]**:

```csharp
Vector3.Transform(ref offset, ref rigRotation, out var rotated);   // preferred; expands q to a matrix inline
var rotated = Vector3.Transform(offset, rigRotation);              // by-value overload
var rotated = rigRotation * offset;                                // Quaternion.operator *(in Quaternion, in Vector3)
```

The operator form is `Conjugate(q) *stride v *stride q`, which with the reversed multiply is the
standard `q v q⁻¹` — same answer, two quaternion products, slower. `ThirdPersonCameraScript` and
`ItemAttachScript` both use `Vector3.Transform`.

**Matrix overloads differ by return type — this catches people:**

| Call | Returns | Use for |
| --- | --- | --- |
| `Vector3.Transform(v, Matrix)` | **`Vector4`** | raw homogeneous transform |
| `Vector3.TransformCoordinate(v, Matrix)` | `Vector3` | **points** (divides by w) |
| `Vector3.TransformNormal(v, Matrix)` | `Vector3` | **directions** (ignores translation) |

### Scenes have transforms too

`Scene` **[core]** has a `Vector3 Offset` field and a `Matrix WorldMatrix`, and scenes nest via
`Scene.Parent` / `Scene.Children`. Root entities compose against `Scene.WorldMatrix`.

---

## 7. CameraComponent

`Stride.Engine.CameraComponent` **[core]**, an `ActivableEntityComponent` (so it has `Enabled`).

Public **fields**: `ViewMatrix`, `ProjectionMatrix`, `ViewProjectionMatrix`, `Frustum`, `Slot`.

Properties: `Projection` (`Perspective` / `Orthographic`), `VerticalFieldOfView` (**degrees**,
default 45, range 1–179), `OrthographicSize` (10), `NearClipPlane` (0.1), `FarClipPlane` (1000),
`UseCustomAspectRatio` / `AspectRatio` (16:9) / `ActuallyUsedAspectRatio`, `UseCustomViewMatrix`,
`UseCustomProjectionMatrix`.

`Update(float? screenAspectRatio)` derives `ViewMatrix` by decomposing **the entity's `WorldMatrix`**,
transposing the rotation and negating the translated position; then builds the RH projection, sets
`ViewProjectionMatrix = ViewMatrix * ProjectionMatrix`, and rebuilds `Frustum`.

### When are the matrices valid?

`SceneCameraRenderer.UpdateCameraToRenderView` calls `camera.Update(viewport.AspectRatio)` from
**`CollectCore`** — inside the Draw phase. (`CameraProcessor.Draw` additionally calls `Update()`
only when `UseCustomAspectRatio` is set.)

**So during `SyncScript.Update()` the camera matrices are last frame's**, same as `WorldMatrix`. Two
options:

```csharp
// A) Refresh it yourself. Note Update() reads WorldMatrix, so refresh that first — the safe pair:
camera.Entity.Transform.UpdateWorldMatrix();
camera.Update();                      // parameterless overload keeps the previous aspect ratio

// B) Better: read the matrices from inside the Draw phase, where the compositor already refreshed them.
```

Option B is what `LineRenderer` does — it reads `Camera.ViewProjectionMatrix` from inside a
`SceneRendererBase.DrawCore` (`Client/Rendering/LineRenderer.cs:190`). Follow that pattern for
anything that projects.

Every toolkit projection helper repeats this warning in its own XML docs: *"This method does not
update the ViewMatrix or ProjectionMatrix before performing the transformation."*

---

## 8. World ↔ screen

Screen coordinates are **normalized**: `(0,0)` top-left, `(1,1)` bottom-right. `Input.MousePosition`
is in that space — which is why `ThirdPersonCameraScript` centers on `0.5` and
`MathExtensions.MousePosToScreenCoords` subtracts `(0.5, 0.5)`.

### Toolkit helpers

`Stride.CommunityToolkit.Engine.CameraComponentExtensions` **[toolkit]**:
`GetPickRay(screenPos) → Ray`, `CalculateRayFromScreenPosition → (near, far)`,
`ScreenPointToRay → (Vector4 near, Vector4 far)`, `ScreenToWorldPoint`, `ScreenToWorldRaySegment`,
`WorldToClip`, `WorldToScreenPoint` (3 overloads — the `GraphicsDevice` one multiplies by window
size), `LogicDirectionToWorldDirection`, `CalculateRayPlaneIntersectionPoint`.
All go through `Matrix.Invert(camera.ViewProjectionMatrix)`; none refresh it.

`Stride.CommunityToolkit.Bepu.CameraComponentExtensions` **[toolkit.bepu]**:
`Raycast(screenPos, maxDistance, out HitInfo, CollisionMask = Everything)` and `RaycastMouse(...)`.
These build the pick ray with the toolkit `GetPickRay`, then call `camera.Entity.GetSimulation().RayCast(...)`.

Both appear in `Client/Program.cs` — `Raycast` at :263 for physics bodies, `GetPickRay` + a manual
`BoundingBox.Intersects(ref ray)` at :285 for non-physical picking.

### Manual projection (what this repo prefers)

`Client/Core/MathExtensions.cs:41-70`:

```csharp
Vector4 clip = Vector4.Transform(new Vector4(worldPosition, 1f), viewProj);   // row-vector order
if (clip.W != 0) { clip.X /= clip.W; clip.Y /= clip.W; clip.Z /= clip.W; }    // NDC, [-1,1], +Y up
float mouseX = clip.X * 0.5f + 0.5f;
float mouseY = 1f - (clip.Y * 0.5f + 0.5f);                                   // flip to Y-down mouse space
```

**`W <= 0` means the point is behind the camera** and the divide blows up. `LineRenderer` guards with
`if (c0.W <= 1e-4f || c1.W <= 1e-4f) continue;` (`Client/Rendering/LineRenderer.cs:196`);
`WorldToMouse` does not — callers must handle off-screen results themselves.

---

## 9. Which camera actually renders

`CameraProcessor.Draw` **[core]** is the entire binding mechanism. For each **enabled**
`CameraComponent` it scans `compositor.Cameras` for the `SceneCameraSlot` whose
`Id == camera.Slot.Id` and sets `slot.Camera = camera`. `Slot` is a `SceneCameraSlotId` — a Guid
wrapper, not a reference.

- **Two enabled cameras claiming one slot throws**, at Draw time, not at construction:
  `"Unable to attach camera [X] ... Another camera, [Y], is enabled and already attached to this slot."`
- Disabling a camera detaches it from its slot.
- `SceneCameraRenderer.ResolveCamera` reads `Camera?.Camera` off its slot; if null it logs
  *"has no camera assigned to its Slot[...]"* and renders nothing.

`GraphicsCompositorHelper.CreateDefault` **[core]** — which `game.AddGraphicsCompositor()`
**[toolkit]** calls (`Client/Program.cs:136`) — creates **exactly one** `SceneCameraSlot` and sets
`compositor.Game = new SceneCameraRenderer { Child = forwardRenderer, Camera = thatSlot }`.

`GameExtensions.Add3DCamera` **[toolkit]** throws if there are no slots, renames slot **[0]**, and
assigns `Slot = cameras[0].ToSlotId()`. So in this repo the camera created at `Client/Program.cs:236`
owns the single slot.

### What a second camera actually requires

**Adding a `CameraComponent` alone does nothing.** You need all three:

1. `compositor.Cameras.Add(new SceneCameraSlot())`
2. `secondCamera.Slot = thatSlot.ToSlotId()`
3. Another `SceneCameraRenderer` bound to that slot, wired into the compositor's renderer graph —
   e.g. via `GraphicsCompositorExtensions.AddSceneRenderer` **[toolkit]**, which is how
   `Client/Program.cs:138` adds `LineSceneRenderer`.

To merely *switch* between two views, keep one slot and toggle `Enabled` — but **disable the old
camera before enabling the new one**, or step 2's attach throws.

`SceneCameraRenderer.RenderMask` (a `RenderGroupMask`) is the per-view culling filter — the hook for
"a second camera that only sees some entities".

Finding a camera: `scene.GetCamera()` / `scene.GetCamera(name)` in
`Stride.CommunityToolkit.Engine.SceneExtensions` **[toolkit]** returns the first
`CameraComponent` found, recursing into children (`Client/Program.cs:222`).

---

## 10. Gotcha index

| # | Gotcha | Verify in |
| --- | --- | --- |
| 1 | `WorldMatrix` in `Update()` is last frame's → call `UpdateWorldMatrix()` | `TransformProcessor.Draw` |
| 2 | `ViewMatrix` / `ViewProjectionMatrix` likewise | `SceneCameraRenderer.UpdateCameraToRenderView` |
| 3 | Wrong `NodeName` silently falls back to the model's root transform | `ModelNodeTransformLink.ComputeMatrix` |
| 4 | `entity.Scene = x` throws if the entity has a parent | `Entity.Scene` setter |
| 5 | `Transform.Children.Add` throws if the child already has a parent; use `Transform.Parent =` | `TransformChildrenCollection.OnTransformAdded` |
| 6 | `entity.Scene` on a child returns the **root's** scene | `Entity.Scene` getter (`FindRoot()`) |
| 7 | Two enabled cameras on one slot throws at Draw time | `CameraProcessor.AttachCameraToSlot` |
| 8 | `VerticalFieldOfView` is degrees; every rotation API is radians | `CameraComponent.Update` |
| 9 | `a * b` = apply `a` then `b` (reversed vs Hamilton/Unity) | `Quaternion.Multiply` source |
| 10 | `Vector3.Transform(v, Matrix)` returns `Vector4`; use `TransformCoordinate` / `TransformNormal` | `Vector3` overload list |
| 11 | Bone links cannot chain — no linking to an already-bone-linked entity | `ModelNodeLinkComponent` XML doc |
| 12 | `Position` / `Rotation` / `Scale` / `WorldMatrix` are **fields** — invisible to property reflection/binding | `TransformComponent` |
| 13 | `W <= 0` in a manual projection means "behind the camera"; guard before dividing | `LineRenderer.DrawCore` |
| 14 | `AddChild` / `FindRoot` / `GetWorldTransformation` are core **extensions**, not members | `EntityTransformExtensions` |
| 15 | Never `using System.Numerics;` in a file that uses Stride maths — see below | `Vector3` conversion operators |

### `System.Numerics` interop — don't add the `using`

`Common` speaks `System.Numerics` (no Stride dependency, by design), the Client speaks
`Stride.Core.Mathematics`. Adding `using System.Numerics;` to a Stride file makes **`Vector3`,
`Vector2`, `Quaternion` and `Matrix` all ambiguous** (CS0104) at every use site — 11 errors in
`Client/Program.cs` the one time it was tried.

You never need it. `Stride.Core.Mathematics.Vector3` declares **implicit conversions both ways**:

```csharp
public static implicit operator Vector3(System.Numerics.Vector3 v)
    => Unsafe.BitCast<System.Numerics.Vector3, Vector3>(v);
public static implicit operator System.Numerics.Vector3(Vector3 v)
    => Unsafe.BitCast<Vector3, System.Numerics.Vector3>(v);
```

So values cross the boundary on their own — a `Common` function returning
`System.Numerics.Vector3` assigns straight to `Transform.Position`, and Stride's `Vector3.Zero`
passes straight into a `Common` method. `BitCast`, so it's free. Verified in
`Stride.Core.Mathematics.dll`.
