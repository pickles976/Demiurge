# Asset Loading Pipeline

## Overview

DemiurgeSharp uses a two-stage asset pipeline for GLTF models:

1. **Compile-time (MSBuild):** The `SyncStrideGltfAssets` target runs the `GltfAssetGenerator` tool, which scans `assets/` for `.gltf` files and generates all Stride asset descriptors needed by the Stride asset compiler.
2. **Runtime:** The game loads compiled models with a plain `Content.Load<Model>(contentPath)` call. No SharpGLTF or image decoding happens at runtime.

This keeps runtime code simple and lets Stride's asset compiler handle texture/material baking consistently.

## MSBuild Step: `SyncStrideGltfAssets`

Defined in `DemiurgeSharp.csproj`, this target runs before `StrideCompileAsset` for every build.

It invokes the `GltfAssetGenerator` console tool (`tools/GltfAssetGenerator`), which uses SharpGLTF to parse each `assets/**/*.gltf` file and emits the corresponding Stride descriptors. Keeping the generator as a standalone console app avoids wrestling with inline-task assembly references and makes the pipeline easier to maintain and debug.

For each glTF source the tool:

1. Loads the model with **SharpGLTF**, using `ValidationMode.Skip`. Real-world exports (e.g. Sketchfab) frequently contain benign spec violations — such as a `byteStride` on animation-sampler accessors — that the strict validator would reject.
2. Extracts embedded/referenced images to `.png` or `.jpg` (detected from magic bytes). No image decoding is performed — the raw bytes are written directly.
3. Writes a `.sdtex` texture descriptor for each image.
4. Writes a `.sdmat` material descriptor for each GLTF material.
   - If the material has a BaseColor texture, the material references the corresponding `.sdtex`.
   - Otherwise it falls back to a solid white diffuse color.
