using System.Diagnostics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Owns the server's fixed-timestep loop. The ONLY public type in this assembly — everything
    /// about how the server works stays internal, so the in-process singleplayer path can't grow
    /// into the client reaching into server state and quietly stopping being authoritative.
    ///
    /// Two ways to drive it. The standalone exe calls <see cref="Run"/> and gives it a thread.
    /// Singleplayer calls <see cref="Step"/> once per frame from the client's main thread.
    ///
    /// SINGLEPLAYER MUST NOT USE A SEPARATE THREAD. Riptide pools both Message and PendingMessage in
    /// unsynchronised static Lists — `if (pool.Count > 0) { pool[0]; pool.RemoveAt(0); }` with no lock
    /// — which is safe for one peer on one thread and corrupts immediately with a server and a client
    /// creating messages concurrently. It surfaces as two threads being handed the same Message (a
    /// truncated read on the far end) and as ArgumentOutOfRangeException inside RetrieveFromPool.
    /// Stepping on the client's thread is what makes in-process hosting viable at all.
    /// </summary>
    public sealed class ServerHost : IDisposable
    {
        readonly GameServer server = new();
        readonly Stopwatch clock = new();

        double accumulator;
        double lastTime;
        bool bound;

        /// <summary>
        /// Binds the socket. Throws if the port is taken — synchronously, so the caller can report it
        /// before doing anything else.
        /// </summary>
        public void Start()
        {
            if (bound) throw new InvalidOperationException("Already started.");

            server.Start();
            bound = true;
            clock.Start();
        }

        /// <summary>
        /// Ticks one Step may run before it gives up and discards the backlog.
        ///
        /// Without a cap this loop is a burst amplifier, and it bites hardest exactly when it should
        /// not. Everything a tick SENDS is emitted unpaced within one frame, so a 200 ms frame runs six
        /// ticks and empties six ticks' worth of chunk streaming into the socket back to back. Long
        /// frames happen when the client is meshing hard, i.e. while terrain is streaming in, so loss
        /// causes retransmits, which lengthen frames, which enlarge the next burst. That death spiral
        /// is what disconnected the client with "Poor connection" after 15 failed reliable attempts.
        ///
        /// Discarding the backlog means the simulation runs slow rather than dying. That is the right
        /// trade for a fixed-step server sharing a thread with a renderer.
        /// </summary>
        const int MaxCatchUpTicks = 4;

        /// <summary>
        /// Advances the server by however much real time has passed, in fixed steps. Call once per
        /// frame. The accumulator means a slow or irregular caller still simulates the right number of
        /// ticks, just in bursts — but a throttled caller does slow the server down, which is the price
        /// of sharing its thread.
        /// </summary>
        public void Step()
        {
            if (!bound) return;

            double now = clock.Elapsed.TotalSeconds;
            accumulator += now - lastTime;
            lastTime = now;

            server.PumpNetwork();                            // pump Riptide every call

            int ticks = 0;
            while (accumulator >= NetworkConfig.FixedDt)     // simulate in fixed steps
            {
                if (ticks++ == MaxCatchUpTicks)
                {
                    accumulator = 0;                         // drop the backlog; see MaxCatchUpTicks
                    break;
                }

                server.Tick(NetworkConfig.FixedDt);
                accumulator -= NetworkConfig.FixedDt;
            }
        }

        /// <summary>Binds and then owns the calling thread until <paramref name="stop"/> fires.</summary>
        public void Run(CancellationToken stop)
        {
            Start();

            while (!stop.IsCancellationRequested)
            {
                Step();
                Thread.Sleep(1);
            }

            server.Stop();
        }

        public void Dispose()
        {
            if (bound) server.Stop();
            bound = false;
        }
    }
}
