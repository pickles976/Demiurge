# Local Players

Because we use a server-authoritative architecture, player position doesn't get written until the server applies player input. This means that in a naive implementation, the time from key press to player model moving is RTT.

To account for this, Local Players use client-side prediction to reduce the amount of lag that players feel.


1. Player Input is read
`/Client/View/LocalPlayerController.cs`
- Holds a reference to a `LocalPlayer` class
- `Update` calls `ComputeIntent`, returns `intent` which is applied to `LocalPlayer` instance.
- In `LocalPlayer.Update` an accumulator gets the `dt` from each frame. This is done so that a game running at 144fps will still run its' simulation at a fixed 30 ticks like the server. When the accumulator is greater than `FixedRateDt` we 
a. Apply moves locally
```C#
PlayerMovement.Step(terrain.Map, ref Move, move.Intent, move.State, NetworkConfig.FixedDt); //predict
pendingMoves.Enqueue(move); // These moves have been applied locally, but not yet acked by the server
```
The step is now a **kinematic step against the terrain** — gravity, jumping, and capsule
collide-and-slide — not flat arithmetic. It lives in `Common` so the client predicts with byte-identical
code over byte-identical streamed voxels. See `docs/voxel/COLLISION.md`.

Two consequences for this path:

- **`MoveState` (position, velocity, grounded) is what gets predicted and replayed, not position
  alone.** Replaying from the server's position while keeping local velocity diverges on the first tick
  after every correction and compounds from there, so velocity and grounded ride on
  `PlayerPositionData` too.
- **Prediction suspends while the terrain under the player hasn't arrived.** Unloaded chunks are
  impassable in the shared step (correct for the world edge), which at spawn would wall the player in
  place while the server walks them normally. `LocalPlayer` follows authority instead while
  `TerrainState.FootprintLoaded` is false — but keeps *sending* input, since the server keeps stepping
  it either way.
b. Send moves over the network. We sent `PlayerInputData` which has an `Intent` and a `Sequence`

2. Apply to Server Simulation

In `/Server/GameServer.cs` `OnMessageReceived` we call `world.ApplyInput(e.FromConnection.Id, e.Message.GetSerializable<PlayerInputData>());` we set `LastReceivedSequence` to this sequence, and enqueue our moves
- In `Tick` moves are popped from the queue and fed to `PlayerMovement.Step` against the server's `ChunkMap`, current position is appended to history, and positions are broadcast.
- `PlayerPositionData`: ack of last processed sequence, tick, position, **velocity, grounded**.
  Lag compensation still only needs position, so `SnapshotBuffer` is unchanged.

3. Client Reconciliation
The client's `PlayerRegistry` makes sure that the movement data is applied to the right client. In this case, our sole `LocalPlayer` object.
- `LocalPlayer.Reconcile` is called with a `MoveState` and the `LastProcessed` number.

All messages with a sequence # before `LastProcessed` get dropped from the queue, the predicted position is saved for comparison, the whole `MoveState` is snapped to the server's, and all of the remaining `PendingMoves` are re-stepped from it.

a. Pending Moves Before
[1][2][3][4][5][6][7][8]

b. `PlayerMoveData` with `LastProcessedSequence` = 4 arrives:
[x][x][x][x][5][6][7][8] => [5][6][7][8]

c. Deltas are applied
`Position` stepped by each delta in: [5][6][7][8]

# Remote Players

Because we use rollback for our hitscan detection, we don't use dead-reckoning prediction for remote player visualization. The reason is that dead reckoning is an ILLUSION. It does NOT accurately reflect the state of players on the server. Instead we opt for interpolation with a 3-tick delay. Players are always seeing remote players as they were on the server in the past. This way when a player shoots, the server can see what the player was shooting at at some time in the past.

Steps 1 and 2 are the exact same.

3. Interpolation
The client's `PlayerRegistry` makes sure that the movement data is applied to the right client. In this case, our sole `LocalPlayer` object.
- Snapshot buffer is written to with `remote.Snapshots.Store(data.Tick, data.Position);`
- In `PlayerViewScript.cs`:
```C#
Entity.Transform.Position = remote.Snapshots.GetInterpolated(Registry.RenderTick, remote.Position).ToStride();
```