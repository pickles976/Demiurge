# OBJECTS

## Spawning

- `GameWorld` holds `ObjectRegistry` and `WeaponSystem`. `ObjectReplication.cs` creates new `ServerObject` and adds to dict.

- Sends `ObjectSpawnData` over the wire. Has id, object type, bundle w/ mask and components.

- Arrives in client's `NetworkManager`. Netman creates new `NetObject`, copies components, and maps into dict.

- `ObjectViewFactory` spawns the models and adds Stride components.

## Syncing