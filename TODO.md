# Art Rules
use 32x32 textures in Blockbench
use 16x16 textures for the ground
For weapons, need to model sights and add anchors for camera to figure out where to position weapon for ADS

Use Codex for actual engineering work, Claude for just adding tiny features.

1. Finish PVP MVP
2. Make the environment richer
3. Refactor commands
4. Finish up AI with Codex
5. Multiplayer test

- Performance improvements

# Map Editor
- [ ] improve ergonomics of terminal
  - [ ] autocomplete
  - [ ] better grouping of functions
- [ ] Add a command to debug draw chunk borders
- [ ] add map editor and structure editor UI block select, object select
  - [ ] hold Q to open menu
  - [ ] Garry's mod-style object selection. Use the thumbnail textures for item previews.
  - [ ] UI for saving and stuff

# PVP Demo

1. Minecraft style launch page.
2. Singleplayer mode. Only 1 map and 1 gamemode for now. Set the number of NPCs. Set number of tickets.
3. Multiplayer server list, add server and direct connect. Server says map, gamemode, active players, # NPCs, ping, and currently open slots.
3a. Configure logs that we can use to figure out the best weapon, how many reconciliation events happened, etc. Save one log per game.
4. Use `dotnet run -- --singleplayer` + a test flag to launch into conquest immediately the way we do today.
5. Add gameover screen with map voting. Game ends when tickets = 0. Show scoreboard and map vote screen (only one map currently).
6. Update server executable to let admin run commands from the terminal.
7. Set fake network latency and jitter from command line for testing
8. Test over VPN
9. Host a server on digitalocean and test
10. Host demo

- [ ] Host a server and run external multiplayer playtests
  - [ ] Provision a DigitalOcean host
  - [ ] Configure the scrungy.com domain
- [ ] Track and fix issues found by the Demo

## Refactoring and Cleanup
- Client performance
- Finish AI
# NPC AI

Claude is too stupid for AI behavioral stuff. Just use it for simple stuff.
The consolidated status and remaining work live in [AI_TODO.md](AI_TODO.md).
  - finish stuff in AI_TODO.md
  - AI needs to be more challenging and aggressive
- Finish PVP features

- Codex refactor certain parts of the code
- Codex finish AI
- Codex event queue implementation

## Stride Upgrade

Try to upgrade Stride version and get particle system working
https://github.com/stride3d/stride-community-toolkit/tree/stride-4.4/examples/code-only/Example12_Particles


# Debugging And Known Issues
- [ ] Revisit the particle system after Stride issue 2496 is resolved:
  https://github.com/stride3d/stride/issues/2496

# Research References

- Grass system: https://nicogo1705.github.io/AssetStore/asset?id=com.nicogo.grass
- SDSL overview: https://hackmd.io/@vN9HDo5XQAGVCM_epmoJBA/S1LxeorWT
- Dual contouring:
  https://www.boristhebrave.com/2018/04/15/dual-contouring-tutorial/
- Surface nets and texturing:
  https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14
- Tree and grass shader reference:
  https://www.youtube.com/watch?v=GOfttJQ-FGw&t=19s
- Additional video references:
  https://www.youtube.com/watch?v=Y0Ko0kvwfgA
  https://m.youtube.com/watch?v=PLMcCKeJ6f0&list=WL&index=56&pp=iAQBsAgC
