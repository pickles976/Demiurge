# Stride Input API (4.3.0.2507, Linux/SDL/Vulkan)

Reference for `Stride.Input`. Every claim below is from decompiled source or repo code.

Re-check any line with:

```bash
ILDLL=~/.nuget/packages/stride.input/4.3.0.2507/lib/net10.0/Stride.Input.dll
ilspycmd -t Stride.Input.InputManager $ILDLL      # single-quote backtick generics: 'Foo`1'
ilspycmd -l class $ILDLL
```

XML docs: `~/.nuget/packages/stride.input/4.3.0.2507/lib/net10.0/Stride.Input.xml`.
This project targets plain `net10.0` (not `net10.0-windows7.0`), so the active backend types are
`Stride.Input.InputSourceSDL` / `MouseSDL` / `KeyboardSDL` / `PointerSDL`.

Repo usage: `Client/View/LocalPlayerController.cs`, `Client/View/PlayerCamera.cs`,
`Client/View/AimLineScript.cs`, `Client/Program.cs`, `Client/Core/MathExtensions.cs`.

---

## 1. Getting at the InputManager

| Context | Expression |
| --- | --- |
| Inside a `ScriptComponent` / `SyncScript` | `Input` |
| Outside a script (e.g. `Program.cs` top-level) | `game.Input` |
| From a service registry | `Services.GetService<InputManager>()` |

- `ScriptComponent.Input` (`Stride.Engine.ScriptComponent`, Stride.Engine.dll) lazily resolves
  `GetSafeServiceAs<InputManager>(Services)` — it is the same singleton as `game.Input`.
- `Stride.Engine.Game.Input` is created in `Game` init as `new InputSystem(Services).Manager` and
  registered via `Services.AddService<InputManager>(Input)`.
- `Stride.Engine.InputSystem` has `UpdateOrder = -50` (`DefaultUpdateOrder`), and `ScriptSystem`
  leaves `UpdateOrder` at its default 0. **Input is fully updated before any script's `Update()`
  runs**, so every script in a frame sees the same input snapshot.

Repo example (`Client/Program.cs:257`) — outside a script, guard on device presence first:

```csharp
if (camera == null || simulation == null || !game.Input.HasMouse) return;
if (game.Input.IsMouseButtonPressed(MouseButton.Left)) { /* ... */ }
```

---

## 2. Edge vs level triggers — the thing everyone gets wrong

`InputManager` (Stride.Input.dll) forwards to the device, which stores three sets rebuilt each frame:

```csharp
public bool IsKeyPressed(Keys key)  => Keyboard?.IsKeyPressed(key) ?? false;   // PressedKeys.Contains
public bool IsKeyReleased(Keys key) => Keyboard?.IsKeyReleased(key) ?? false;  // ReleasedKeys.Contains
public bool IsKeyDown(Keys key)     => Keyboard?.IsKeyDown(key) ?? false;      // DownKeys.Contains
```

| Method | Semantics | Lives in |
| --- | --- | --- |
| `IsKeyPressed(Keys)` | **edge**, true only on the frame the key went down | `pressedKeys`, cleared every `Update` |
| `IsKeyReleased(Keys)` | **edge**, true only on the frame the key went up | `releasedKeys`, cleared every `Update` |
| `IsKeyDown(Keys)` | **level**, true every frame while held | `downKeys`, persists between down/up |
| `IsMouseButtonPressed(MouseButton)` | **edge**, on press | `MouseDeviceState.pressedButtons`, cleared in `Reset()` |
| `IsMouseButtonReleased(MouseButton)` | **edge**, on release | `MouseDeviceState.releasedButtons`, cleared in `Reset()` |
| `IsMouseButtonDown(MouseButton)` | **level**, while held | `MouseDeviceState.downButtons` |

Verified in `Stride.Input.KeyboardDeviceBase.Update` (`pressedKeys.Clear(); releasedKeys.Clear();`
at the top of every frame) and `Stride.Input.MouseDeviceState.Update` → `Reset()`.

Bulk forms of the same data: `Input.PressedKeys` / `ReleasedKeys` / `DownKeys` (`IReadOnlySet<Keys>`),
`Input.PressedButtons` / `ReleasedButtons` / `DownButtons` (`IReadOnlySet<MouseButton>`), plus the
booleans `HasPressedKeys`, `HasReleasedKeys`, `HasDownKeys`, `HasPressedMouseButtons`,
`HasReleasedMouseButtons`, `HasDownMouseButtons`.

Repo pattern (`Client/View/LocalPlayerController.cs`) — level for movement/state, edge for actions:

```csharp
if (Input.IsKeyDown(Keys.W)) direction += forward;      // level: held movement
bool aiming = Input.IsMouseButtonDown(MouseButton.Right);
if (Input.IsKeyPressed(Keys.R)) local.TryReload();      // edge: one reload per keypress
```

### GOTCHA: OS key auto-repeat re-fires `IsKeyPressed`

`KeyboardDeviceBase.HandleKeyDown` emits a `KeyEvent { IsDown = true }` **every time** it is called,
even when the key is already in `KeyRepeats`/`downKeys` (it only bumps `RepeatCount`). And
`KeyboardSDL.OnKeyEvent` does **not** inspect `e.Repeat` — it maps the keysym and calls
`HandleKeyDown` unconditionally. So an auto-repeated SDL `SDL_KEYDOWN` re-adds the key to
`pressedKeys` and `IsKeyPressed` goes true again mid-hold.

UNVERIFIED (not from the decompile): whether SDL delivers repeats depends on the platform/WM
auto-repeat setting; SDL2 sends them by default. If you need a strict one-shot on a key that may be
held, gate on your own edge state or on `IsKeyDown` transitions rather than `IsKeyPressed`.

Mouse buttons do **not** have this problem: `MouseDeviceState.HandleButtonDown` early-outs when the
button is already in `downButtons`.

---

## 3. Mouse position, delta, wheel

### `Input.MousePosition` → `Vector2`, **normalized 0..1, origin top-left, +Y down**

Chain (all in Stride.Input.dll): `MouseSDL.OnMouseMoveEvent` passes SDL window-relative pixels
(`e.X`, `e.Y`, origin top-left) to `MouseDeviceState.HandleMove`, which does
`newPosition *= PointerState.InverseSurfaceSize`; `MouseSDL.OnSizeChanged` sets that surface size to
`uiControl.ClientSize` (`invSurfaceSize = 1f / SurfaceSize`); `InputManager.ProcessEvent(PointerEvent)`
then stores `mousePosition = inputEvent.Position`.

So `MousePosition == (pixelX / clientWidth, pixelY / clientHeight)`. `(0,0)` is the top-left of the
client area, `(1,1)` the bottom-right, `(0.5,0.5)` the centre.

**Not clamped.** Nothing in `MouseSDL`/`MouseDeviceState`/`InputManager` clamps, so values can leave
`[0,1]` when the cursor is dragged outside the window while a button is held.

Related:

- `Input.AbsoluteMousePosition` — the same point in **pixels** (`PointerEvent.AbsolutePosition` is
  `Position * Pointer.SurfaceSize`).
- `Input.MousePosition` has a **setter**: `InputManager.SetMousePosition` → `Mouse.SetPosition(normalized)`
  → `MouseSDL.SetPosition` warps the OS cursor (`SDL_WarpMouseInWindow`) to `normalized * SurfaceSize`.

Repo conversions (`Client/Core/MathExtensions.cs:35`) — normalized top-left/Y-down to the
LineRenderer's centre-origin/Y-up pixel space:

```csharp
public static Vector2 MousePosToScreenCoords(Vector2 v, Rectangle rect)
{
    var centered = v - new Vector2(0.5f, 0.5f);
    return new Vector2(centered.X * rect.Width, 1.0f - (centered.Y * rect.Height));
}
```

`ThirdPersonCameraScript` (`Client/View/PlayerCamera.cs:50`) does the same recentring by hand.

### `Input.MouseDelta` → `Vector2`, **normalized, per-frame, accumulated, anisotropic**

- Reset to zero at the top of every `InputManager.Update` (`ResetGlobalInputState`).
- Accumulated over all mouse pointer events of the frame: `MouseDelta += inputEvent.DeltaPosition`.
- Normalized by the **same component-wise** `InverseSurfaceSize` as the position, i.e.
  `(dxPixels / clientWidth, dyPixels / clientHeight)`. **X and Y use different divisors**, so on a
  non-square window a 45-degree physical mouse move does not produce a 45-degree delta. For free-look
  sensitivity that should be aspect-independent, multiply X back by the aspect ratio or use
  `Input.AbsoluteMouseDelta`.
- `Input.AbsoluteMouseDelta` — the same delta in pixels.

### `Input.MouseWheelDelta` → `float`

```csharp
public void ProcessEvent(MouseWheelEvent inputEvent)
{
    if (Math.Abs(inputEvent.WheelDelta) > Math.Abs(MouseWheelDelta))
        MouseWheelDelta = inputEvent.WheelDelta;   // largest-magnitude event, NOT a sum
}
```

It keeps the largest-magnitude wheel event of the frame rather than summing them, and is reset to
`0f` each `Update`. Value comes from `MouseSDL.OnMouseWheelEvent`: `sdlWheelEvent.Y` negated when
`Direction == 1` (SDL "flipped" mode) — i.e. wheel notches, positive = scroll up.

### `MouseButton` enum (Stride.Input.dll)

`Left`, `Middle`, `Right`, `Extended1`, `Extended2` (SDL X1/X2). Note the ordering: `Middle` is 1,
`Right` is 2 — never cast ints to `MouseButton`.

---

## 4. Mouse capture and visibility (free-look cameras)

```csharp
Input.LockMousePosition(forceCenter: false);  // -> Mouse.LockPosition(forceCenter)
Input.UnlockMousePosition();                  // -> Mouse.UnlockPosition()
bool locked = Input.IsMousePositionLocked;    // -> Mouse.IsPositionLocked, false if no mouse
Game.IsMouseVisible = false;                  // Stride.Games.GameBase -> GameWindow.IsMouseVisible
```

`MouseSDL.LockPosition(bool forceCenter)`:

1. No-ops if already locked.
2. Remembers `relativeCapturedPosition` = window centre when `forceCenter`, else the current
   `uiControl.RelativeCursorPosition`.
3. `uiControl.SetRelativeMouseMode(true)` → `SDL_SetRelativeMouseMode(true)`.

**What changes while locked** — `MouseSDL.OnMouseMoveEvent` branches:

```csharp
if (IsPositionLocked) MouseState.HandleMouseDelta(new Vector2(e.Xrel, e.Yrel));
else                  MouseState.HandleMove(new Vector2(e.X, e.Y));
```

- `MouseDelta` / `AbsoluteMouseDelta` keep updating, now from SDL's *relative* motion (`Xrel`/`Yrel`),
  which is unbounded by the window edge — this is the point of locking.
- `MousePosition` **freezes**. `HandleMouseDelta` emits a `PointerEvent` whose `Position` is the
  unchanged stored `Position`, so `InputManager.mousePosition` keeps re-receiving the same value it
  had when you locked. Do not read `MousePosition` for aiming while locked.

`MouseSDL.UnlockPosition()`: `SetRelativeMouseMode(false)`, then warps the cursor back to
`relativeCapturedPosition` (`SDL_WarpMouseInWindow`) and clears it to zero.

Visibility caveats:

- `Game.IsMouseVisible` and the lock state are **independent flags**. `MouseSDL` never touches
  `Cursor.Show()`/`Hide()`. The XML doc's "hides it" / "restore the mouse visibility" wording is
  describing SDL relative mode's own implicit cursor hiding, not a change to `Game.IsMouseVisible`.
  `Game.IsMouseVisible` will still read whatever you last set.
- `GameWindowSDL.IsMouseVisible` (Stride.Games.dll) is a plain setter that calls `Cursor.Show()` /
  `Cursor.Hide()` and dedupes on the current value.

This repo currently runs *unlocked* — `ThirdPersonCameraScript.Start` hides the cursor but the
`LockMousePosition` call is commented out (`Client/View/PlayerCamera.cs:33`), because the third-person
aim system needs absolute `MousePosition` for look-ahead.

### GOTCHA: focus loss does not reset input state

`InputManager.Pause()` / `Resume()` (wired to `Game.Deactivated` / `Game.Activated` in
`Stride.Engine.InputSystem`) just forward to `IInputSource.Pause()/Resume()`. `InputSourceSDL` does
**not** override them, and `InputSourceBase.Pause/Resume` are empty. So alt-tabbing away does not
clear `DownKeys`/`DownButtons` — a key held at focus loss can stay "down" until a real key-up arrives.

---

## 5. Device presence

All on `InputManager`, all `bool` (or `int`), all recomputed as devices are added/removed:

`HasPointer`, `HasMouse`, `HasKeyboard`, `HasGameController`, `HasGamePad`,
`GameControllerCount`, `GamePadCount`.

Direct device handles (null when absent): `Pointer`, `Mouse`, `Keyboard`, `TextInput`,
`DefaultGamePad`. Collections: `Pointers`, `Keyboards`, `GamePads`, `GameControllers`, `Sensors`,
`Sources`.

The `Is*` helpers already null-check (`Keyboard?.IsKeyPressed(key) ?? false`), so `HasMouse` guards
are only needed when you touch `Input.Mouse` / `MousePosition` / `LockMousePosition` directly —
`LockMousePosition`/`UnlockMousePosition` are themselves `if (HasMouse)`-guarded no-ops.

`Input.Scan()` re-enumerates devices; the XML doc warns it takes several milliseconds and should be
called only at non-critical moments (pause menu, config screen).

---

## 6. `Keys` enum — full member list

`Stride.Input.Keys`, backing values are Stride-specific (**not** Win32 VK codes). Several names are
aliases sharing one value.

```
None=0 Cancel=1 Back=BackSpace=2 Tab=3 LineFeed=4 Clear=5 Enter=Return=6 Pause=7
Capital=CapsLock=8 HangulMode=KanaMode=9 JunjaMode=10 FinalMode=11 HanjaMode=KanjiMode=12
Escape=13 ImeConvert=14 ImeNonConvert=15 ImeAccept=16 ImeModeChange=17 Space=18
PageUp=Prior=19 Next=PageDown=20 End=21 Home=22 Left=23 Up=24 Right=25 Down=26
Select=27 Print=28 Execute=29 PrintScreen=Snapshot=30 Insert=31 Delete=32 Help=33
D0=34 D1=35 D2=36 D3=37 D4=38 D5=39 D6=40 D7=41 D8=42 D9=43
A=44 B=45 C=46 D=47 E=48 F=49 G=50 H=51 I=52 J=53 K=54 L=55 M=56
N=57 O=58 P=59 Q=60 R=61 S=62 T=63 U=64 V=65 W=66 X=67 Y=68 Z=69
LeftWin=70 RightWin=71 Apps=72 Sleep=73
NumPad0=74 NumPad1=75 NumPad2=76 NumPad3=77 NumPad4=78 NumPad5=79 NumPad6=80
NumPad7=81 NumPad8=82 NumPad9=83
Multiply=84 Add=85 Separator=86 Subtract=87 Decimal=88 Divide=89
F1=90 F2=91 F3=92 F4=93 F5=94 F6=95 F7=96 F8=97 F9=98 F10=99 F11=100 F12=101
F13=102 F14=103 F15=104 F16=105 F17=106 F18=107 F19=108 F20=109 F21=110 F22=111 F23=112 F24=113
NumLock=114 Scroll=115
LeftShift=116 RightShift=117 LeftCtrl=118 RightCtrl=119 LeftAlt=120 RightAlt=121
BrowserBack=122 BrowserForward=123 BrowserRefresh=124 BrowserStop=125 BrowserSearch=126
BrowserFavorites=127 BrowserHome=128
VolumeMute=129 VolumeDown=130 VolumeUp=131
MediaNextTrack=132 MediaPreviousTrack=133 MediaStop=134 MediaPlayPause=135
LaunchMail=136 SelectMedia=137 LaunchApplication1=138 LaunchApplication2=139
Oem1=OemSemicolon=140  OemPlus=141  OemComma=142  OemMinus=143  OemPeriod=144
Oem2=OemQuestion=145   Oem3=OemTilde=146
Oem4=OemOpenBrackets=149  Oem5=OemPipe=150  Oem6=OemCloseBrackets=151
Oem7=OemQuotes=152     Oem8=153            Oem102=OemBackslash=154
Attn=163 CrSel=164 ExSel=165 EraseEof=166 Play=167 Zoom=168 NoName=169 Pa1=170 OemClear=171
NumPadEnter=180 NumPadDecimal=181
```

Notes:

- **Tilde / backtick is `Keys.OemTilde`** (`== Keys.Oem3 == 146`), verified present in
  Stride.Input.dll. `KeyboardSDL.SDLKeys.MapKey` maps SDL keycode 96 (`` ` ``, `SDLK_BACKQUOTE`) to
  `Keys.Oem3`. This is the usual console-toggle key.
