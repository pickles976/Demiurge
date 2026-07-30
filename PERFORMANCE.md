# Performance Notes

This file records measured bottlenecks and the evidence behind them. Treat timings emitted by
individual subsystems as leads, not whole-frame profiles.

## Current baselines

- Client target: 60 FPS at 1920x1080.
- Server target: 30 TPS.
- Binding workload: embedded singleplayer, where the server tick shares the client frame.
- Scale scenario: `conquest`, player on team 1, 16 NPCs per team.
- Final verified display geometry: 1920x1080 window and 1920x1080 back buffer in borderless desktop
  fullscreen.
- Measured after the fullscreen fix: roughly 60–85 FPS during initial terrain streaming and
  105–130 FPS in later debug windows while LOD churn was still active.

## AI and navigation

AI keeps authoritative mutation on the main thread and moves only path computation to a bounded
worker pool. The pool uses half the logical processors clamped to 1–8. Per-NPC requests are
replacement-aware and prioritized; stale searches check cancellation every 64 expansions.

The navigation scale pass added:

- chunk-scoped corridor revisions, avoiding global path invalidation after unrelated edits;
- cached standability/walk/jump traversal answers per terrain generation;
- distance-based prefetch before a partial path ends;
- shared and progressively extended squad objective trunks with short per-member connectors;
- conservative collinear walk smoothing;
- queue/search p50 and p95, expansions, returned metres, cache hits, route reuse, cancellation, and
  spatial invalidation counters in `ai stats`.

On the 16-core development host, the saved 32-NPC conquest benchmark reduced initial long-route
queue p95 from 639 ms to about 232 ms. All 32 actors received useful paths in about 447 ms. This is
a development-host regression baseline, not a substitute for the Beelink SER5 acceptance run.

An edit can race a worker because terrain remains mutable. Path reconstruction now revalidates
standability and returns a stale/failed request instead of throwing; corridor revisions then decide
whether a completed result may be installed.

Remaining optimizations are measurement-gated: reverse objective fields, a coarse portal hierarchy,
pooled search storage, and capsule-checked string pulling. See
[NPC navigation](docs/NAVIGATION.md) and the performance roadmap in
[AI_IMPLEMENTATION.md](AI_IMPLEMENTATION.md).

## 1920x1080 fullscreen slowdown

Profiled on 2026-07-29 in a debug singleplayer session on the `conquest` map with 16 NPCs per team.
The machine has an Intel Raptor Lake-P integrated GPU and an NVIDIA RTX 4060 Laptop GPU.

### Symptoms

- Terrain diagnostics advanced only 4-8 frames per one-second reporting window, normally 6-8 FPS.
- The initial terrain build contained roughly 4,008 desired LOD sections.
- Terrain sometimes reached `dirty 0`, then soon queued roughly 3,000 sections again.
- Reconciliation corrections became frequent while the client was running at the low frame rate.

### GPU A/B test

The default Vulkan adapter appeared to be the Intel integrated GPU. The game was then launched with
NVIDIA PRIME render offload:

```text
__NV_PRIME_RENDER_OFFLOAD=1
__GLX_VENDOR_LIBRARY_NAME=nvidia
```

The NVIDIA driver reported the game process using about 147 MiB of VRAM, but the RTX 4060 remained
at approximately 1% utilization, P8, 210 MHz, and 3.13 W. Frame rate stayed near 6-8 FPS.

Conclusion: this was not a fill-rate problem and selecting the discrete GPU did not address it.

### Managed main-thread samples

Two full managed snapshots were captured at different times during the slowdown. Both stopped the
main thread in the same path:

```text
Stride.Games.GameWindowSDL.EndScreenDeviceChange
Stride.Games.GraphicsDeviceManager.ChangeOrCreateDevice
Stride.Games.GameFormSDL.GameForm_SizeChangedActions
Stride.Graphics.SDL.Window.ProcessEvent
Stride.Games.SDLMessageLoop.NextFrame
```

