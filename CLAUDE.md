# DemiurgeSharp

Code-only Stride 4.3.0.2507 multiplayer game. net10.0, Linux, Vulkan backend.
There is no Game Studio project: the scene is assembled in code in `Client/Program.cs`,
which is also the composition root for all wiring.

## Build & run

```bash
dotnet build DemiurgeSharp.slnx
dotnet run                                      # client (DemiurgeSharp.csproj)
dotnet run --project Server/DemiurgeServer.csproj   # server
```

If the build goes weird after dependency changes: `dotnet clean && dotnet restore --no-cache && dotnet build --no-incremental`.

Projects: `DemiurgeSharp.csproj` (client), `Server/DemiurgeServer.csproj`,
`Common/DemiurgeCommon.csproj`, `tools/GltfAssetGenerator`.

## Architecture

**Read `RECIPES.md` before changing gameplay code.** It is the map, not a tutorial:
Common is the wire, Server is truth, Client is Netcode → Sim → View with a strict
one-way flow, and Program.cs wires it all. It also carries step-by-step recipes for
adding a replicated component, an equippable item, a weapon, or a new trait — each one
a fixed list of append-only edits. Follow the recipe instead of re-deriving it; the
steps that are easy to forget are exactly the ones that shipped bugs before.

Wire rule worth repeating here: enum values and the `ComponentBundle` if-chain order
ARE the protocol. Append, never reorder, never delete — clients desync silently.

Design specs live in `docs/superpowers/specs/`, plans in `docs/superpowers/plans/`,
loose notes in `docs/scratchpad/`.

## Stride engine reference — check `stride_docs/` first

`stride_docs/` is our own Stride reference, written from the decompiled 4.3.0.2507
assemblies and cross-checked against this repo. **Look there before decompiling the
engine or searching the web** — it exists specifically to kill that cold-start cost.

- `scripts-and-lifecycle.md` — ScriptComponent/SyncScript/AsyncScript, update order, priorities
- `input.md` — keyboard/mouse API, edge vs level triggers, `Keys`, mouse lock and delta
- `entities-transforms-cameras.md` — entities, transforms, world matrices, cameras, projection
- `rendering-and-compositor.md` — graphics compositor, custom scene renderers, materials, lights, UI
- `community-toolkit.md` — which helpers are CommunityToolkit vs core Stride, and what they do
- `physics-bepu.md` — Stride.BepuPhysics bodies, colliders, raycasts, impulses

If a fact is missing, decompile it rather than guessing, then **add it back to the
relevant doc**:

```bash
ilspycmd -t Stride.Engine.Processors.ScriptSystem \
  ~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/Stride.Engine.dll
ilspycmd -l class <dll>          # list types
# backtick generics need single quotes: -t 'Stride.Engine.EntityProcessor`2'
```

Assemblies: `~/.nuget/packages/<package>/4.3.0.2507/lib/net10.0/*.dll`, with XML doc
comments in `Stride.*.xml` beside them. Use the plain `net10.0` variants — this is Linux.

## Engine gotchas that have already cost real time

- **There is no `Enabled` switch on a script.** `ScriptComponent` derives from
  `EntityComponent`, not `ActivableEntityComponent`, so `script.Enabled = false` doesn't
  compile — and nothing would honour it anyway: `ScriptSystem.Update` schedules every
  registered sync script unconditionally, and `ScriptProcessor` only reacts to components
  being added/removed. To actually stop per-frame work, early-return on your own flag or
  remove the component.
- **Vulkan/Linux landmines** (all documented in `Client/Program.cs` comments):
  `FastTextRenderer` crashes, so `AddProfiler()` and `DebugTextSystem.Print` are
  unusable — use the UI/`HUD` path for on-screen text; particle rendering crashes
  (stride3d/stride#2496) and is disabled; SSR/`LocalReflections` needs a pixel format
  Vulkan lacks and is turned off; setting `IsFullScreen` before `Run()` throws in
  `InitDefaultRenderTarget` — use borderless windowed inside `Start()`.
- `Texture.Load` pulls in Windows-only `System.Drawing.Common`; decode with
  StbImageSharp and build textures via `Texture.New2D`.

## Working with Sebastian

- **He implements.** Default output is a plan, a diagnosis, or a review — do not edit
  project files unless he explicitly delegates the implementation.
- **Never `git commit`.** Even when implementation is delegated, leave changes staged
  for him to review and commit himself.
- **Verify visual changes by asking him to look**, not by screenshotting the game.
- Two-client local testing: an unfocused Stride window is throttled by the engine.
  Background-window stutter is not a netcode bug.
