# Linux Setup Guide for DemiurgeSharp (Stride code-only project)

Stride was designed primarily for Windows. Running it on Linux requires a few one-time system packages and a couple of build workarounds already present in `DemiurgeSharp.csproj`. This document explains what to install, why each piece is needed, and what to do if a clean build breaks.

---

## 1. System packages

Install these once on your machine:

```bash
sudo apt install libfreeimage-dev
```

`libfreeimage-dev` pulls in `libfreeimage3` (the runtime) and provides the unversioned `/usr/lib/x86_64-linux-gnu/libfreeimage.so` symlink. Stride's asset compiler calls `dlopen("libfreeimage.so")` to load FreeImage at runtime. Without the symlink the asset compiler crashes on every clean build.

> **Why not just `libfreeimage3`?** The runtime package installs `libfreeimage.so.3` (versioned). The system linker won't resolve an unversioned `dlopen("libfreeimage.so")` call against it. The `-dev` package adds the unversioned symlink that makes it work.

---

## 2. `StrideGraphicsApi` must be set to `OpenGL`

Stride defaults to `Direct3D11` when `StrideGraphicsApi` is not set. D3D11 is Windows-only.

**Fix already in `DemiurgeSharp.csproj`:**
```xml
<StrideGraphicsApi>OpenGL</StrideGraphicsApi>
```

Without this you get `DllNotFoundException: Unable to load shared library 'dxgi.dll'` at runtime.

---

## 3. Native `.so` files and the `LD_LIBRARY_PATH` workaround

Stride ships native libraries (`.so` files on Linux) inside NuGet packages under `runtimes/linux-x64/native/`. However, Stride's `.ssdeps` manifest files record those paths using **Windows backslashes** (e.g. `runtimes\linux-x64\native\DxtWrapper.so`). The Stride NuGet resolver running on Linux can't parse those paths, so it never registers the native libs with .NET's loader.

When the asset compiler runs as a subprocess it loads the full Stride engine, which immediately tries to `dlopen` several native libraries via `NativeLibraryHelper.PreloadLibrary`. If those `.so` files aren't on the search path the static constructors throw `TypeInitializationException` and asset compilation aborts.

**Affected packages and their native libs:**

| NuGet package | Native library | Used by |
|---|---|---|
| `Stride.TextureConverter` | `DxtWrapper.so`, `PVRTexLib.so` | Texture/sprite font compilation |
| `Stride.Physics` | `libbulletc.so` | Physics module initializer |
| `Stride.Audio` | `libstrideaudio.so` | Audio module initializer |
| `Stride.Navigation` | `libstridenavigation.so` | Navigation module initializer |
| `Stride.Assets` | `VHACD.so` | Convex hull decomposition |

**Fix already in `DemiurgeSharp.csproj`:**

An inline MSBuild task sets `LD_LIBRARY_PATH` on the MSBuild process before the asset compiler subprocess is spawned. Child processes inherit the env var, so the system linker finds the `.so` files.

```xml
<Target Name="SetStrideNativeLibPathForLinux" BeforeTargets="StrideCompileAsset"
        Condition="$([MSBuild]::IsOSPlatform('Linux'))">
  <SetProcessEnvVar Name="LD_LIBRARY_PATH"
                    Value="$(NuGetPackageRoot)stride.textureconverter/4.3.0.2507/runtimes/linux-x64/native:
                           $(NuGetPackageRoot)stride.physics/4.3.0.2507/runtimes/linux-x64/native:
                           $(NuGetPackageRoot)stride.audio/4.3.0.2507/runtimes/linux-x64/native:
                           $(NuGetPackageRoot)stride.navigation/4.3.0.2507/runtimes/linux-x64/native:
                           $(NuGetPackageRoot)stride.assets/4.3.0.2507/runtimes/linux-x64/native:
                           $([System.Environment]::GetEnvironmentVariable('LD_LIBRARY_PATH'))" />
</Target>
```

> **When upgrading Stride packages:** update the version number (`4.3.0.2507`) in all five paths to match the new package version.

---

## 4. Why errors only appear on a clean build

Stride's asset compiler caches compiled asset results in `obj/stride/assetbuild/data/`. On an incremental build the compiler checks hashes, finds assets up-to-date, and exits before loading most Stride subsystems — so `libbulletc.so`, `DxtWrapper.so`, etc. are never touched.

When you `dotnet clean` or use `--no-incremental`, the cache is wiped and the compiler does a full compilation pass. Every Stride subsystem (physics, audio, texture converter, navigation) initializes, and any missing native library causes an immediate crash.

**The fix is already in place** — but if you ever see `TypeInitializationException` or `Could not load native library` errors after a clean build, the cause is almost certainly a missing entry in the `LD_LIBRARY_PATH` target or a missing system package.

---

## 5. Quick diagnostics

If a clean build fails with asset compiler errors:

1. Run the asset compiler directly with the same `LD_LIBRARY_PATH` the build target sets and capture the **inner exception** — the top-level `TypeInitializationException` always wraps the real error:

   ```bash
   LD_LIBRARY_PATH="$HOME/.nuget/packages/stride.textureconverter/4.3.0.2507/runtimes/linux-x64/native:$HOME/.nuget/packages/stride.physics/4.3.0.2507/runtimes/linux-x64/native:..." \
   dotnet "$HOME/.nuget/packages/stride.core.assets.compilerapp/4.3.0.2507/lib/net10.0/Stride.Core.Assets.CompilerApp.dll" \
     --disable-auto-compile --project-configuration Debug --platform=Windows \
     --compile-property:StrideGraphicsApi=OpenGL \
     --output-path="bin/Debug/net10.0/data" \
     --build-path="obj/stride/assetbuild/data" \
     --package-file="DemiurgeSharp.csproj" 2>&1 | grep -A 10 "InnerException\|Could not load"
   ```

2. The inner exception names the missing library, e.g. `Could not load native library libbulletc`. Search for that `.so` in the NuGet cache:

   ```bash
   find ~/.nuget/packages -path "*/runtimes/linux-x64/native/*.so" | grep bulletc
   ```

3. Add that package's native path to the `LD_LIBRARY_PATH` value in `DemiurgeSharp.csproj`.

4. If the library isn't in any NuGet package (like `freeimage`), it's a system dependency — install it via apt.
