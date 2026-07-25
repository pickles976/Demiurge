# Rendering, the graphics compositor, materials & custom renderers

Reference for DemiurgeSharp: **Stride 4.3.0.2507, net10.0, Linux, Vulkan backend**
(`<StrideGraphicsApi>Vulkan</StrideGraphicsApi>`, `DemiurgeSharp.csproj`).
This project builds its compositor **in code** — there is no Game Studio scene asset for it.
Everything below is verified against decompiled Stride assemblies or repo source; the two
exceptions are tagged `UNVERIFIED:`.

`[core]` = core Stride. `[toolkit]` = Stride.CommunityToolkit (a *community* package, not
engine code — its behaviour is not documented in Stride's own docs).

| I want to… | Section |
|---|---|
| Understand the compositor graph this repo builds | [§1](#1-the-compositor-this-project-builds) |
| Add debug drawing / a custom renderer | [§2](#2-writing-a-custom-scene-renderer) |
| Turn a post-processing effect on/off | [§3](#3-forwardrenderer--postprocessingeffects) |
| Build a material in code | [§4](#4-materials-in-code) |
| Write a custom SDSL shader | [§5](#5-custom-sdsl-shader-mixins) |
| Load / create a texture | [§6](#6-textures-in-code) |
| Add lights & shadows | [§7](#7-lights) |
| Add an on-screen debug readout | [§8](#8-ui-rendering--on-screen-debug-text) |
| **Why does X crash on Vulkan?** | [§9](#9-linuxvulkan-landmines) |
| Where do I `ilspycmd` type Y? | [§10](#10-appendix-which-assembly-holds-what) |

---

## 1. The compositor this project builds

A `GraphicsCompositor` `[core]` (`Stride.Rendering.Compositing.GraphicsCompositor`) is the
whole description of "how a frame is drawn": a list of `RenderStage`s, a list of
`RootRenderFeature`s that decide which render objects go into which stage, a list of camera
slots, and an `ISceneRenderer` tree rooted at `.Game` that actually executes.

All setup lives in `Start()`, `Client/Program.cs:136-138`:

```csharp
var compositor = game.AddGraphicsCompositor();
compositor.AddCleanUIStage();
compositor.AddSceneRenderer(new LineSceneRenderer());
```

**Order matters.** Each call mutates what the previous one produced.

### 1.1 `game.AddGraphicsCompositor()` `[toolkit]`

`Stride.CommunityToolkit.Engine.GameExtensions` (Stride.CommunityToolkit.dll). It is a
one-liner over core Stride:

```csharp
GraphicsCompositorHelper.CreateDefault(true, "StrideForwardShadingEffect",
    null, null, GraphicsProfile.Level_10_0, RenderGroupMask.All);
```

`GraphicsCompositorHelper.CreateDefault` `[core]`
(`Stride.Rendering.Compositing.GraphicsCompositorHelper`, Stride.Engine.dll) produces:

- **RenderStages:** `Opaque` (`StateChangeSortMode`), `Transparent` (`BackToFrontSortMode`),
  `ShadowMapCaster`, `ShadowMapCasterParaboloid`, `ShadowMapCasterCubeMap`
  (all `FrontToBackSortMode`).
- **RootRenderFeatures:** `MeshRenderFeature` (sub-features `TransformRenderFeature`,
  `SkinningRenderFeature`, `MaterialRenderFeature`, `ShadowCasterRenderFeature`,
  `ForwardLightingRenderFeature`), `SpriteRenderFeature`, `BackgroundRenderFeature`.
- A `ForwardRenderer` assigned to `.SingleView`, `.Editor`, and wrapped in a
  `SceneCameraRenderer` for `.Game`. One `SceneCameraSlot`.

**What is *not* there** — the source of several silent-nothing-renders bugs:
no `UIRenderFeature`, no `ParticleEmitterRenderFeature`, no `InstancingRenderFeature`.

Note also: `CreateDefault` builds a `PostProcessingEffects`, then immediately calls
`DisableAll()` and re-enables only `ColorTransforms`. Remember this — the next call undoes it.

### 1.2 `compositor.AddCleanUIStage()` `[toolkit]`

`Stride.CommunityToolkit.Rendering.Compositing.GraphicsCompositorExtensions`. Two halves.

**(a) `AddPostEffects` — replaces `PostEffects` wholesale:**

```csharp
var fx = new PostProcessingEffects();          // fresh instance, ctor defaults
fx.DepthOfField.Enabled = false;
fx.ColorTransforms.Transforms.Add(new ToneMap());
((ForwardRenderer)compositor.SingleView).PostEffects = fx;
```

> **Causal chain worth internalising.** `CreateDefault` disabled every effect; `AddCleanUIStage`
> throws that instance away and installs a **fresh** `PostProcessingEffects` sitting at
> constructor defaults, which turn most effects *back on* (see §3). **That — and only that — is
> why `Program.cs:139-143` has to explicitly disable `LocalReflections`.** No comment in the
> repo currently states this; without it the disable line looks arbitrary. If you ever remove
> `AddCleanUIStage()`, the SSR disable becomes unnecessary; if you add another toolkit helper
> that touches `PostEffects`, re-check the whole set.

**(b) `AddRenderStagesAndFeatures` — adds the UI stage and rebuilds `.Game`:**

- Adds a `RenderStage("UiStage", "Main")`.
- Adds a `UIRenderFeature` with two `SimpleGroupToRenderStageSelector`s: render groups **0-30**
  go to the ForwardRenderer's `TransparentRenderStage`; render group **31** goes to `UiStage`.
- Replaces `compositor.Game` with a `SceneRendererCollection`:

```
SceneRendererCollection                      <- compositor.Game
├── SceneCameraRenderer  mask = !Group31     -> ForwardRenderer (compositor.SingleView)
│                                               Opaque + Transparent + shadows + PostEffects
└── SceneCameraRenderer  mask = Group31      -> SingleStageRenderer { UiStage }
```

Group31 UI therefore renders **outside** post-processing — no tonemap, no bloom, no FXAA on
your HUD text. That is what "clean" means.

### 1.3 `compositor.AddSceneRenderer(...)` `[toolkit]`

If `compositor.Game` is already a `SceneRendererCollection`, the renderer is **appended** to
its children; otherwise the extension wraps the existing `.Game` and the new renderer in a new
collection. Since `AddCleanUIStage()` already made it a collection, `LineSceneRenderer` becomes
child **[2]** and draws after the scene *and* after the UI:

```
SceneRendererCollection
├── SceneCameraRenderer  -> ForwardRenderer      (scene)
├── SceneCameraRenderer  -> SingleStageRenderer  (Group31 UI)
└── LineSceneRenderer                            (debug lines, on top)
```

> **Trap: the line renderer has no camera.** It is a bare child of the collection, *not* wrapped
> in a `SceneCameraRenderer`, so nothing binds a camera or a view/projection for it. That is
> exactly why `LineRenderer.Camera` is a manually-assigned `public static CameraComponent?`
> (`Client/Rendering/LineRenderer.cs:28`), set once at `Client/Program.cs:237`, and why
> `DrawCore` projects 3D segments by hand through `Camera.ViewProjectionMatrix`. If you add
> another bare scene renderer that needs a camera, you must do the same — or wrap it in a
> `SceneCameraRenderer` with a camera slot yourself.

### 1.4 Adding a render feature after the fact

> **Trap: the default compositor has no `InstancingRenderFeature`.** An entity with an
> `InstancingComponent` silently renders exactly **one** copy instead of failing. `GrassField`
> patches the compositor at runtime (`Client/Rendering/GrassField.cs:119-127`):

```csharp
var meshRenderFeature = game.SceneSystem.GraphicsCompositor.RenderFeatures
    .OfType<MeshRenderFeature>().First();
if (!meshRenderFeature.RenderFeatures.Any(f => f is InstancingRenderFeature))
    meshRenderFeature.RenderFeatures.Add(new InstancingRenderFeature());
```

Idempotent, and must run **after** `AddGraphicsCompositor()`. The same reach-in pattern works
for any other missing `SubRenderFeature`.

---

## 2. Writing a custom scene renderer

This is the pattern to copy for any debug drawing. Reference implementation:
`Client/Rendering/LineRenderer.cs:134-260` (`LineSceneRenderer`).

### 2.1 The base class

Derive from `SceneRendererBase` `[core]` (`Stride.Rendering.Compositing.SceneRendererBase`,
Stride.Rendering.dll):

| Member | Kind | Notes |
|---|---|---|
| `DrawCore(RenderContext, RenderDrawContext)` | **abstract** | the only required override |
| `CollectCore(RenderContext)` | virtual | optional collect phase |
| `InitializeCore()` | virtual (from `RendererCoreBase`) | one-time GPU setup |
| `Unload()` | virtual | teardown |
| `Enabled` | property | `Draw()` is skipped when false |

`SceneRendererBase.Draw()` runs `PreDrawCoreInternal` → `DrawCore` → `PostDrawCoreInternal`,
gated on `Enabled`.

`RendererCoreBase` (Stride.Rendering.dll) already hands you protected
`Services`, `Content`, `GraphicsDevice`, `EffectSystem`, `Context`,
`NewScopedRenderTarget2D(...)`, `PushScopedResource(...)`.

> Minor: `LineSceneRenderer.InitializeCore` uses `Services.GetSafeServiceAs<EffectSystem>()`
> even though `EffectSystem` is already an inherited protected property. Both work.

### 2.2 Getting a `CommandList` / `GraphicsContext`

They come from the `RenderDrawContext` argument of `DrawCore`
(`Stride.Rendering.RenderDrawContext`, Stride.Rendering.dll):

```csharp
protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
{
    var commandList = drawContext.CommandList;   // also: .GraphicsContext, .GraphicsDevice,
    var viewport = commandList.Viewport;         //       .RenderContext, .ResourceGroupAllocator
}
```

`drawContext.PushRenderTargetsAndRestore()` returns an `IDisposable` if you need to retarget.

### 2.3 Loading an effect

```csharp
var effectSystem = Services.GetSafeServiceAs<EffectSystem>();
_effect = new EffectInstance(effectSystem.LoadEffect("LineColorShader").WaitForResult());
_effect.Parameters.Set(AlphaScaleKey, 1f);
_pipelineState = new MutablePipelineState(GraphicsDevice);
```

`LoadEffect(this EffectSystem, string)` is `Stride.Rendering.EffectSystemExtensions` and returns
`TaskOrResult<Effect>` — hence `.WaitForResult()`. The string is a shader class name; see §5.

Bind parameters **by name** to stay independent of generated shader-key classes
(`LineRenderer.cs:138-139`):

```csharp
private static readonly ValueParameterKey<float> AlphaScaleKey =
    ParameterKeys.NewValue(1f, "LineColorShader.AlphaScale");
```

### 2.4 The draw recipe

Condensed from `LineSceneRenderer.DrawCore`. Every step is required:

```csharp
// 1. upload vertices (grow the GPU buffer only when the CPU array outgrows it)
if (_vertexBuffer == null || _vertexBuffer.ElementCount < _vertices.Length)
{
    _vertexBuffer?.Dispose();
    _vertexBuffer = Buffer.Vertex.New(GraphicsDevice, _vertices, GraphicsResourceUsage.Dynamic);
}
else _vertexBuffer.SetData(commandList, _vertices);

// 2. make sure the effect is current
_effect.UpdateEffect(GraphicsDevice);

// 3. describe the pipeline
_pipelineState.State.SetDefaults();
_pipelineState.State.RootSignature    = _effect.RootSignature;
_pipelineState.State.EffectBytecode   = _effect.Effect.Bytecode;
_pipelineState.State.PrimitiveType    = PrimitiveType.TriangleList;
_pipelineState.State.InputElements    = LineVertex.Layout.CreateInputElements();
_pipelineState.State.RasterizerState  = RasterizerStates.CullNone;
_pipelineState.State.BlendState       = BlendStates.NonPremultiplied;
_pipelineState.State.DepthStencilState = DepthStencilStates.None;
_pipelineState.State.Output.CaptureState(commandList);   // adopt current RT formats
_pipelineState.Update();

// 4. bind and draw
commandList.SetPipelineState(_pipelineState.CurrentState);
_effect.Apply(drawContext.GraphicsContext);
commandList.SetVertexBuffer(0, _vertexBuffer, 0, LineVertex.Layout.VertexStride);
commandList.Draw(drawnVertices);
```

Vertex layout is a `VertexDeclaration` over a `[StructLayout(LayoutKind.Sequential)]` struct
(`LineRenderer.cs:111-131`); semantic names in `VertexElement` must match the `stage stream`
semantics in the SDSL.

`Output.CaptureState(commandList)` is what makes the pipeline agree with whatever render target
the compositor left bound — skipping it is a common cause of "works nowhere" pipelines.

### 2.5 Registering it

```csharp
compositor.AddSceneRenderer(new MyDebugRenderer());   // after AddCleanUIStage()
```

See the camera caveat in §1.3.

---

## 3. `ForwardRenderer` & `PostProcessingEffects`

`ForwardRenderer` `[core]` (`Stride.Rendering.Compositing.ForwardRenderer`, Stride.Engine.dll)
is `compositor.SingleView` in this project. Useful properties:

`Clear` · `OpaqueRenderStage` · `TransparentRenderStage` · `ShadowMapRenderStages` ·
`GBufferRenderStage` · `PostEffects` · `LightShafts` · `MSAALevel` · `MSAAResolver` ·
`LightProbes` (default `true`) · `SubsurfaceScatteringBlurEffect` · `VRSettings` ·
`BindDepthAsResourceDuringTransparentRendering` (default `true`).

### Defaults of a fresh `PostProcessingEffects`

`Stride.Rendering.Images.PostProcessingEffects` (Stride.Rendering.dll), constructor:

| Effect | Default |
|---|---|
| `Outline` | **off** (explicitly) |
| `Fog` | **off** (explicitly) |
| `AmbientOcclusion` | on |
| `LocalReflections` (SSR) | on |
| `DepthOfField` | on (but `AddCleanUIStage` turns it off) |
| `BrightFilter`, `Bloom`, `LightStreak`, `LensFlare` | on |
| `Antialiasing` | on — `FXAAEffect` |
| `ColorTransforms` | on (`AddCleanUIStage` adds a `ToneMap`) |

"On" here is `RendererCoreBase.Enabled`, which defaults to `true`.
`DisableAll()` turns off everything including antialiasing, colour transforms and the internal
range compress/decompress shaders.

### Toggling one effect

`Client/Program.cs:139-143`:

```csharp
if (((ForwardRenderer)compositor.SingleView).PostEffects is PostProcessingEffects postFx)
    postFx.LocalReflections.Enabled = false;
```

Do this **after** `AddCleanUIStage()`, or it will be discarded (§1.2a).

Side effect worth knowing: `RequiresNormalBuffer` and `RequiresSpecularRoughnessBuffer` are both
`=> LocalReflections.Enabled`, so disabling SSR also drops those G-buffer passes.
`RequiresVelocityBuffer` comes from the antialiasing effect.

---

## 4. Materials in code

```csharp
var material = Material.New(game.GraphicsDevice, new MaterialDescriptor
{
    Attributes = new MaterialAttributes
    {
        Diffuse      = new MaterialDiffuseMapFeature(new ComputeTextureColor(texture)),
        DiffuseModel = new MaterialDiffuseLambertModelFeature(),
    }
});
```

(`Client/Program.cs:116-124`.) `MaterialDescriptor.Attributes` is pre-constructed, so
`Attributes = { ... }` (collection/member initialiser on the existing instance) also works and
is used at `Program.cs:169-178` and `GrassField.cs:62-73`. Both spellings are equivalent.

- **Without a `DiffuseModel` the surface is unlit.** `MaterialDiffuseLambertModelFeature` is the
  cheap default.
- `IComputeColor` implementations used in this repo
  (`Stride.Rendering.Materials.ComputeColors`):
  - `ComputeColor(color)` — flat value.
  - `ComputeTextureColor(texture)` — sampled texture; `.Filtering = TextureFilter.Point`
    for pixel-art (`GrassField.cs:67`).
  - `ComputeShaderClassColor { MixinReference = "TestShader" }` — a custom SDSL mixin, §5.
- Other attributes demonstrated (`GrassField.cs:62-73`) — a double-sided cutout material:
  ```csharp
  CullMode = CullMode.None,
  Transparency = new MaterialTransparencyCutoffFeature { Alpha = new ComputeFloat(0.05f) },
  ```
  The cutoff feature enables the pixel shader during depth-only rendering, so alpha-clipping
  applies to the **shadow pass** too.

### Assigning to a model

```csharp
ground.Get<ModelComponent>().Materials[0] = groundMaterial;
```

`ModelComponent.Materials` (Stride.Engine.dll) is an `IndexingDictionary<Material>`. Entries
**override** `Model.Materials` per slot index; a missing/null entry falls through to the model's
own material. Index = material slot on the `Model`, not the mesh index.

---

## 5. Custom SDSL shader mixins

`MixinReference` names a **shader class**, not a file path.

Resolution (`Stride.Shaders.Compiler.EffectCompilerBase`, Stride.Shaders.dll):

```csharp
public static readonly string DefaultSourceShaderFolder = "shaders";
public static string GetStoragePathFromShaderType(string type)
    => DefaultSourceShaderFolder + "/" + type + ".sdsl";
```

So `"TestShader"` resolves to the content URL `shaders/TestShader.sdsl`.

**In this repo that means source must live at `assets/shaders/<Name>.sdsl`.**
`DemiurgeSharp.sdpkg` declares `AssetFolders: - Path: !dir assets`, so the asset compiler strips
the `assets/` prefix when building content URLs. Verified against the compiled content database:
`bin/Debug/net10.0/data/db/` contains the URLs `shaders/TestShader.sdsl` and
`shaders/LineColorShader.sdsl`. **The shader class name must equal the file name.**

Two live examples:

```hlsl
// assets/shaders/TestShader.sdsl — a ComputeColor node usable as a material feature
namespace Demiurge {
    shader TestShader : ComputeColor {
        override float4 Compute() { return float4(0, 1, 0, 1); }
    };
}
```

`assets/shaders/LineColorShader.sdsl` is the other shape: `shader LineColorShader : ShaderBase`
with `stage stream` inputs and `stage override void VSMain()/PSMain()`, loaded directly by
`EffectSystem.LoadEffect` rather than through a material.

---

## 6. Textures in code

### From raw pixels

```csharp
var texture = Texture.New2D(game.GraphicsDevice, img.Width, img.Height,
    PixelFormat.R8G8B8A8_UNorm_SRgb, img.Data);
```

`Texture.New2D<T>(GraphicsDevice, int, int, PixelFormat, T[], …) where T : unmanaged`
(`Stride.Graphics.Texture`, Stride.Graphics.dll). Defaults: 1 mip,
`TextureFlags.ShaderResource`, `GraphicsResourceUsage.Immutable`. Non-data overloads exist for
render targets.

### Decoding PNGs — do **not** use `Texture.Load`

Repo pattern (`Client/Program.cs:110-114`, `Client/View/HUD.cs:142-149`):

```csharp
ImageResult img;
using (var stream = File.OpenRead("assets/images/bullet.png"))
    img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
return Texture.New2D(game.GraphicsDevice, img.Width, img.Height,
    PixelFormat.R8G8B8A8_UNorm_SRgb, img.Data);
```

**Why:** `Texture.Load` → `Image.Load`, and the static constructor of `Stride.Graphics.Image`
(Stride.dll) registers `StandardImageHelper.LoadFromMemory` for **Png, Jpg, Bmp, Gif, Tiff,
Wmp**. `StandardImageHelper` is implemented with `System.Drawing.Bitmap` /
`System.Drawing.Imaging` (Stride.dll references `System.Drawing.Common`), which is
**Windows-only** on modern .NET. The class even carries the engine's own TODO:
*"Replace using System.Drawing, as it is not available on all platforms."*

Only `ImageFileType.Dds` (`DDSHelper`) and `ImageFileType.Stride` (`ImageHelper`) avoid it.
That is why loading pipeline-compiled textures still works fine:

```csharp
var texture = game.Content.Load<Texture>("models/grass_tex0");   // GrassField.cs:59
```

Prefer the content pipeline (`.sdtex`, auto-generated by `tools/GltfAssetGenerator`) for
anything shipped as an asset; use StbImageSharp only for loose files read at runtime.

---

## 7. Lights

`LightComponent` `[core]` (`Stride.Engine.LightComponent`) has exactly two interesting members:
`ILight Type` and `float Intensity`. **Direction comes from the entity transform**, not from a
property on the light.

Ambient (`Client/Program.cs:295-298`):

```csharp
new Entity("Ambient Light") { new LightComponent { Intensity = 1.0f, Type = new LightAmbient() } };
```

Directional with shadows (`Client/Program.cs:300-330`):

```csharp
new LightComponent
{
    Intensity = 20.0f,
    Type = new LightDirectional
    {
        Color = new ColorRgbProvider(Color.White),
        Shadow =
        {
            Enabled = true,
            Size = LightShadowMapSize.Large,
            Filter = new LightShadowMapFilterTypePcf { FilterSize = LightShadowMapFilterTypePcfSize.Filter5x5 },
            PartitionMode = new LightDirectionalShadowMap.PartitionLogarithmic(),
            ComputeTransmittance = false
        }
    }
};
// direction:
entity.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-30.0f))
                          * Quaternion.RotationY(MathUtil.DegreesToRadians(-180.0f));
```

**Intensity conventions in this scene:** ambient `1.0`, directional `20.0`. Directional
intensities are much larger than ambient; treat these two as the calibrated baseline rather than
re-deriving.

**Why shadows work at all:** `GraphicsCompositorHelper.CreateDefault` only attaches a
`ShadowMapRenderer` to the `ForwardLightingRenderFeature` on the `graphicsProfile >= Level_10_0`
branch. The toolkit passes `Level_10_0`, so we get it — plus `LightClusteredPointSpotGroupRenderer`.
The lower branch has **no shadow renderer at all**. Don't lower the profile.

`game.AddDirectionalLight()` `[toolkit]` is a one-call shortcut; it is present but commented out
at `Client/Program.cs:161` in favour of the explicit light above.

---

## 8. UI rendering & on-screen debug text

Reference: `Client/View/HUD.cs`.

```csharp
new Entity
{
    new UIComponent
    {
        Page = new UIPage { RootElement = canvas },
        RenderGroup = RenderGroup.Group31      // rendered by AddCleanUIStage()
    },
    new HudScript { ... },
};
```

`RenderGroup.Group31` is the mask `AddCleanUIStage()` routes to the post-FX-free `UiStage`
(§1.2b). **Without a `UIRenderFeature` in the compositor, `UIComponent`s draw nothing at all** —
`CreateDefault` does not add one, `AddCleanUIStage()` does.

Other `UIComponent` defaults (`Stride.Engine.UIComponent`, Stride.UI.dll):
`IsFullScreen = true`, `Resolution = (1280, 720, 1000)`, `ResolutionStretch`,
`IsBillboard = true`, `SnapText = true`, `Sampler = LinearClamp`.

Element vocabulary used here (`Stride.UI`, `Stride.UI.Controls`, `Stride.UI.Panels`):
`Canvas`, `StackPanel { Orientation }`, `TextBlock { Text, Font, TextSize, TextColor, Margin }`,
`ImageElement { Source = new SpriteFromTexture { Texture = … } }`, `Thickness`,
`Visibility.Collapsed`. Font: `game.Content.Load<SpriteFont>("StrideDefaultFont")`.

### Recipe: add a new on-screen readout

1. Build a `TextBlock`, put it in a `StackPanel`, put that in a `Canvas` with alignment.
2. Wrap in an `Entity` with `UIComponent { Page = new UIPage { RootElement = canvas },
   RenderGroup = RenderGroup.Group31 }`.
3. Add a `SyncScript` on the *same* entity holding a reference to the `TextBlock`.
4. `entity.Scene = rootScene;`

Copy `HUD.DebugStatsScript` (`HUD.cs:155-200`): it caches the last-rendered values and only
assigns `TextBlock.Text` when something changed, so steady-state frames allocate nothing.
`HUD.CreateDebugStats` (`HUD.cs:96-140`) is the entity/FPS readout wired up in
`Client/Program.cs:194`-ish.

**This is the *only* on-screen text path that works on Vulkan** — see §9. UI draws through
`UIRenderFeature` → `UIBatch : BatchBase<UIImageDrawInfo>` (Stride.UI.dll), the same batching
machinery as `SpriteBatch`, and a completely different code path from `FastTextRenderer`.

---

## 9. Linux/Vulkan landmines

The `[IL]` items were confirmed by decompiling the shipped assemblies. The `[comment]` items
rest solely on `Client/Program.cs` comments and are marked `UNVERIFIED:` — treat them as true
(they were paid for in debugging time) but know they have not been re-derived from source.

### 9.1 `[IL]` `AddProfiler()` and `DebugTextSystem.Print` crash — use the UI instead

`Stride.Graphics.FastTextRenderer.Initialize` (Stride.Graphics.dll) does:

```csharp
MappedResource unmapped = graphicsContext.CommandList.MapSubresource(indexBuffer, ...);
nint dataPointer = unmapped.DataBox.DataPointer;
/* ... fill indices ... */
graphicsContext.CommandList.UnmapSubresource(unmapped);
indexBufferBinding = new IndexBufferBinding(
    Buffer.Index.New(..., new ReadOnlySpan<byte>((void*)dataPointer, num)), ...);  // <-- after unmap
```

The pointer is read **after** `UnmapSubresource`. Both `Stride.Profiling.DebugTextSystem` and
`Stride.Profiling.GameProfilingSystem` (Stride.Engine.dll) hold a `FastTextRenderer`, and
`game.AddProfiler()` `[toolkit]` just adds a `GameProfiler` script driving the latter.

Repo: `game.AddProfiler()` disabled at `Client/Program.cs:181-187`;
`game.DebugTextSystem.Print(...)` disabled at `Client/Program.cs:253-255`.
Replacement: `HUD.CreateDebugStats` (§8).

### 9.2 `[IL]` SSR / `LocalReflections` allocates a format Vulkan doesn't map

Both ends verified:

- `Stride.Rendering.Images.LocalReflections` (Stride.Rendering.dll) hardcodes
  `RayTraceTargetFormat` and `ReflectionsFormat` to `PixelFormat` **26** =
  `R11G11B10_Float`, and allocates the temporal + ray-trace buffers with it.
- The Vulkan backend's `VulkanConvertExtensions.ConvertPixelFormat`
  (`.../stride.graphics/4.3.0.2507/lib/net10.0/**Vulkan**/Stride.Graphics.dll`) switches on
  `inputFormat - 2`; the case for `24` (i.e. format 26) falls through to
  `throw new InvalidOperationException("Unsupported texture format: " + …)`.

So the first frame with SSR enabled throws. Combined with §1.2a — `AddCleanUIStage` re-enabling
it — this is why `Client/Program.cs:139-143` exists.

### 9.3 Particles: two separate problems

**(a) `[IL]` The default compositor cannot draw particles at all.**
`CreateDefault` adds no particle render feature, so a `ParticleSystemComponent` simulates on the
CPU and renders nothing. `game.AddParticleRenderer()` `[toolkit]` →
`GraphicsCompositorExtensions.AddParticleStagesAndFeatures()` adds a
`ParticleEmitterRenderFeature` with a `ParticleEmitterTransparentRenderStageSelector` bound to
the `Opaque` and `Transparent` stages (and throws `NullReferenceException` if either stage is
missing).

**(b) `UNVERIFIED: (repo comment only)` — that render feature AccessViolations on Vulkan.**
Per `Client/Program.cs:144-150`: `ParticleEmitterRenderFeature` crashes with an
`AccessViolationException` on the Vulkan backend, a known engine bug
(**stride3d/stride#2496**); the official Stride particle samples crash the same way on Linux.
Re-enable once fixed upstream or if the project moves back to OpenGL.

Consequence: `AddParticleRenderer()` is commented out (`Program.cs:150`) and
`ParticleExample.CreateAtOrigin()` is commented out with it (`Program.cs:190-192`) so nothing
simulates invisibly and burns CPU. `Client/Rendering/ParticleExample.cs` is kept as dead
reference code.

### 9.4 `UNVERIFIED: (repo comment only)` — never set `IsFullScreen` before `Run()`

Per the top-level NOTE at `Client/Program.cs:100-103`: setting
`GraphicsDeviceManager.IsFullScreen` before `game.Run(...)` makes the SDL/Linux backend create
an **exclusive-fullscreen swapchain whose pixel format resolves to `None`**, which then causes a
`DivideByZeroException` in `InitDefaultRenderTarget`.

Do window configuration inside `Start()` instead (`Client/Program.cs:156-160`):

```csharp
// game.Window.FullscreenIsBorderlessWindow = true;   // borderless is the safe route
// game.GraphicsDeviceManager.IsFullScreen = true;
game.GraphicsDeviceManager.PreferredBackBufferWidth = 800;
game.GraphicsDeviceManager.PreferredBackBufferHeight = 600;
game.GraphicsDeviceManager.ApplyChanges();
```

### 9.5 `[IL]` A shader with **zero** resource bindings can't build a pipeline

The Vulkan `PipelineState.CreatePipelineLayout` builds a dictionary of the shader's resource
bindings and then calls:

```csharp
new DescriptorSetLayoutBuilder.Entry[dictionary.Max(x => x.Value) + 1]
```

`Enumerable.Max` on an empty sequence throws `InvalidOperationException: Sequence contains no
elements`. Any custom SDSL used through `MutablePipelineState` therefore needs **at least one
bound parameter**. That is the documented reason `assets/shaders/LineColorShader.sdsl` declares
`stage float AlphaScale` — it doubles as a global fade knob and as the required binding.

### 9.6 Build-side Linux workarounds (context)

Two targets in `DemiurgeSharp.csproj` exist purely for Linux and are version-pinned to
`4.3.0.2507` — update them on a Stride upgrade:

- `SetStrideNativeLibPathForLinux` — Stride's `.ssdeps` uses Windows backslash paths, so the
  asset-compiler subprocess can't find native `.so` files; the target sets `LD_LIBRARY_PATH` in
  the MSBuild process and the child inherits it.
- `DeployGlslangValidator` — **Vulkan runtime shader compilation shells out to
  `linux-x64/glslangValidator.bin`** relative to the process working directory. NuGet doesn't
  deploy that contentFile transitively, so the target `cp -p`s it from the package cache.
  If shader compilation fails at runtime with a missing binary, check this first.

---

## 10. Appendix: which assembly holds what

Base path: `~/.nuget/packages/<pkg>/4.3.0.2507/lib/net10.0/`. XML docs ship beside each DLL.

```bash
ilspycmd -t <FullTypeName> <dll>        # decompile a type
ilspycmd -l class <dll>                 # list types
# single-quote backtick generics in bash:  ilspycmd -t 'Ns.Type`1' x.dll
```

| Namespace / type | Package → DLL |
|---|---|
| `Stride.Rendering.Compositing.SceneRendererBase`, `RendererCoreBase`, `RenderDrawContext`, `EffectSystem(Extensions)`, `Images.PostProcessingEffects`, `Images.LocalReflections` | `stride.rendering` → `Stride.Rendering.dll` |
| `Stride.Rendering.Compositing.GraphicsCompositorHelper`, `ForwardRenderer`; `Stride.Engine.*` (`LightComponent`, `ModelComponent`); `Stride.Profiling.*` | `stride.engine` → `Stride.Engine.dll` |
| `Stride.Graphics.Texture`, `FastTextRenderer`, `CommandList` | `stride.graphics` → `Stride.Graphics.dll` |
| `Stride.Graphics.PixelFormat`, `Image`, `StandardImageHelper`, `DDSHelper` | `stride` → `Stride.dll` |
| **Vulkan backend** (`PipelineState`, `VulkanConvertExtensions`) | `stride.graphics` → `**Vulkan**/Stride.Graphics.dll` |
| `Stride.UI.*`, `Stride.Engine.UIComponent`, `UIPage`, `Stride.Rendering.UI.UIRenderFeature` | `stride.ui` → `Stride.UI.dll` |
| `Stride.Shaders.Compiler.EffectCompilerBase` | `stride.shaders` → `Stride.Shaders.dll` |
| `Stride.Particles.Rendering.ParticleEmitterRenderFeature` | `stride.particles` → `Stride.Particles.dll` |
| **All `[toolkit]` extensions** (`GameExtensions`, `GraphicsCompositorExtensions`, `MaterialExtensions`, gizmos, procedural models) | `stride.communitytoolkit` (`1.0.0-preview.62`) → `Stride.CommunityToolkit.dll` |

Repo files referenced throughout:
`Client/Program.cs` (compositor setup, lights, materials) ·
`Client/Rendering/LineRenderer.cs` (custom `SceneRendererBase`) ·
`Client/Rendering/GrassField.cs` (instancing, cutout material) ·
`Client/Rendering/TracerManager.cs` (persistent draws over the immediate-mode queue) ·
`Client/View/HUD.cs` (UI) · `assets/shaders/*.sdsl` · `DemiurgeSharp.csproj` · `DemiurgeSharp.sdpkg`.