- Modifiers are **side-specific only**: there is no `Keys.Shift`/`Ctrl`/`Alt` — use `LeftShift`,
  `RightShift`, `LeftCtrl`, `RightCtrl`, `LeftAlt`, `RightAlt`.
- Number-row digits are `D0`..`D9`; numpad digits are `NumPad0`..`NumPad9`.
- Unused numeric gaps: 147-148, 155-162, 172-179. The list above is exhaustive.
- `Keys.None` is returned by `MapKey` for unmapped keysyms, and `KeyboardSDL.OnKeyEvent` drops those
  events entirely — unmapped keys are invisible to the input system.

### GOTCHA: the engine steals Ctrl + C + F12

`Stride.Engine.Game.EndDraw` (Stride.Engine.dll) contains a hard-coded screenshot hotkey:

```csharp
if (Input.HasKeyboard && Input.IsKeyDown(Keys.LeftCtrl) && Input.IsKeyDown(Keys.C)
    && Input.IsKeyReleased(Keys.F12)) { /* saves <exe>_<timestamp>.png next to the executable */ }
```

Do not bind that combination.

---

## 7. Event streams (alternative to polling)

Per-frame lists on `InputManager`, all cleared/recycled at the start of each `Update`:
`Events` (`IReadOnlyList<InputEvent>`), `KeyEvents` (`KeyEvent`: `Key`, `IsDown`, `RepeatCount`),
`PointerEvents` (`PointerEvent`), `GestureEvents`.

