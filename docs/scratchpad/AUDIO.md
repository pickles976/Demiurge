# Stride OpenAL: `SoundInstance.Stop()` self-deadlocks (game hard-freeze)

**Stride 4.3.0.2507** · Linux · OpenAL-Soft 1.23.1 · code-first (Stride.CommunityToolkit)

## Symptom
Playing short 2D sounds repeatedly hard-freezes the game (main thread spins forever, no
exception; one CPU core pegged at 100%). It triggers on the first `SoundInstance.Stop()` —
and `Stop()` is reached not just explicitly but via `Dispose()`, replaying/reusing an
instance, **or even reading `PlayState`** (the getter calls `Stop()` once a voice finishes).

## Root cause — recursive spinlock self-deadlock in `libstrideaudio.so`
`xnAudioSourceStop` takes the global `OpenAL::ContextState::sOpenAlLock` spinlock and, **without
releasing it**, calls `xnAudioSourceFlushBuffers`, which tries to take the **same non-recursive
lock again on the same thread** → it spins on the `cmpxchg` forever.

```asm
; xnAudioSourceStop
f4a2: lock cmpxchg %cl,(%rbx)   ; acquire sOpenAlLock
f4a6: jne f4a0                  ; spin until held
...   call alSourceStop ...     ; (no release of the lock anywhere here)
f4dc: call xnAudioSourceFlushBuffers   ; still holding the lock

; xnAudioSourceFlushBuffers  <-- hung thread is parked here
f362: lock cmpxchg %cl,(%r12)   ; acquire sOpenAlLock AGAIN (already held by this thread)
f368: jne f360                  ; spins forever
```

Hung-thread stack (`dotnet-dump`):
```
xnAudioSourceFlushBuffers        ← spinning on sOpenAlLock (never returns)
xnAudioSourceStop                ← holds sOpenAlLock
SoundInstance.Stop()             SoundInstance.cs:261
SoundInstance.get_PlayState()    SoundInstance.cs:348   ← getter calls Stop() on a finished voice
<game>.Play() → game loop
```

It is **not** backend-specific: reproduces with `ALSOFT_DRIVERS=pipewire` and `=alsa`. The
lock is in Stride's wrapper, above OpenAL's device backend.

## How to fix
The bug is that `xnAudioSourceStop` calls a function that re-acquires a lock it already holds.
Any one of:

1. **Release `sOpenAlLock` before calling `xnAudioSourceFlushBuffers`** in `xnAudioSourceStop`
   (and re-acquire after if needed) — minimal change.
2. **Give `FlushBuffers` a "lock already held" path** (an internal `FlushBuffersLocked` that
   skips the `sOpenAlLock` acquire), and call that from `xnAudioSourceStop`. Same pattern for
   any other locked caller of `FlushBuffers`.
3. **Make `sOpenAlLock` recursive** (reentrant) — simplest but coarsest.

Separately, in managed code: `SoundInstance.get_PlayState()` should not call `Stop()` as a side
effect — a property getter triggering a blocking native call is what makes the freeze happen on
an innocent state read.

## Minimal repro
```csharp
var sound = Content.Load<Sound>("Sfx");   // short, non-spatialized, StreamFromDisk:false
var inst  = sound.CreateInstance();
inst.Play();
// after it finishes, on a later frame:
_ = inst.PlayState;                        // freezes: getter -> Stop() -> xnAudioSourceStop
```

## Workaround (no engine fix)
Never `Stop()`, `Dispose()`, reuse, or read `PlayState` of a played instance. Create a fresh
`SoundInstance`, `Play()` it once, and keep a reference so it isn't finalized (a finalizer would
call `Stop()`). Costs a leaked OpenAL source per play (silent after the driver's ~256 source
limit), but never freezes.
