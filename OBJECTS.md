Step 1 — Common: the component protocol

  New file Common/Components.cs. This one file is the component wire protocol — the enums' numeric values and the if-chain order in ComponentBundle must never be reordered, only appended to
  (same append-only rule as your message-id enums):

  using System.Numerics;
  using Riptide;

  namespace Demiurge
  {
      /// <summary>What kind of thing to build on spawn. Only the view layer interprets
      /// this — the replication plumbing carries it opaquely.</summary>
      public enum ObjectType : ushort
      {
          Crate = 1,
          TrainingDummy,
      }

      /// <summary>One bit per replicated component. Doubles as "what an object HAS"
      /// (spawn) and "what changed" (update). Append-only: these bits are wire protocol.</summary>
      [Flags]
      public enum NetComponents : ushort
      {
          None      = 0,
          Transform = 1 << 0,
          Health    = 1 << 1,
      }

      public struct TransformState : IMessageSerializable
      {
          public Vector3 Position;
          public float Yaw;

          public void Serialize(Message m) { m.AddVector3(Position); m.AddFloat(Yaw); }
          public void Deserialize(Message m) { Position = m.GetVector3(); Yaw = m.GetFloat(); }
      }

      public struct HealthState : IMessageSerializable
      {
          public ushort Current;
          public ushort Max;

          public void Serialize(Message m) { m.AddUShort(Current); m.AddUShort(Max); }
          public void Deserialize(Message m) { Current = m.GetUShort(); Max = m.GetUShort(); }
      }

      /// <summary>Some subset of an object's components, mask-prefixed. The if-chain
      /// order is the wire format; new components go at the end of both methods.</summary>
      public struct ComponentBundle : IMessageSerializable
      {
          public NetComponents Mask;
          public TransformState Transform;
          public HealthState Health;

          public void Serialize(Message m)
          {
              m.AddUShort((ushort)Mask);
              if (Mask.HasFlag(NetComponents.Transform)) m.AddSerializable(Transform);
              if (Mask.HasFlag(NetComponents.Health))    m.AddSerializable(Health);
          }

          public void Deserialize(Message m)
          {
              Mask = (NetComponents)m.GetUShort();
              if (Mask.HasFlag(NetComponents.Transform)) Transform = m.GetSerializable<TransformState>();
              if (Mask.HasFlag(NetComponents.Health))    Health = m.GetSerializable<HealthState>();
          }
      }
  }

  Note the bundle holds all component structs but serializes only the masked ones — senders fill in whatever's handy, the mask decides what hits the wire. That's Listing 5.14's inProperties
  pattern with components as the property groups.

  In Common/NetworkProtocol.cs, append three ids to ServerToClientId — after PlayerStatus, never in the middle (renumbering existing entries would silently break the protocol):

  public enum ServerToClientId : ushort
  {
      Welcome = 1,
      PlayerSpawn,
      PlayerDespawn,
      PlayerPosition,
      PlayerStatus,
      ObjectSpawn,
      ObjectDespawn,
      ObjectState,
  }

  Step 2 — Common: the three delta messages

  These are Chapter 5's RA_Create / RA_Update / RA_Destroy (Listing 5.10), except each replication action gets its own Riptide message id instead of an action byte — Riptide's message
  framing is your ReplicationHeader.

  New file Common/Messages/ObjectSpawnData.cs:

  using Riptide;

  namespace Demiurge
  {
      public struct ObjectSpawnData : IMessageSerializable
      {
          public uint NetworkId;
          public ObjectType Type;
          public ComponentBundle State;   // Mask = everything the object HAS, full state

          public void Serialize(Message message)
          {
              message.AddUInt(NetworkId);
              message.AddUShort((ushort)Type);
              message.AddSerializable(State);
          }

          public void Deserialize(Message message)
          {
              NetworkId = message.GetUInt();
              Type = (ObjectType)message.GetUShort();
              State = message.GetSerializable<ComponentBundle>();
          }
      }
  }

  New file Common/Messages/ObjectStateData.cs:

  using Riptide;

  namespace Demiurge
  {
      public struct ObjectStateData : IMessageSerializable
      {
          public uint NetworkId;
          public uint Tick;               // for snapshot interpolation, same as player positions
          public ComponentBundle State;   // Mask = only what CHANGED

          public void Serialize(Message message)
          {
              message.AddUInt(NetworkId);
              message.AddUInt(Tick);
              message.AddSerializable(State);
          }

          public void Deserialize(Message message)
          {
              NetworkId = message.GetUInt();
              Tick = message.GetUInt();
              State = message.GetSerializable<ComponentBundle>();
          }
      }
  }

  New file Common/Messages/ObjectDespawnData.cs — the book's observation that destruction needs no class id applies: identity only.

  using Riptide;

  namespace Demiurge
  {
      public struct ObjectDespawnData : IMessageSerializable
      {
          public uint NetworkId;

          public void Serialize(Message message) => message.AddUInt(NetworkId);
          public void Deserialize(Message message) => NetworkId = message.GetUInt();
      }
  }

  Step 3 — Server: ServerObject

  New file Server/ServerObject.cs:

  namespace Demiurge.GameServer
  {
      public class ServerObject
      {
          public ushort Id { get; init; }   // unused; players have one, objects use NetworkId
          public uint NetworkId { get; init; }
          public ObjectType Type { get; init; }
          public NetComponents Has { get; init; }   // fixed at spawn

          // Fields, not properties, on purpose: these are mutable structs, and a
          // property getter returns a COPY — `obj.Transform.Position = x` through a
          // property silently mutates the copy and compiles to a no-op warning/error.
          public NetComponents Dirty;
          public TransformState Transform;
          public HealthState Health;
      }
  }

  Actually, drop that Id line — objects deliberately have no Riptide client id, that's the identity separation your TODO called for:

  public class ServerObject
  {
      public uint NetworkId { get; init; }
      public ObjectType Type { get; init; }
      public NetComponents Has { get; init; }

      // Fields, not properties: mutable structs accessed through a property getter
      // return a copy, so `obj.Transform.Position = x` would edit the copy, not the object.
      public NetComponents Dirty;
      public TransformState Transform;
      public HealthState Health;
  }

  That fields-not-properties note is worth adding to your C# idioms glossary — it's the classic mutable-struct trap and it will bite again.

  Step 4 — Server: GameWorld learns to spawn, mutate, and broadcast objects

  Additions to Server/GameWorld.cs. First the fields and demo spawns (add using System.Numerics; is already there):

  private readonly Dictionary<uint, ServerObject> objects = new();
  private uint nextNetworkId = 1;   // 0 is reserved as "no object", per the book's LinkingContext

  private const NetComponents StreamedComponents = NetComponents.Transform;

  // TEMPORARY: demo objects to prove the pipeline; delete once real spawns exist.
  private ServerObject? testCrate, testDummy;

  In the constructor:

  public GameWorld(Server server)
  {
      this.server = server;

      // TEMPORARY demo world content. SendToAll to zero clients is a no-op;
      // connecting clients get these via the AddPlayer catch-up.
      testCrate = SpawnObject(ObjectType.Crate, NetComponents.Transform, new Vector3(3f, 0.5f, 0f));
      testDummy = SpawnObject(ObjectType.TrainingDummy, NetComponents.Transform | NetComponents.Health, new Vector3(-2f, 0f, 2f));
      testDummy.Health = new HealthState { Current = 100, Max = 100 };
      testDummy.Dirty |= NetComponents.Health;
  }

  The spawn/despawn pair — note SpawnObject takes the has mask from the caller, so this method never needs to know what a Crate is:

  public ServerObject SpawnObject(ObjectType type, NetComponents has, Vector3 position)
  {
      var obj = new ServerObject { NetworkId = nextNetworkId++, Type = type, Has = has };
      obj.Transform.Position = position;
      objects[obj.NetworkId] = obj;
      server.SendToAll(CreateObjectSpawnMessage(obj));
      return obj;
  }

  public void DespawnObject(uint networkId)
  {
      if (!objects.Remove(networkId)) return;
      Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectDespawn);
      message.AddSerializable(new ObjectDespawnData { NetworkId = networkId });
      server.SendToAll(message);
  }

  private Message CreateObjectSpawnMessage(ServerObject obj)
  {
      Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectSpawn);
      message.AddSerializable(new ObjectSpawnData {
          NetworkId = obj.NetworkId,
          Type = obj.Type,
          State = new ComponentBundle { Mask = obj.Has, Transform = obj.Transform, Health = obj.Health }
      });
      return message;
  }

  In AddPlayer, right after the existing player catch-up loop — newcomers must learn about existing objects the same way they learn about existing players:

  foreach (var obj in objects.Values)                // catch the newcomer up on objects
      server.Send(CreateObjectSpawnMessage(obj), clientId);

  In Tick, after the player loop and before BroadcastPositions():

  // TEMPORARY demo behaviors: orbiting crate (streamed state), decaying dummy (evented state).
  if (testCrate != null)
  {
      float t = _Tick / (float)NetworkConfig.TickRate;
      testCrate.Transform.Position = new Vector3(3f * MathF.Cos(t), 0.5f, 3f * MathF.Sin(t));
      testCrate.Dirty |= NetComponents.Transform;
  }
  if (testDummy != null && _Tick % (NetworkConfig.TickRate * 3) == 0)
  {
      testDummy.Health.Current = testDummy.Health.Current > 10
          ? (ushort)(testDummy.Health.Current - 10)
          : testDummy.Health.Max;
      testDummy.Dirty |= NetComponents.Health;
  }

  BroadcastObjectStates();

  And the broadcast itself — this is where the streamed/evented channel split lives:

  private void BroadcastObjectStates()
  {
      foreach (var obj in objects.Values)
      {
          if (obj.Dirty == NetComponents.None) continue;

          // Streamed state (transforms): unreliable — the next tick supersedes a loss.
          // Evented state (health): reliable — a lost change might never be re-sent.
          var streamed = obj.Dirty & StreamedComponents;
          var evented  = obj.Dirty & ~StreamedComponents;

          if (streamed != NetComponents.None)
              server.SendToAll(CreateStateMessage(obj, streamed, MessageSendMode.Unreliable));
          if (evented != NetComponents.None)
              server.SendToAll(CreateStateMessage(obj, evented, MessageSendMode.Reliable));

          obj.Dirty = NetComponents.None;
      }
  }

  private Message CreateStateMessage(ServerObject obj, NetComponents mask, MessageSendMode mode)
  {
      Message message = Message.Create(mode, ServerToClientId.ObjectState);
      message.AddSerializable(new ObjectStateData {
          NetworkId = obj.NetworkId,
          Tick = _Tick,
          State = new ComponentBundle { Mask = mask, Transform = obj.Transform, Health = obj.Health }
      });
      return message;
  }

  The whole "how much bandwidth does an idle object cost" question is answered by the first line: Dirty == None → continue. A thousand static crates cost zero bytes per tick. That's the
  book's world-state-delta idea doing its job.

  Step 5 — Client: NetworkManager events

  In Client/Netcode/NetworkManager.cs, three events next to the existing ones:

  public event Action<ObjectSpawnData>? ObjectSpawned;
  public event Action<ObjectDespawnData>? ObjectDespawned;
  public event Action<ObjectStateData>? ObjectStateReceived;

  and three cases in the dispatch switch:

  case ServerToClientId.ObjectSpawn:
      ObjectSpawned?.Invoke(e.Message.GetSerializable<ObjectSpawnData>());
      break;
  case ServerToClientId.ObjectDespawn:
      ObjectDespawned?.Invoke(e.Message.GetSerializable<ObjectDespawnData>());
      break;
  case ServerToClientId.ObjectState:
      ObjectStateReceived?.Invoke(e.Message.GetSerializable<ObjectStateData>());
      break;

  Step 6 — Client sim: SnapshotBuffer, NetObject, ObjectRegistry

  New file Client/Simulation/SnapshotBuffer.cs — this is RemotePlayer's interpolation machinery extracted into a reusable class (leave RemotePlayer alone for now; migrating it onto this is a
  nice later cleanup):

  using System.Numerics;
  using Demiurge;

  public class SnapshotBuffer
  {
      private readonly Queue<(uint Tick, Vector3 Position)> snapshots = new();
      private long newestArrival;

      public uint NewestTick { get; private set; }
      public double SecondsSinceNewest =>
          (System.Diagnostics.Stopwatch.GetTimestamp() - newestArrival) / (double)System.Diagnostics.Stopwatch.Frequency;

      public void Store(uint tick, Vector3 position)
      {
          // Unreliable channel: drop reordered/duplicate packets so tick math never underflows
          if (snapshots.Count > 0 && tick <= NewestTick) return;

          snapshots.Enqueue((tick, position));
          NewestTick = tick;
          newestArrival = System.Diagnostics.Stopwatch.GetTimestamp();

          while (tick - snapshots.Peek().Tick > NetworkConfig.TickRate) // keep 1s of history
              snapshots.Dequeue();
      }

      public Vector3 GetInterpolated(double renderTick, Vector3 fallback)
      {
          if (snapshots.Count == 0) return fallback;

          (uint Tick, Vector3 Position)? previous = null;
          foreach (var snapshot in snapshots)
          {
              if (snapshot.Tick >= renderTick)
              {
                  if (previous is not { } older) return snapshot.Position;
                  double t = (renderTick - older.Tick) / (snapshot.Tick - older.Tick);
                  return Vector3.Lerp(older.Position, snapshot.Position, (float)t);
              }
              previous = snapshot;
          }
          return snapshots.Last().Position;
      }
  }

  New file Client/Simulation/NetObject.cs — the client-side mirror; note the same mutable-struct-fields rule as ServerObject:

  using Demiurge;

  // Sim-layer mirror of a server object. Netcode writes, view reads — same
  // contract as Player. Has says which component fields are meaningful.
  public class NetObject
  {
      public uint NetworkId { get; init; }
      public ObjectType Type { get; init; }
      public NetComponents Has { get; init; }

      public TransformState Transform;   // fields: mutable structs (see ServerObject)
      public HealthState Health;

      public SnapshotBuffer Snapshots { get; } = new();
  }

  New file Client/Simulation/ObjectRegistry.cs — PlayerRegistry's shape, plus the buffer for the one real race (an update racing ahead of its spawn — Riptide's reliable delivery is not
  ordered relative to unreliable, and a reliable health update discarded here would be lost forever):

  using Demiurge;
  using Demiurge.GameClient;

  public class ObjectRegistry
  {
      private readonly Dictionary<uint, NetObject> objects = new();

      // Updates that arrived before their spawn (reliable spawn racing unreliable/
      // reliable state). Applied in arrival order once the spawn lands.
      private readonly Dictionary<uint, Queue<ObjectStateData>> pendingUpdates = new();
      private const int MaxPendingPerObject = 30;

      public event Action<NetObject>? ObjectSpawned;   // sim -> view boundary
      public event Action<NetObject>? ObjectDespawned;

      public ObjectRegistry(NetworkManager network)
      {
          network.ObjectSpawned += OnSpawn;
          network.ObjectDespawned += OnDespawn;
          network.ObjectStateReceived += OnState;
      }

      private void OnSpawn(ObjectSpawnData data)
      {
          if (objects.ContainsKey(data.NetworkId)) return;

          var obj = new NetObject { NetworkId = data.NetworkId, Type = data.Type, Has = data.State.Mask };
          obj.Transform = data.State.Transform;
          obj.Health = data.State.Health;
          objects[data.NetworkId] = obj;

          if (pendingUpdates.Remove(data.NetworkId, out var queued))
              foreach (var update in queued)
                  Apply(obj, update);

          ObjectSpawned?.Invoke(obj);   // after pending applies, so the view builds from newest state
      }

      private void OnDespawn(ObjectDespawnData data)
      {
          pendingUpdates.Remove(data.NetworkId);
          if (!objects.Remove(data.NetworkId, out var obj)) return;
          ObjectDespawned?.Invoke(obj);
      }

      private void OnState(ObjectStateData data)
      {
          if (objects.TryGetValue(data.NetworkId, out var obj))
          {
              Apply(obj, data);
              return;
          }

          // Spawn hasn't arrived yet: hold the update instead of discarding it.
          if (!pendingUpdates.TryGetValue(data.NetworkId, out var queue))
              pendingUpdates[data.NetworkId] = queue = new Queue<ObjectStateData>();
          if (queue.Count >= MaxPendingPerObject) queue.Dequeue();
          queue.Enqueue(data);
      }

      private static void Apply(NetObject obj, ObjectStateData data)
      {
          if (data.State.Mask.HasFlag(NetComponents.Transform))
          {
              obj.Transform = data.State.Transform;
              obj.Snapshots.Store(data.Tick, data.State.Transform.Position);
          }
          if (data.State.Mask.HasFlag(NetComponents.Health))
              obj.Health = data.State.Health;
      }
  }

  Apply is the receiving half of the mask contract: only masked components touch the mirror. Because every component always carries full state (never a diff), applying is idempotent — a
  duplicate or stale-buffered update can't corrupt anything.

  Step 7 — Client view: factory + per-component scripts

  New file Client/View/ObjectViewFactory.cs. The builders dictionary is Chapter 5's object creation registry (Listing 5.7) in eight lines — gameplay registers types, plumbing stays ignorant.
  The per-component entity.Add block below it is where composition pays off visually:

  using Demiurge;
  using Stride.CommunityToolkit.Bepu;
  using Stride.CommunityToolkit.Engine;
  using Stride.CommunityToolkit.Rendering.ProceduralModels;
  using Stride.Engine;

  public class ObjectViewFactory
  {
      private readonly Game game;
      private readonly Scene scene;
      private readonly Dictionary<ObjectType, Func<NetObject, Entity>> builders;

      public ObjectViewFactory(Game game, Scene scene, ObjectRegistry registry)
      {
          this.game = game;
          this.scene = scene;
          builders = new()
          {
              [ObjectType.Crate] = _ => game.Create3DPrimitive(PrimitiveModelType.Cube,
                                            new() { IncludeCollider = false }),
              [ObjectType.TrainingDummy] = _ => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")) },
          };
          registry.ObjectSpawned += CreateView;
          registry.ObjectDespawned += DestroyView;
      }

      private void CreateView(NetObject obj)
      {
          if (!builders.TryGetValue(obj.Type, out var build)) return; // unknown type: skip, don't crash

          var entity = build(obj);
          entity.Name = $"NetObject_{obj.NetworkId}";

          // Attach view behavior per component the object HAS — the mask decides,
          // not the type. A new type with Health gets a health view for free.
          if (obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new NetTransformScript { Object = obj });
          if (obj.Has.HasFlag(NetComponents.Health))    entity.Add(new HealthScaleScript { Object = obj });

          entity.Transform.Position = obj.Transform.Position.ToStride();
          entity.Scene = scene;
      }

      private void DestroyView(NetObject obj)
      {
          if (scene.Entities.FirstOrDefault(e => e.Name == $"NetObject_{obj.NetworkId}") is { } entity)
          {
              scene.Entities.Remove(entity);
              entity.Scene = null;
          }
      }
  }

  New file Client/View/NetObjectScripts.cs — two humble objects, no decisions, render what the sim says:

  using Demiurge;
  using Stride.Core.Mathematics;
  using Stride.Engine;

  // Interpolated transform — the RemotePlayer branch of PlayerViewScript, generalized.
  public class NetTransformScript : SyncScript
  {
      public required NetObject Object { get; init; }

      public override void Update()
      {
          double renderTick = Object.Snapshots.NewestTick
              + Object.Snapshots.SecondsSinceNewest * NetworkConfig.TickRate - 3.0;
          Entity.Transform.Position =
              Object.Snapshots.GetInterpolated(renderTick, Object.Transform.Position).ToStride();
          Entity.Transform.Rotation = Quaternion.RotationY(Object.Transform.Yaw);
      }
  }

  // TEMPORARY health visual: squash the model by health fraction. Stand-in until
  // a real world-space health bar exists; proves the per-component view pipeline.
  public class HealthScaleScript : SyncScript
  {
      public required NetObject Object { get; init; }

      public override void Update()
      {
          float frac = Object.Health.Max == 0 ? 1f : Object.Health.Current / (float)Object.Health.Max;
          Entity.Transform.Scale = new Vector3(1f, 0.3f + 0.7f * frac, 1f);
      }
  }

  Step 8 — Client: composition root

  In Client/Program.cs, next to the existing registry (line ~58):

  var objectRegistry = new ObjectRegistry(network);

  and in Start, next to PlayerViewFactory (line ~192):

  var objectViewFactory = new ObjectViewFactory(game, rootScene, objectRegistry);

  That preserves the composition-root rule: these two lines are the only place the object pipeline gets wired.

  Step 9 — Verify

  1. Basic replication. Rebuild both, run server + client. Expected: a cube orbiting the origin at radius 3, moving smoothly (that's snapshot interpolation over the unreliable stream), and a
  second dummy model near (-2, 0, 2) that visibly shrinks a notch every 3 seconds, then springs back to full height when its health resets (reliable evented updates).
  2. Late-joiner catch-up. Start a second client a minute later. It must immediately show both objects — crate in its current orbit position within one tick, dummy at its current health, not
  full. Current-health-on-join proves catch-up sends live state, not spawn-time state.
  3. The race buffer. Hard to trigger naturally on localhost; verify by inspection or temporarily delay spawn handling. Low priority — the code path is five lines and idempotent.
  4. Idle cost. Comment out the crate's demo motion, watch server bandwidth (or just log sends): a dirty-free object should send nothing. This confirms the delta discipline.

  What to build on top

  - Real spawn/despawn triggers replace the TEMPORARY constructor demo — e.g. a pickup spawner, or press-a-key-to-spawn debug command (that's a client→server message, your first
  ClientToServerId addition since PlayerInput).
  - Damage — when you wire the gun through the server (TODO §3's edge-triggered fire action), the server-side hit code is just obj.Health.Current -= dmg; obj.Dirty |= NetComponents.Health;
  and everything downstream already works. That's the payoff moment for this whole system.
  - New components are a four-place change, all append-only: a bit in NetComponents, a struct, two lines in ComponentBundle, an entity.Add in the factory. If that ever feels like too much
  ceremony per component, that's the moment to graduate to the book's registry-of-readers indirection — not before.
  - Batching (many objects per message, the book's MTU concern) only matters when object counts grow; you'd add a count-prefixed repeat of the ObjectStateData body. Don't do it until
  profiling says so.

  Build it in this order and each step compiles; nothing observable happens until step 8 wires it up, then everything appears at once. Ping me with what breaks — my bet on the likeliest bug:
  forgetting the AddPlayer catch-up loop, which shows up as "works for the first client, second client sees an empty world."