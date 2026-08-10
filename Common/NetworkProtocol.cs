using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{

    public static class NetworkConfig
    {
        public const ushort Port = 7777;
        public const int ProtocolVersion = 2;

        /// <summary>
        /// Who to connect to. Here rather than at the call site because the client now opens TWO
        /// connections — Riptide for gameplay and <see cref="ChunkTransport"/> for terrain — and they
        /// must never disagree about where the server is.
        /// </summary>
        public const string ServerHost = "127.0.0.1";

        /// <summary>Server simulation ticks per second. Everything tick-related —
        /// the server's fixed timestep, snapshot history windows, renderTick math —
        /// must derive from this so client and server can't drift apart.</summary>
        public const int TickRate = 30;
        public const float FixedDt = 1f / TickRate;

        /// <summary>
        /// How far behind the newest snapshot remote players are rendered.
        /// Ther server's rewind gate needs this number as well.
        /// </summary>
        public const int InterpolationDelayTicks = 3;

        /// <summary>
        /// Oldest client view the server will rewind to when validating a shot.
        /// Matches snapshot buffer's 1s of retention.
        /// </summary>
        public const int MaxRewindTicks = TickRate;

        // --- Fake network conditions ---
        // Non-zero latency holds every message for latency +- jitter before it reaches the far end.
        // The in-process transport applies these in BOTH directions; the Riptide path applies them on
        // client inbound only, as it always has.
        public static readonly float SimulatedLatencySeconds = 0f;
        public static readonly float SimulatedJitterSeconds = 0f;
    }

    /// <summary>
    /// How badly the in-process transport is allowed to misbehave.
    /// </summary>
    /// <remarks>
    /// The in-process transport exists so singleplayer can run without sharing Riptide's unsynchronised
    /// static pools. But a plain queue delivers in order, unserialised and unbounded, which would make
    /// singleplayer PASS where a real network FAILS — silently, and only discovered against a real
    /// server. So the fake does not imitate Riptide, it <b>dominates</b> it:
    /// <code>
    /// green in singleplayer  =>  green in multiplayer
    /// </code>
    /// which holds when the set of delivery orderings the fake can produce is a SUPERSET of Riptide's.
    /// Divergences that make the fake stricter are features; divergences that make it more permissive
    /// are bugs.
    /// <para>
    /// The second half of the property is equally load-bearing and easier to forget: that set must also
    /// be a SUBSET of what is physically realizable. A fake that reorders further than Riptide's own
    /// deduplication window tolerates manufactures failures no real network can produce, and debugging
    /// those is pure waste. Hostility is capped at the real ceiling, never past it.
    /// </para>
    /// </remarks>
    public static class TransportHostility
    {
        /// <summary>
        /// Furthest a message may be displaced from its send position.
        /// </summary>
        /// <remarks>
        /// 64 because that is where Riptide's own <c>ReliableSequencer</c> logs "the gap between received
        /// sequence IDs was very large". Past it we would be inventing scenarios rather than reproducing
        /// them.
        /// </remarks>
        public const int ReorderWindow = 64;

        /// <summary>High for a LAN, ordinary for a bad connection.</summary>
        public const double UnreliableDropRate = 0.02;

        /// <summary>
        /// Rare in practice — and rare is exactly why it stays unfound without deliberate injection.
        /// </summary>
        /// <remarks>
        /// Riptide's unreliable channel assigns no sequence id, so nothing dedups it and a duplicated
        /// datagram reaches the handler. <c>PlayerInput</c> is unreliable, so a duplicate double-applies
        /// movement if the input queue applies every arrival.
        /// </remarks>
        public const double UnreliableDuplicateRate = 0.01;

        /// <summary>Reliable is guaranteed by Riptide, so dropping it would be MORE permissive than the
        /// real transport rather than stricter — the wrong direction, and it would break the guarantee.</summary>
        public const double ReliableDropRate = 0.0;

        /// <summary>
        /// Delivery decisions retained for diagnosis.
        /// </summary>
        /// <remarks>
        /// Required, not a nicety. A seed alone does not reproduce a live session: it makes the delivery
        /// POLICY deterministic, not the traffic, because the message sequence depends on frame-to-frame
        /// input timing. Seed plus decision log is what makes a glitch diagnosable.
        /// </remarks>
        public const int DeliveryLogCapacity = 256;
    }

    // One enum per direction. The ushort value IS the wire protocol —
    // if client and server disagree on these numbers, handlers silently never fire.
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
        PlayerFired,
        HitConfirm,
        ChunkSlabs,
        // Appended, never inserted — these values ARE the protocol.
        TerrainEdit,
        CommandResult,
        ActivityFeed,
        MatchTickets,
        Scoreboard
    }

    public enum ClientToServerId : ushort
    {
        PlayerInput = 1,
        PlayerFire,
        PlayerReload,
        PlayerInteract,
        // Appended, never inserted — these values ARE the protocol.
        PlayerDig,
        CommandRequest,
        /// <summary>F: use the emplaced thing in reach. Distinct from PlayerInteract (E, which
        /// changes what is in your hands) because "operate this" and "pick this up" are different
        /// intentions about the same object, and one key doing both is what the press/hold split was
        /// trying and failing to express.</summary>
        PlayerUse,
        /// <summary>Drop a bomb on a point. Only legal while operating an emplaced mortar.</summary>
        MortarFire
    }

    public static class MessageExtensions
    {
        #region Vector2
        /// <inheritdoc cref="AddVector2(Message, Vector2)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddVector2(Message, Vector2)"/>.</remarks>
        public static Message Add(this Message message, Vector2 value) => AddVector2(message, value);

        /// <summary>Adds a <see cref="Vector2"/> to the message.</summary>
        /// <param name="value">The <see cref="Vector2"/> to add.</param>
        /// <returns>The message that the <see cref="Vector2"/> was added to.</returns>
        public static Message AddVector2(this Message message, Vector2 value)
        {
            return message.AddFloat(value.X).AddFloat(value.Y);
        }

        /// <summary>Retrieves a <see cref="Vector2"/> from the message.</summary>
        /// <returns>The <see cref="Vector2"/> that was retrieved.</returns>
        public static Vector2 GetVector2(this Message message)
        {
            return new Vector2(message.GetFloat(), message.GetFloat());
        }
        #endregion

        #region Vector3
        /// <inheritdoc cref="AddVector3(Message, Vector3)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddVector3(Message, Vector3)"/>.</remarks>
        public static Message Add(this Message message, Vector3 value) => AddVector3(message, value);

        /// <summary>Adds a <see cref="Vector3"/> to the message.</summary>
        /// <param name="value">The <see cref="Vector3"/> to add.</param>
        /// <returns>The message that the <see cref="Vector3"/> was added to.</returns>
        public static Message AddVector3(this Message message, Vector3 value)
        {
            return message.AddFloat(value.X).AddFloat(value.Y).AddFloat(value.Z);
        }

        /// <summary>Retrieves a <see cref="Vector3"/> from the message.</summary>
        /// <returns>The <see cref="Vector3"/> that was retrieved.</returns>
        public static Vector3 GetVector3(this Message message)
        {
            return new Vector3(message.GetFloat(), message.GetFloat(), message.GetFloat());
        }
        #endregion

        #region Quaternion
        /// <inheritdoc cref="AddQuaternion(Message, Quaternion)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddQuaternion(Message, Quaternion)"/>.</remarks>
        public static Message Add(this Message message, Quaternion value) => AddQuaternion(message, value);

        /// <summary>Adds a <see cref="Quaternion"/> to the message.</summary>
        /// <param name="value">The <see cref="Quaternion"/> to add.</param>
        /// <returns>The message that the <see cref="Quaternion"/> was added to.</returns>
        public static Message AddQuaternion(this Message message, Quaternion value)
        {
            return message.AddFloat(value.X).AddFloat(value.Y).AddFloat(value.Z).AddFloat(value.W);
        }

        /// <summary>Retrieves a <see cref="Quaternion"/> from the message.</summary>
        /// <returns>The <see cref="Quaternion"/> that was retrieved.</returns>
        public static Quaternion GetQuaternion(this Message message)
        {
            return new Quaternion(message.GetFloat(), message.GetFloat(), message.GetFloat(), message.GetFloat());
        }
        #endregion
    }
}
