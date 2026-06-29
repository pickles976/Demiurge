# Bullet Tracer (Line-Based) — Design

**Date:** 2026-06-29
**Status:** Approved (pending spec review)

## Summary

Render a client-side "tracer" streak for each gun shot by reusing the existing
`LineRenderer` (immediate-mode, world-space thick lines). Damage is handled by a
future server-side raycast; the tracer is a purely visual, client-only effect
that points roughly in the shot direction. One tracer per shot.

The previously-considered Stride particle-system approach was set aside: the
existing `LineRenderer` already draws exactly the kind of world-space streak we
need, with no asset authoring or pooling.

## Behavior

- **Style:** Instant streak + fade. The full `barrel → endpoint` segment appears
  immediately on the shot, then fades to transparent over ~0.1s. No travel, no
  width animation (LineRenderer width is global).
- **Endpoint:** Cursor raycast. On shot, cast from the cursor like
  `Program.cs:131` (`camera.Raycast(Input.MousePosition, maxRange, out HitInfo)`).
  - Hit → `endpoint = hit.Point` (streak stops at the wall/target).
  - Miss → `endpoint = rayOrigin + rayDir * maxRange` (straight streak to a far
    point along the cursor ray).
- **Direction:** `barrel → endpoint`, where `barrel = GetBarrelPosition()`. The
  barrel and camera origins differ slightly (minor parallax) but both converge
  on the aimed world point, so the streak reads correctly.

## Components

### 1. `TracerManager` (static) — `Client/TracerManager.cs`

Parallels `LineRenderer`'s static immediate-mode style.

- `private struct Tracer { Vector3 Start; Vector3 End; float Age; float Lifetime; Color BaseColor; }`
- `Spawn(Vector3 start, Vector3 end, Color color, float lifetime)` — adds a tracer.
- `Update(float dt)` — for each live tracer:
  - `Age += dt`
  - `alpha = 1 - Age / Lifetime` (clamped ≥ 0)
  - `LineRenderer.DrawLine(Start, End, BaseColor * alpha)`
  - remove when `Age >= Lifetime`.

The alpha relies on the `Color` parameter `LineRenderer.DrawLine` already accepts;
the renderer's `NonPremultiplied` blend state handles the fade. No changes to
`LineRenderer`.

### 2. `TracerSystem` (SyncScript) — tick driver

A tiny `SyncScript` added once to the scene. `Update()` calls
`TracerManager.Update(dt)` once per frame. Kept separate from `GunScript` so that
networked/other-player tracers can feed the same manager later without each gun
ticking the shared list.

### 3. `GunScript.OnTriggerPull()` — spawn point

Replaces the existing `// TODO: get barrel position and spawn bullet`.

- `var barrel = GetBarrelPosition();`
- Resolve the camera via the existing
  `PlayerEntity → PlayerScript → CameraEntity → CameraComponent` chain.
- Compute the cursor world-ray once; raycast along it (mirrors `Program.cs`):
  `endpoint = hit ? hit.Point : ray.Origin + ray.Direction * maxRange`.
- `TracerManager.Spawn(barrel, endpoint, Color.White, TracerLifetime);`

Fire-rate / ammo gating is unchanged (already in `OnTriggerPull`).

### Targeted refactor

The camera-lookup null-chain is currently duplicated in `DrawAimLine` and now
needed in `OnTriggerPull`. Extract `private bool TryGetCamera(out CameraComponent camera)`
in `GunScript` and use it in both call sites. In-scope only; no unrelated cleanup.

## Data flow

```
OnTriggerPull (mouse down, gated by fire rate/ammo)
  → barrel = GetBarrelPosition()
  → endpoint = cursor raycast (hit.Point, else far point along ray)
  → TracerManager.Spawn(barrel, endpoint, White, lifetime)

each frame:
  TracerSystem.Update → TracerManager.Update(dt)
    → LineRenderer.DrawLine(start, end, color*alpha) per live tracer
    → LineSceneRenderer draws & clears the queue
```

## Config

- `TracerLifetime ≈ 0.08–0.12s` (~5–7 frames at 60fps) — field.
- `maxRange` for the raycast / miss fallback — field.

## Out of scope / deferred

- Server-authoritative hit point (no networking exists yet). Future: draw
  `barrel → serverHit` when a shot result arrives.
- Muzzle flash / impact effects (would be the particle-system path).
- Per-tracer width animation (LineRenderer width is global).

## Testing & verification

- No automated tests (per project direction for this work).
- Verify visually by running the game and firing — confirmation done by the user,
  not via self-capture.
