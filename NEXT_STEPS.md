# NEXT_STEPS.md — finishing server-side rewind: the test rig + HitConfirm

State of play: rewind itself is DONE. The shared interpolation clock
(`PlayerRegistry.RenderTick`), the `RenderTick` stamp on `PlayerFireData`,
`SnapshotBuffer` in Common, and the server's history + rewound raycast
(`ServerPlayer.History`, the gate in `ApplyFire`, `Raycast` sampling
`History.GetInterpolated`) are all in the codebase. What's left:

1. **Part 1 — fake latency/jitter.** Rewind has never actually been *observed*
   working — on localhost the rewind window is ~3 ticks and everything hits
   regardless. This is the book's Chapter 7 test rig ("Simulating Real-World
   Conditions", p. 228), and its verify section doubles as the rewind verify.
2. **Part 2 — HitConfirm.** The shooter's only feedback today is watching the
   victim's HP; this closes the loop.

One loose end from the rewind implementation: `ApplyFire`'s gate never checks
`float.IsFinite(fire.RenderTick)`. NaN compares false against both bounds, so it
slips through — harmlessly (NaN render ticks make `GetInterpolated` fall back to
the newest snapshot, i.e. no rewind), but the other float inputs all get the
finite check, so add it to the gate for consistency:

```csharp
if (!float.IsFinite(fire.RenderTick)) return;
```

## Part 1 — Fake latency and jitter (the test rig)

The book's Chapter 7 recipe: hold each incoming packet until `now + latency ±
jitter`, then process it. Two constants in `NetworkConfig` switch it on — no tc/
netem, no root, works the same on any machine.

Where it hooks in: `NetworkManager.OnMessageReceived` is the single choke point
where every server message becomes a typed event. Two constraints shape the
implementation:

- **Deserialize immediately, delay the delivery.** Riptide recycles the `Message`
  object the moment the handler returns, so the queue must hold the decoded struct
  (captured in a closure), never the `Message` itself.
- **Delay only — never drop.** By the time we see a reliable message, Riptide's
  transport has already acked it. A "simulated drop" here would be a permanent
  loss the real network can't produce. (Real unreliable loss is still worth
  testing occasionally — that's what netem is for.)

Jitter makes deliveries reorder (the priority queue dequeues by due time, not
arrival) — that's a feature: it stress-tests exactly the reordering tolerance the
codebase claims (`SnapshotBuffer`'s stale-tick dedup, `ObjectRegistry`'s pending
queue, the owned-object backfill in Program.cs).

### 1a. The knobs — `Common/NetworkProtocol.cs`

Add to `NetworkConfig` (`static readonly`, not `const`, so `if (… <= 0f)` doesn't
trip unreachable-code warnings):

```csharp
// ---- test rig: fake network conditions, client inbound only ----
// Non-zero latency holds every received message for latency ± jitter before
// it reaches the sim. Inbound-only is enough for rewind testing: what rewind
// compensates is the STALENESS OF THE CLIENT'S VIEW, which is inbound delay
// + interpolation delay. Ship with both at 0.
public static readonly float SimulatedLatencySeconds = 0f;   // try 0.08f
public static readonly float SimulatedJitterSeconds = 0f;    // ± amplitude; try 0.02f
```

### 1b. The queue — `Client/Netcode/NetworkManager.cs`

New members:

```csharp
// Fake-latency rig (see NetworkConfig.SimulatedLatencySeconds). Holds decoded
// messages as ready-to-fire closures until their simulated arrival time.
private readonly PriorityQueue<Action, double> delayed = new();
private readonly Random rng = new();
private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

