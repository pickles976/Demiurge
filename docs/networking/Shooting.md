# Shooting

`LocalPlayerController.cs` -> `local.TryFire()`
sends `position`, `sequence`, and `renderTick` over the wire.


`PlayerFireData` goes to server

`GameServer` -> `GameWorld` -> `ApplyFire` -> `Raycast` -> Rewind Players