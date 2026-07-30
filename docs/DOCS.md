
# Documentation Index

Start with:

- [README](../README.md): build, launch, editor workflow, project layout
- [Architecture](ARCHITECTURE.md): runtime boundaries, authority, AI layers, and threading
- [AI implementation](../AI_IMPLEMENTATION.md): current behavior and the staged design record
- [NPC navigation](NAVIGATION.md): traversal, path workers, reuse, recovery, and diagnostics
- [Performance](../PERFORMANCE.md): measured regressions, fixes, and current baselines
- [Recipes](../RECIPES.md): append-only checklists for extending gameplay and replication
- [Editor](../EDITOR.md): source/runtime map formats and editor lifecycle
- [Commands](COMMANDS.md): developer, server, map, and editor commands

Subsystem references:

- [Networking](networking/): movement, shooting, object replication, and robustness
- [Voxel terrain](voxel/): data model, generation, meshing, and collision
- [Stride runtime notes](../stride_docs/): code-only engine, assets, rendering, UI, and platform findings
- [Design specifications](superpowers/specs/) and [implementation plans](superpowers/plans/)
- [Scratchpad](scratchpad/): non-authoritative art, audio, camera, and terrain notes

Useful Stride vocabulary:

- **Scene:** container for entities.
- **Entity:** model, camera, light, or an aggregate of entity components.
- **EntityComponent:** behavior or data attached to an entity.
- **BodyComponent:** physics component.
- **Graphics compositor:** render-stage and post-processing organization.

External references:

- [Camera controls reference](https://www.youtube.com/watch?v=ijN3gobR6Zo&t=1s)
- [Stride development blog](https://www.vaclavelias.com/)