Push-style: implement `IInputEventListener<TEvent>` and register with
`Input.AddListener(this)` / `Input.RemoveListener(this)`.

Events are pooled — `PointerEvent.Clone()` exists precisely because holding a reference past the
frame gets you a recycled object.

---

## 8. Text input (brief)

- `Input.TextInput` → `ITextInputDevice` (on SDL this is the same object as `Input.Keyboard`:
  `KeyboardSDL : KeyboardDeviceBase, ITextInputDevice`).
- `EnabledTextInput()` / `DisableTextInput()` wrap `SDL_StartTextInput` / `SDL_StopTextInput`.
- Consume via `IInputEventListener<TextInputEvent>`. `TextInputEvent` has `Text`, `Type`
  (`TextInputEventType.Input` for committed text, `.Composition` for IME preedit),
  `CompositionStart`, `CompositionLength`.

## 9. Gamepads (brief)

- `Input.DefaultGamePad`, `Input.GamePads`, `Input.GetGamePadByIndex(int)`,
  `Input.GetGamePadsByIndex(int)`, `Input.GamePadCount`, `Input.HasGamePad`.
- `IGamePadDevice`: `State` (`GamePadState`), `Index`, `PressedButtons`/`ReleasedButtons`/`DownButtons`
  (`IReadOnlySet<GamePadButton>`, same edge/level split as keys), `IndexChanged`, `SetVibration(...)`.