5. Writes an `.sdm3d` model descriptor with a populated `Materials:` list (and, when a skeleton is generated, a `Skeleton:` reference).
6. If the source has **any animation or a skin**, writes a `.sdskel` skeleton descriptor and a `.sdanim` animation descriptor per animation. See [Skeletons and Animation](#skeletons-and-animation) for why these are required.
7. Rewrites `DemiurgeSharp.sdpkg` so every model, skeleton, and animation is listed under `RootAssets`.

Textures and materials do **not** need to be in `RootAssets`; they are compiled transitively through the model's asset references. Skeletons and animations are listed explicitly for clarity, though skeletons would also be reached transitively.

## Generated Files

For a source file `assets/models/ak47.gltf` the tool generates:

| File | Type | Purpose |
|------|------|---------|
| `ak47_tex0.png` | Raw image | Extracted diffuse/color texture |
| `ak47_tex0.sdtex` | `TextureAsset` | Tells Stride how to import the image |
| `ak47_mat0.sdmat` | `MaterialAsset` | Diffuse material, referencing the texture |
| `ak47.sdm3d` | `ModelAsset` | References the GLTF source and its materials |

Multiple images/materials produce numbered variants: `_tex0`, `_tex1`, `_mat0`, `_mat1`, etc.

A source with animations or a skin additionally produces:

| File | Type | Purpose |
|------|------|---------|
| `<base>_skeleton.sdskel` | `SkeletonAsset` | Node hierarchy the model and animations bind to |
| `<base>_anim_<name>.sdanim` | `AnimationAsset` | One per source animation; `<name>` is the sanitized clip name |

For example `basil.gltf` (one animation named `walk`) also yields `basil_skeleton.sdskel` and `basil_anim_walk.sdanim`, and `girl_mechanic/scene.gltf` (four animations) yields `scene_skeleton.sdskel` plus `scene_anim_root_Girl_walk.sdanim`, `scene_anim_root_Girl_run.sdanim`, etc.

### Example generated YAML

These are the formats the tool emits for Stride 4.3.x. If you upgrade Stride and the asset compiler starts rejecting files, compare these templates with freshly hand-authored assets.

`.sdtex`:
```yaml
!Texture
Id: {tex-guid}
SerializedVersion: {Stride: 2.0.0}
Tags: []
Source: !file ak47_tex0.png
IsCompressed: false
Type: !ColorTextureType
    UseSRgbSampling: true
    ColorKeyColor: {R: 255, G: 0, B: 255, A: 255}
    Alpha: Interpolated
    PremultiplyAlpha: false
GenerateMipmaps: false
IsStreamable: false
```

`.sdmat` with a texture:
```yaml
!MaterialAsset
Id: {mat-guid}
SerializedVersion: {Stride: 2.0.0}
Tags: []
Attributes:
    Diffuse: !MaterialDiffuseMapFeature
        DiffuseMap: !ComputeTextureColor
            Key: Material.DiffuseMap
            Texture: {tex-guid}:models/ak47_tex0
            Filtering: Point
    DiffuseModel: !MaterialDiffuseLambertModelFeature {}
```

`.sdm3d` (with a skeleton, as emitted for an animated/skinned source):
```yaml
!Model
Id: {model-guid}
SerializedVersion: {Stride: 2.0.0}
Tags: []
Source: !file basil.gltf
Skeleton: {skeleton-guid}:models/basil_skeleton
Materials:
    -   Name: Material
        MaterialInstance:
            Material: {mat-guid}:models/basil_mat0
```

The `Skeleton:` line is omitted for sources without animation or a skin (e.g. `ak47.gltf`).

`.sdskel`:
```yaml
!Skeleton
Id: {skeleton-guid}
SerializedVersion: {Stride: 2.0.0}
Tags: []
Source: !file basil.gltf
Nodes: []
```

An empty `Nodes: []` makes `SkeletonAsset.Compact` false, so Stride imports the **full** node hierarchy from the source with every node preserved (and de-duplicates node names internally). We deliberately do not enumerate the nodes — see the caveat in [Skeletons and Animation](#skeletons-and-animation).

`.sdanim`:
```yaml
!Animation
Id: {anim-guid}
SerializedVersion: {Stride: 2.0.0}
Tags: []
Source: !file basil.gltf
AnimationStack: 0
Skeleton: {skeleton-guid}:models/basil_skeleton
RepeatMode: LoopInfinite
Type: !StandardAnimationAssetType {}
```

`AnimationStack` is the **integer index** of the source animation (the position in the glTF's `animations` array). It selects which clip this asset compiles — `0` for the first animation, `2` for the third, etc.

## GUID Strategy

All asset GUIDs are deterministic so the same source file always produces the same IDs across clean builds.

A GUID is the MD5 hash of the asset's **content path** (relative to `assets/`, no extension, forward slashes), formatted as `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.

Examples:

- Model `assets/models/ak47.gltf` → content path `models/ak47` → model GUID.
- Texture `assets/models/ak47_tex0.png` → content path `models/ak47_tex0` → texture GUID.
- Material `assets/models/ak47_mat0.sdmat` → content path `models/ak47_mat0` → material GUID.

Because GUIDs are stable, the `.sdpkg` manifest and asset references remain consistent even if the tool regenerates files.

## Asset Reference Chain

The Stride compiler follows references from the model down to the texture:

```
.sdm3d (Model)
  ├─ Materials → references .sdmat (Material)
  │     └─ DiffuseMap → references .sdtex (Texture)
  │           └─ Source → references .png/.jpg image
  └─ Skeleton → references .sdskel (Skeleton)   [animated/skinned sources only]

.sdanim (Animation)
  ├─ Source   → references the .gltf
  └─ Skeleton → references the same .sdskel
```

Both the model and each animation reference the **same** skeleton asset, which keeps their node ordering aligned so animation curves land on the right `NodeTransformations` at runtime. The `.sdm3d` and `.sdanim` assets are listed in `RootAssets`; materials and textures are discovered transitively.

## Runtime Loading

Models are loaded with the standard Stride content pipeline:

```csharp
Model LoadModel(string gltfPath)
{
    var contentPath = Path.ChangeExtension(Path.GetRelativePath("assets", gltfPath), null);
    return game.Content.Load<Model>(contentPath);
}
```

Example usage:

```csharp
var ak = new Entity("AK47") { new ModelComponent(LoadModel("assets/models/ak47.gltf")) };
```

All textures and materials are already baked into the compiled model; no runtime SharpGLTF or image decoding is required.

## Skeletons and Animation

### Why a skeleton is mandatory

Stride imports glTF geometry and animation through **Assimp**. Decompiling the asset compiler (`Stride.Assets.Models.ImportModelCommand`) shows that the per-node animation curves are only emitted when the `AnimationAsset` references a skeleton:

```csharp
if (SkeletonUrl != null) {
    // emit curves like [ModelComponent.Key].Skeleton.NodeTransformations[i].Transform.Rotation
}
```

Without a skeleton it falls back to exporting only the **root** node's transform. A clip that animates non-root nodes (arms, legs, bones) then produces zero channels, and the build fails with:

```
File <model>.gltf doesn't have any animation information.
```

This is true for **both** rigid/node animation (e.g. Blockbench's `basil.gltf`) and skinned/armature animation (e.g. Sketchfab's `girl_mechanic`). The animation *style* is irrelevant — the missing skeleton reference is the cause. (In the Stride GameStudio editor this is hidden because importing a rigged model auto-creates and wires up the skeleton; this headless pipeline does it explicitly instead.) Skinned meshes also need the skeleton to deform at runtime, so the generator emits one whenever the source has **any animation or a skin**.

### Selecting a clip from a multi-animation source

A single glTF often contains several animations. Each compiled `.sdanim` picks exactly one via `AnimationStack` — the integer index into the glTF's `animations` array. The generator emits one `.sdanim` per animation, named after the (sanitized) clip name, each with the matching index.

### Runtime playback

The model and each clip are loaded separately, then driven by an `AnimationComponent`:

```csharp
var girl = new Entity("GIRL") { new ModelComponent(LoadModel("assets/models/girl_mechanic/scene.gltf")) };

var animations = new AnimationComponent();
girl.Add(animations);
animations.Animations.Add("walk", game.Content.Load<AnimationClip>("models/girl_mechanic/scene_anim_root_Girl_walk"));
animations.Play("walk");

girl.Scene = rootScene;
```

The dictionary key (`"walk"`) is the name you `Play()`; the content path is the compiled `.sdanim`. `using Stride.Animations;` is required for `AnimationClip`.

### Caveats

- **`N node(s) … not in asset [<base>_skeleton], please reimport` (warning).** Harmless. The `.sdskel` uses `Nodes: []`, which correctly preserves every node; the warning only means the asset doesn't enumerate them for the editor UI. We can't reproduce that list from SharpGLTF anyway, because Assimp injects its own nodes (`RootNode`, `_rootJoint`, `$AssimpFbx$` pivots, etc.) that aren't in the glTF.
- **`Unable to properly determine target node … duplicate name` (warning).** Emitted for sources whose animated nodes share names — Blockbench exports a group node and a mesh node with the same name (`basil`'s `arm_left`, `leg_right`, …). The clip still plays, but Stride targets the first node of that name, so a limb may animate slightly wrong. Fix it at the source by giving nodes unique names (or rigging with a proper armature, whose bones are uniquely named).

## Adding a New Model

1. Drop the `.gltf` file anywhere under `assets/` (e.g. `assets/models/myweapon.gltf`).
2. Run `dotnet run` (or `dotnet build`).
3. The MSBuild target runs `GltfAssetGenerator` and creates the descriptors automatically — including a skeleton and one animation asset per clip if the source is animated or skinned.
4. Load the model with `LoadModel("assets/models/myweapon.gltf")`, and any clip with `game.Content.Load<AnimationClip>("models/myweapon_anim_<name>")`.

No manual asset creation, GUID assignment, or `.sdpkg` editing is required.

## TextureAsset YAML pitfalls

These are easy mistakes that Stride silently accepts without error but produces wrong results:

- **Do not add `Width`/`Height` fields.** `IsSizeInPercentage` defaults to `true` in Stride, so `Width: 64` means *64% of source size*, not 64 pixels. This compiles a 64×64 source down to ~41×41 and causes blurry output. Omit both fields entirely; Stride defaults to 100% (full source resolution).

- **`IsSRgb: true` is not a valid `TextureAsset` property** — Stride's YAML deserializer silently ignores unknown fields. sRGB is declared via `Type: !ColorTextureType { UseSRgbSampling: true }`. Without a `Type`, Stride falls back to a default that may not handle color-space correctly.

## Troubleshooting

- **Textures appear blurry:**
  - Confirm the generated `.sdtex` files do not have `Width`/`Height` fields (see pitfalls above).
  - Confirm `Type: !ColorTextureType` is present, not the obsolete `IsSRgb: true` field.
  - `GenerateMipmaps: false` and `Filtering: Point` in the `.sdmat` are correct for nearest-neighbor sampling.
  - Run `dotnet clean && dotnet build` to force a full texture recompile after changing `.sdtex` files.

- **Model loads but has no texture:**
  - Check that the GLTF material has a `BaseColor` texture.
  - Inspect the generated `.sdmat` and `.sdm3d` YAML.
  - Run `dotnet clean && dotnet build` to force a full asset recompile.

- **Build errors about missing assets:**
  - Ensure the `.gltf` is under `assets/`.
  - Verify `DemiurgeSharp.sdpkg` lists the model under `RootAssets`.

- **`<model>.gltf doesn't have any animation information`:**
  - The `.sdanim` is missing its `Skeleton:` reference, or no `.sdskel` was generated. Animation curves on non-root nodes require a skeleton — see [Skeletons and Animation](#skeletons-and-animation).
  - Confirm the `.sdm3d` and `.sdanim` both reference the same `<base>_skeleton`.

- **Wrong clip plays, or all `.sdanim` files animate identically:**
  - Check that each `.sdanim` has the correct `AnimationStack` index for its clip. The index is the clip's position in the glTF `animations` array.

- **`SchemaException: ... must NOT be defined` while generating:**
  - SharpGLTF's strict validator rejected a real-world export. The generator already loads with `ValidationMode.Skip`; if you change that code, keep validation relaxed.

- **YAML serialization mismatch:**
  - Stride's YAML expectations for `MaterialInstance`, shader keys, or texture filtering enums can change between versions. If the asset compiler rejects generated files, compare a hand-authored working asset with the generated one and adjust the templates in `tools/GltfAssetGenerator/Program.cs`.