private void Dispatch(Action deliver)
{
    if (NetworkConfig.SimulatedLatencySeconds <= 0f) { deliver(); return; }
    double due = Now + NetworkConfig.SimulatedLatencySeconds
        + (rng.NextDouble() * 2.0 - 1.0) * NetworkConfig.SimulatedJitterSeconds;
    delayed.Enqueue(deliver, due);
}
```

`Update` drains what's due after pumping the socket:

```csharp
public void Update()
{
    client.Update();
    while (delayed.TryPeek(out _, out double due) && due <= Now)
        delayed.Dequeue().Invoke();
}
```

And every case in `OnMessageReceived` becomes deserialize-then-`Dispatch` (each
case needs its own variable name — switch sections share one scope):

```csharp
private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
{
    // Decode NOW (Riptide reuses the Message after this returns), deliver
    // through Dispatch — immediately, or late when fake latency is on.
    switch ((ServerToClientId)e.MessageId)
    {
        case ServerToClientId.Welcome:
            var welcome = e.Message.GetSerializable<WelcomeData>();
            Dispatch(() => ClientId = welcome.ClientId);
            break;
        case ServerToClientId.PlayerSpawn:
            var spawn = e.Message.GetSerializable<PlayerSpawnData>();
            Dispatch(() => PlayerSpawned?.Invoke(spawn));
            break;
        case ServerToClientId.PlayerDespawn:
            var despawn = e.Message.GetSerializable<PlayerDespawnData>();
            Dispatch(() => PlayerDespawned?.Invoke(despawn));
            break;
        case ServerToClientId.PlayerPosition:
            var position = e.Message.GetSerializable<PlayerPositionData>();
            Dispatch(() => PlayerPositionReceived?.Invoke(position));
            break;
        case ServerToClientId.ObjectSpawn:
            var objSpawn = e.Message.GetSerializable<ObjectSpawnData>();
            Dispatch(() => ObjectSpawned?.Invoke(objSpawn));
            break;
        case ServerToClientId.ObjectDespawn:
            var objDespawn = e.Message.GetSerializable<ObjectDespawnData>();
            Dispatch(() => ObjectDespawned?.Invoke(objDespawn));
            break;
        case ServerToClientId.ObjectState:
            var objState = e.Message.GetSerializable<ObjectStateData>();
            Dispatch(() => ObjectStateReceived?.Invoke(objState));
            break;
        case ServerToClientId.PlayerFired:
            var fired = e.Message.GetSerializable<PlayerFiredData>();
            Dispatch(() => PlayerFired?.Invoke(fired));
            break;
    }
}
```

A nice free property: the interpolation clock stamps `newestArrival` when
`OnPlayerPosition` *runs* — i.e., on delayed delivery — so the fake latency flows
into `RenderTick` exactly like real latency would. Nothing else needs to know the
rig exists.

**Verify Part 1 — this is also the rewind verify.** Two clients, both windows
focused/visible (an unfocused Stride window throttles updates and the stutter
reads like a netcode bug).

1. **Constants at 0 first**: everything behaves exactly as before (the `Dispatch`
   fast path never queues) — full health smoke test: HP 100 at spawn, 10-damage
   steps, respawn at origin, late joiner sees correct health.
2. **Latency `0.08f`, jitter `0.02f`, rebuild**: your own movement stays crisp
   (prediction), remote players and object changes (pickups, HP) lag ~80ms but
   stay smooth, reconciliation corrections stay quiet.
3. **The money shot**: client B strafes continuously; client A puts the crosshair
   dead on B's rendered model and fires. Hits must register (B's HP drops). For
   contrast, aim where B "actually is" (ahead of the model) — that *misses*.
   Without rewind the opposite would be true; that flip is rewind working.
4. **Respawn ghost**: kill B, and as B teleports keep shooting the death spot —
   HP must not drop on respawned-B (that's the respawn `History.Clear()`).
5. Leave the constants on for Part 2's verify, then ship them back at `0f`.

## Part 2 — HitConfirm: the shooter learns their shot landed

Follows the server→client message recipe exactly (append the enum, the struct, a
NetworkManager event + case, subscribe in the composition root — never in a view
script). The payload rides to the shooter only, and it's **unreliable**: a
hitmarker is cosmetic, and a lost one costs nothing.

### 2a. Common

`Common/NetworkProtocol.cs` — append to `ServerToClientId` (append-only, as
always):

```csharp
PlayerFired,
HitConfirm
```

New file `Common/Messages/HitConfirmData.cs`:

```csharp
using Riptide;

namespace Demiurge
{
    public struct HitConfirmData : IMessageSerializable
    {
        public uint TargetNetworkId;   // what was hit (status object for players)
        public ushort Damage;

        public void Serialize(Message message)
        {
            message.AddUInt(TargetNetworkId);
            message.AddUShort(Damage);
        }

