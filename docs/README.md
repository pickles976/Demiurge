# Documentation Index

Start with:

- [README](../README.md): build, launch, editor workflow, project layout
- [CLAUDE](../CLAUDE.md): architecture invariants, performance targets, and implementation guidance
- [Architecture](ARCHITECTURE.md): runtime boundaries, authority, AI layers, and threading
- [NPC navigation](NAVIGATION.md): traversal, path workers, reuse, recovery, and diagnostics
- [Time-costed pathfinding](BARITONE.md): A* traversal design, performance budgets, and rollout plan
- [Recipes](RECIPES.md): append-only checklists for extending gameplay and replication
- [Editor](EDITOR.md): source/runtime map formats and editor lifecycle
- [Commands](COMMANDS.md): developer, server, map, and editor commands
- [Asset loading](ASSET_LOADING.md): glTF synchronization and Stride asset descriptors
- [Linux setup](LINUX_SETUP_GUIDE.md): platform-specific development setup

Subsystem references:

- [Networking](networking/): movement, shooting, object replication, and robustness
- [Voxel terrain](voxel/): data model, generation, meshing, and collision
- [Stride runtime notes](stride/): code-only engine, assets, rendering, UI, and platform findings
- [Design specifications](superpowers/specs/) and [implementation plans](superpowers/plans/)
- [Scratchpad](scratchpad/): non-authoritative art, audio, camera, and terrain notes
- [Multiplayer game programming reference](<Multiplayer Game Programming.pdf>)

Plans and work tracking:

- [Threading plan](THREAD.md): process and simulation threading analysis
- [Weapons plan](WEAPONS.md): weapon-system requirements and implementation notes
- [Current work](TODO.md): active voxel-terrain milestones and deliberately deferred work

Useful Stride vocabulary:

- **Scene:** container for entities.
- **Entity:** model, camera, light, or an aggregate of entity components.
- **EntityComponent:** behavior or data attached to an entity.
- **BodyComponent:** physics component.
- **Graphics compositor:** render-stage and post-processing organization.

External references:

- [Camera controls reference](https://www.youtube.com/watch?v=ijN3gobR6Zo&t=1s)
- [Stride development blog](https://www.vaclavelias.com/)
