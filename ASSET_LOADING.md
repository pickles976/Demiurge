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

1. Loads the model with **SharpGLTF**.
2. Extracts embedded/referenced images to `.png` or `.jpg` (detected from magic bytes). No image decoding is performed — the raw bytes are written directly.
3. Writes a `.sdtex` texture descriptor for each image.
4. Writes a `.sdmat` material descriptor for each GLTF material.
   - If the material has a BaseColor texture, the material references the corresponding `.sdtex`.
   - Otherwise it falls back to a solid white diffuse color.
5. Writes an `.sdm3d` model descriptor with a populated `Materials:` list.
6. Rewrites `DemiurgeSharp.sdpkg` so every model is listed under `RootAssets`.

Textures and materials do **not** need to be in `RootAssets`; they are compiled transitively through the model's asset references.

## Generated Files

For a source file `assets/models/ak47.gltf` the tool generates:

| File | Type | Purpose |
|------|------|---------|
| `ak47_tex0.png` | Raw image | Extracted diffuse/color texture |
| `ak47_tex0.sdtex` | `TextureAsset` | Tells Stride how to import the image |
| `ak47_mat0.sdmat` | `MaterialAsset` | Diffuse material, referencing the texture |
| `ak47.sdm3d` | `ModelAsset` | References the GLTF source and its materials |

Multiple images/materials produce numbered variants: `_tex0`, `_tex1`, `_mat0`, `_mat1`, etc.

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

`.sdm3d`:
```yaml
!Model
Id: {model-guid}
SerializedVersion: {Stride: 2.0.0}
Tags: []
Source: !file ak47.gltf
Materials:
    -   Name: Material
        MaterialInstance:
            Material: {mat-guid}:models/ak47_mat0
```

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
  └─ Materials → references .sdmat (Material)
        └─ DiffuseMap → references .sdtex (Texture)
              └─ Source → references .png/.jpg image
```

Only the `.sdm3d` model needs to be listed in `RootAssets` of the `.sdpkg`; the compiler discovers and compiles materials and textures automatically.

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

## Adding a New Model

1. Drop the `.gltf` file anywhere under `assets/` (e.g. `assets/models/myweapon.gltf`).
2. Run `dotnet run` (or `dotnet build`).
3. The MSBuild target runs `GltfAssetGenerator` and creates the descriptors automatically.
4. Load it in code: `LoadModel("assets/models/myweapon.gltf")`.

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

- **YAML serialization mismatch:**
  - Stride's YAML expectations for `MaterialInstance`, shader keys, or texture filtering enums can change between versions. If the asset compiler rejects generated files, compare a hand-authored working asset with the generated one and adjust the templates in `tools/GltfAssetGenerator/Program.cs`.