        public void Deserialize(Message message)
        {
            TargetNetworkId = message.GetUInt();
            Damage = message.GetUShort();
        }
    }
}
```

(A world-space impact point for hit FX would be the natural next field — it needs
`Raycast` to also return the hit distance `t`, so it's left for when FX exist.)

### 2b. Server — `Server/WeaponSystem.cs`

In `ApplyFire`, inside the existing damage branch (right after `hit.Dirty |=
NetComponents.Health;`):

```csharp
// Tell the shooter it landed. Unreliable + shooter-only: cosmetic feedback,
// the victim's replicated Health remains the truth.
Message confirm = Message.Create(MessageSendMode.Unreliable, ServerToClientId.HitConfirm);
confirm.AddSerializable(new HitConfirmData { TargetNetworkId = hit.NetworkId, Damage = stats.Damage });
server.Send(confirm, player.Id);
```

### 2c. Client netcode — `Client/Netcode/NetworkManager.cs`

The event, next to `PlayerFired`:

```csharp
public event Action<HitConfirmData>? HitConfirmed;   // cosmetic: your shot landed
```

The dispatch case, through the Part 1 rig like everything else:

```csharp
case ServerToClientId.HitConfirm:
    var confirm = e.Message.GetSerializable<HitConfirmData>();
    Dispatch(() => HitConfirmed?.Invoke(confirm));
    break;
```

### 2d. Client sim — `Client/Simulation/Player.cs`

`LocalPlayer` gets a counter in the poll-and-diff style the HUD already reads
(netcode writes, view reads — no event across the boundary needed):

```csharp
// Count of server-confirmed hits. Netcode writes (via the composition root),
// the HUD polls and flashes a marker when it changes.
public uint HitConfirms { get; private set; }
public void ConfirmHit() => HitConfirms++;
```

`Client/Program.cs`, with the other bridges in the composition root:

```csharp
network.HitConfirmed += _ => registry.LocalPlayer?.ConfirmHit();
```

### 2e. Client view — `Client/View/HUD.cs`

In `CreateUI`, a marker that lives collapsed at the end of the panel:

```csharp
var hitText = new TextBlock
{
    Text = "HIT",
    TextColor = Color.Red,
    Font = font,
    TextSize = 24,
    VerticalAlignment = VerticalAlignment.Center,
    Margin = new Thickness(12, 0, 0, 0),
    Visibility = Visibility.Collapsed,
};
```

add it to the panel after `ammoText`:

```csharp
ammoPanel.Children.Add(hitText);
```

hand it to the script:

```csharp
new HudScript {
    canvas = canvas,
    AmmoText = ammoText,
    HealthText = healthText,
    HitText = hitText },
```

and `HudScript` gains the flash logic:

```csharp
public TextBlock HitText { get; set; } = null!;

private uint _lastHitConfirms;
private float _hitFlashLeft;   // seconds the marker stays visible
```

at the end of `Update`, after the ammo block:

```csharp
if (local.HitConfirms != _lastHitConfirms)
{
    _lastHitConfirms = local.HitConfirms;
    _hitFlashLeft = 0.15f;
    HitText.Visibility = Visibility.Visible;
}
else if (_hitFlashLeft > 0f)
{
    _hitFlashLeft -= (float)Game.UpdateTime.Elapsed.TotalSeconds;
    if (_hitFlashLeft <= 0f) HitText.Visibility = Visibility.Collapsed;
}
```

(Moving the marker onto the reticle — `CursorReticleScript` — and adding a sound
through `SoundManager` are pure polish on the same counter; nothing else changes.)

**Verify Part 2:** two clients, latency constants on. (1) A shoots B: "HIT"
flashes on A's HUD one simulated-latency later, in step with B's HP dropping on
A's screen — that's the full round trip A → PlayerFire → rewound raycast →
HitConfirm → A. (2) Shooting the training dummy also flashes (any Health hit
confirms). (3) Missed shots and shots blocked by a healthless pickup do NOT flash.
(4) B shooting sees flashes on B's HUD only — the message is shooter-only.

## The tradeoff you signed up for (book p. 249)

Rewind moves the injustice from the shooter to the victim: a laggy shooter's view
is old, so a victim who just ducked behind cover can still be hit "around the
corner" — by up to `MaxRewindTicks` of movement. That constant is the tuning knob:
lower it and high-ping shooters start missing; raise it and corner deaths get
worse. One second is a generous starting point; competitive shooters often cap
compensation nearer 200–250ms.

Natural follow-up, in the old recipe style: a debug toggle that draws the server's
rewound hit-spheres so you can *see* the rewind instead of inferring it from HP
drops — and a hit `Point` on `HitConfirmData` once there are impact FX to place.

*(The Part 1–5 consolidation doc this file once was: `git show 6f66e30:NEXT_STEPS.md`.)*
