`
Unhandled exception. System.ArgumentException: Navigation cell NavCell { X = -40, Y = 15, Z = 80, Key = -10995115950065, CentreXZ = <-39.5, 80.5> } is not standable (Parameter 'cell')
   at Demiurge.NavTraversal.Position(ChunkMap map, NavCell cell) in /home/sebas/Projects/DemiurgeSharp/Common/Navigation/NavTraversal.cs:line 255
   at Demiurge.GameServer.NavigationSystem.TryReuseSharedRoute(PathRequest request, NavPath& path) in /home/sebas/Projects/DemiurgeSharp/Server/Ai/NavigationSystem.cs:line 434
   at Demiurge.GameServer.NavigationSystem.Process(PathRequest request) in /home/sebas/Projects/DemiurgeSharp/Server/Ai/NavigationSystem.cs:line 249
   at Demiurge.GameServer.NavigationSystem.Work() in /home/sebas/Projects/DemiurgeSharp/Server/Ai/NavigationSystem.cs:line 232
   at System.Threading.Thread.StartCallback()
`

- AI not pathfinding across bridges, falling into giant trenches.
   - The test map has deep trenches dug with a 15m spherical brush, with bridges across. Sometimes the NPCs will pathfind across the bridges, other times they will fall in
- AI still cannot dig itself out of trenches, it struggles to dig stairs. It digs big chunks out of the walls of the trench, but does not dig upward.
   - NPCs need to know how to "stair" upwards, a common movement/digging technique in voxel games
- AI digging too slowly, not making enough progress
- Chunk changes are slow, get significantly worse when lots of NPCs are digging
- AI not digging enough cover for itself. They need to dig a foxhole to cover themselves first, and then expand the walls (not just a 1x1 hole)
- Model teleports back to spawn when NPC dies