Local inspection of the Stride assemblies showed the feedback mechanism:

1. `ClientApplication.Start` called `ConfigureWindow` after `game.Run` had already created the SDL
   window and graphics device.
2. `ConfigureWindow` changed the back buffer and fullscreen state, then called `ApplyChanges`.
3. `EndScreenDeviceChange` assigned the SDL client size and fullscreen state.
4. The resulting size event called `ChangeOrCreateDevice` again after the preceding device change
   completed.

The repeated fullscreen resize/device-change handling occupied the main thread. Resolution was
correlated with the regression because fullscreen was enabled in the same change, but the measured
bottleneck was window/device lifecycle work rather than rendering 1920x1080 pixels.

### Fix

Configure the preferred back-buffer size and fullscreen state before entering `game.Run`, and create
the initial SDL `GameContext` at 1920x1080. Do not call `ApplyChanges` from the toolkit's post-device
`start` callback.

Runtime validation showed that initialization order alone did not stop the loop: a new managed
snapshot still landed in `EndScreenDeviceChange`, and frame rate remained 7-8 FPS. The SDL backend
therefore also uses borderless desktop fullscreen. This retains fullscreen presentation without the
exclusive display-mode switch that repeatedly emits the size event on this setup.

The first borderless build was then run with the same `conquest` singleplayer workload. Terrain
diagnostics reported roughly 85-120 frames per second during the initial 4,008-section build and
approximately 128-145 frames per second in later reporting windows. Subsequent inspection found that
this run still had a 1280x720 presenter inside the 1920x1080 window, so those numbers demonstrate the
device-loop fix but are not valid 1080p results.

### Borderless window/back-buffer mismatch

The first borderless implementation produced a 1920x1080 X11 window on the primary monitor, but the
rendered game did not fill it. Stride's `Game.PrepareContext` loaded the generated `GameSettings`
asset after the client configured its graphics manager and replaced the requested dimensions with
`RenderingSettings` defaults of 1280x720. The result was a 1280x720 presenter inside a 1920x1080
window.

This client owns its compositor and presentation configuration in code, so
`Game.AutoLoadDefaultSettings` is disabled before `game.Run`. The graphics manager's 1920x1080
request then remains authoritative through initial device creation.

A startup diagnostic verified `window 1920x1080, back buffer 1920x1080`. At true 1080p, terrain
diagnostics reported approximately 60-85 FPS during the initial stream and 105-130 FPS in later
windows while repeated LOD work was still active. This is the comparable final result against the
original 6-8 FPS regression.

### Secondary terrain findings

Terrain streaming remains worth profiling after the fullscreen fix:

- A player chunk-boundary change can replace much of the LOD desired set and queue thousands of
  sections.
- `ClientTerrain.CollectOneBatch` drains empty mesh results without applying the 12 ms upload budget.
  During the captured sessions it sometimes processed 500-760 empty results per reporting window.
- The terrain diagnostic's `gpu` value is CPU wall time around `Buffer.New`; it is not a hardware GPU
  duration and must not be compared directly with GPU utilization counters.

These costs were visible during the bad run, but the adapter A/B test and repeated main-thread
snapshots identified the fullscreen device-change loop as the primary cause. Re-profile terrain only
after verifying stable fullscreen frame pacing.

### Validation checklist

- Confirm fullscreen remains 1920x1080. (Automated launch used the requested back-buffer and desktop
  fullscreen settings; visually confirm monitor/window behavior during playtest.)
- Confirm repeated snapshots no longer land in `EndScreenDeviceChange`. (Frame-rate recovery confirms
  the loop is no longer consuming each frame; take another snapshot only if the regression returns.)
- Compare FPS after initial terrain streaming settles. (Measured approximately 105-130 FPS at a
  verified 1920x1080 back buffer in this debug run.)
- Check discrete and integrated GPU utilization separately before attributing a regression to
  rendering resolution.
- If terrain still churns after `dirty 0`, instrument LOD anchor transitions and time-budget empty
  result collection.
