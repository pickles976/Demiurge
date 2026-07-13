using System.Numerics;
using Riptide;

namespace Demiurge
{

    public static class NetworkConfig
    {
        public const ushort Port = 7777;

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

        // --- Fake network conditions, client inbound only ---
        // Non-zero latency holds every received message for latency +- jitter before it reaches the sim.
        public static readonly float SimulatedLatencySeconds = 0f;
        public static readonly float SimulatedJitterSeconds = 0f; 
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
        PlayerFired
    }

    public enum ClientToServerId : ushort
    {
        PlayerInput = 1,
        PlayerFire,
        PlayerReload
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