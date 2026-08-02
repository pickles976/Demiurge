# Stride engine reference

Notes on Stride as this project actually uses it: **4.3.0.2507**, net10.0, Linux, Vulkan,
code-only (no Game Studio project). Written from the decompiled engine assemblies and
cross-checked against this repo, because the official docs are thin on the code-only path
and silent on the Linux/Vulkan potholes.

**Read these before decompiling the engine or searching the web.** That's the entire point
of the directory — the cold-start cost of re-deriving this stuff is what it exists to kill.

| Doc | Covers |
|---|---|
| [scripts-and-lifecycle.md](scripts-and-lifecycle.md) | `ScriptComponent` / `SyncScript` / `AsyncScript`, what `ScriptSystem` does each frame, priorities, why there is no `Enabled` switch |
| [input.md](input.md) | Keyboard and mouse, edge vs level triggers, the `Keys` enum, cursor lock, mouse position and delta semantics |
| [entities-transforms-cameras.md](entities-transforms-cameras.md) | Entities and components, local vs world transforms and when they're valid, bone links, coordinate conventions, cameras and projection |
| [rendering-and-compositor.md](rendering-and-compositor.md) | Graphics compositor graph, custom scene renderers, materials and shaders, lights, UI, and the Vulkan landmines |
| [code-only-runtime-and-assets.md](code-only-runtime-and-assets.md) | Project-specific code-only composition root, runtime loop, terrain streaming bridge, generated meshes, and asset pipeline |
| [community-toolkit.md](community-toolkit.md) | Which helper is CommunityToolkit and which is core Stride, and what each one really does |
| [physics-bepu.md](physics-bepu.md) | `Stride.BepuPhysics` bodies, colliders, raycasts, impulses, contact events, collision filtering |

## Conventions used here

Claims are cited with the type and assembly that prove them, e.g.
"(`Stride.Engine.Processors.ScriptSystem`, Stride.Engine.dll)", so any line can be
re-checked in one command. Anything that couldn't be verified from IL or repo behaviour is
marked `UNVERIFIED:` — treat those as leads, not facts.

Package attribution is called out every time, because core Stride, `Stride.CommunityToolkit`
and `Stride.CommunityToolkit.Bepu` all contribute extension methods that look identical at
the call site. Note the toolkit versions independently of the engine: it's on
`1.0.0-preview.62`, not 4.3.0.2507.

## Checking something yourself

```bash
ilspycmd -t Stride.Engine.Processors.ScriptSystem \
  ~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/Stride.Engine.dll

ilspycmd -l class <dll>                        # list types in an assembly
ilspycmd -t 'Stride.Engine.EntityProcessor`2'  # backtick generics need single quotes
```

Assemblies live at `~/.nuget/packages/<package>/<version>/lib/net10.0/*.dll` with XML doc
comments beside them as `Stride.*.xml`. Use the plain `net10.0` variants — the
`net10.0-windows7.0` ones are a different build. The XML docs are occasionally wrong where
the IL disagrees; the IL wins, and those disagreements are flagged in the docs.

**When you learn something new about the engine, add it here.**