- `GamePadButton` is a `[Flags] ushort`: `PadUp/Down/Left/Right` (`Pad` = all four), `Start`, `Back`,
  `LeftThumb`, `RightThumb`, `LeftShoulder`, `RightShoulder`, `A`, `B`, `X`, `Y`.
- `InputManager.GameControllerAxisDeadZone` (static, default `0.05f`) applies to all controller axes.
- SDL backend: `GamePadSDL` / `GameControllerSDL`, hot-plug handled by `InputSourceSDL` via
  `JoystickDeviceAdded/Removed`. Vibration XML doc says it is only supported on Windows XInput/UWP.

## 10. Touch / pointers / gestures (brief)

- `Input.Pointer` / `Input.Pointers` / `Input.HasPointer`; `Input.PointerEvents` carries
  `PointerId` (mouse is always 0), `Position` (normalized, same space as `MousePosition`),
  `DeltaPosition`, `AbsolutePosition`, `DeltaTime`, `EventType`
  (`Pressed`/`Moved`/`Released`/`Canceled`), `IsDown`.
- The mouse's **left** button also synthesizes pointer press/release (`MouseDeviceState.HandleButtonDown`
  calls `HandlePointerDown()` only for `MouseButton.Left`), so left-click shows up in `PointerEvents`.
- Gestures: add `GestureConfig` subclasses (`GestureConfigTap`, `Drag`, `Flick`, `LongPress`,
  `Composite`) to `Input.Gestures`, then read `Input.GestureEvents` each frame.

## 11. Virtual buttons (brief)

`Input.VirtualButtonConfigSet` + `GetVirtualButton(configIndex, name)`,
`IsVirtualButtonPressed/Down/Released(configIndex, name)`. Bindings are built from
`VirtualButton.Keyboard.*` / `.Mouse.*` / `.GamePad.*` / `.Pointer.*` via `VirtualButtonBinding`,
`VirtualButtonGroup`, `VirtualButtonTwoWay`. Not used in this repo.

---

## 12. Misc gotchas

- `InputManager.TransformPosition(Size2F fromSize, RectangleF destRect, Vector2 screenCoords)` is a
  static helper for remapping normalized pointer coords into a sub-viewport.
- `Input.UseRawInput` is a **no-op stub** on this build: the getter always returns `false` and the
  setter does nothing.
- `Input.LastPointerDevice` tells you which device last drove `MousePosition`/`MouseDelta`.
- `InputManager.PreUpdateInput` fires after devices produce events but before they are routed to
  listeners — the hook point for injecting or filtering input.
