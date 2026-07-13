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
Position = PlayerMovement.Step(Position, move.Intent, move.State, NetworkConfig.FixedDt); //predict
pendingMoves.Enqueue(move); // These moves have been applied locally, but not yet acked by the server
```
b. Send moves over the network. We sent `PlayerInputData` which has an `Intent` and a `Sequence`

2. Apply to Server Simulation

In `/Server/GameServer.cs` `OnMessageReceived` we call `world.ApplyInput(e.FromConnection.Id, e.Message.GetSerializable<PlayerInputData>());` we set `LastReceivedSequence` to this sequence, and enqueue our moves
- In `Tick` moves are popped from the queue, applied to the position, current position is appended to history, and positions are broadcast.
- `PlayerPositionData`, ack of last processed in sqeuence, tick, position

3. Client Reconciliation
The client's `PlayerRegistry` makes sure that the movement data is applied to the right client. In this case, our sole `LocalPlayer` object.
- `LocalPlayer.Reconcile` is called with a position and the `LastProcessed` number.

All messages with a sequence # before `LastProcessed` get dropped from the queue, Position snapshot is saved, and all of the remaining `PendingMoves` deltas are applied to the snapshot.

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