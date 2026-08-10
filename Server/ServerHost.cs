using System.Diagnostics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Owns the server's fixed-timestep loop. The ONLY public type in this assembly — everything
    /// about how the server works stays internal, so the in-process singleplayer path can't grow
    /// into the client reaching into server state and quietly stopping being authoritative.
    ///
    /// Three ways to drive it. The standalone exe calls <see cref="Run"/> and gives it the calling
    /// thread. Singleplayer calls <see cref="StartOnOwnThread"/>. <see cref="Step"/> remains public for
    /// tests and for any caller that wants to pump it manually.
    /// </summary>
    /// <remarks>
    /// Singleplayer used to be pinned to the client's thread, and the reason is worth keeping written
    /// down because it constrained the design for a long time. Riptide pools both <c>Message</c> and
    /// <c>PendingMessage</c> in unsynchronised static Lists —
    /// <c>if (pool.Count > 0) { pool[0]; pool.RemoveAt(0); }</c> with no lock — which is safe for one
    /// peer on one thread and corrupts immediately with a server and a client creating messages
    /// concurrently. It surfaced as two threads being handed the same Message (a truncated read on the
    /// far end) and as <c>ArgumentOutOfRangeException</c> inside <c>RetrieveFromPool</c>.
    /// <para>
    /// That constraint is gone because singleplayer no longer uses Riptide at all: it runs on
    /// <c>InProcessNetwork</c>, whose queues are locked and whose <c>Message</c> pool is
    /// <c>[ThreadStatic]</c>. A REMOTE client still uses Riptide, but then the two peers are in
    /// different processes and never share a pool. Do not reintroduce a configuration where a Riptide
    /// server and a Riptide client run on separate threads of one process.
    /// </para>
    /// </remarks>
    public sealed class ServerHost : IDisposable
    {
        GameServer server;
        ServerOptions options;
        readonly Stopwatch clock = new();

        double accumulator;
        double lastTime;
        bool bound;

        Thread? thread;
        CancellationTokenSource? threadStop;

        public ServerHost(bool allowCheats = false)
            : this(new ServerOptions { AllowCheats = allowCheats })
        {
        }

        public ServerHost(ServerOptions options)
        {
            this.options = options;
            server = new GameServer(options);
        }

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
        /// Discarding the backlog means the simulation runs slow rather than dying.
        /// </summary>
        /// <remarks>
        /// Re-read when the server moved to its own thread, because the original rationale was about a
        /// slow CALLER and there is no longer a caller. The cap still earns its place, but for a
        /// different reason: it now bounds the server against ITSELF. When a tick costs more than
        /// <see cref="NetworkConfig.FixedDt"/> — which it currently does, around 50 ms against a 33 ms
        /// budget — the accumulator grows on every pass and this is what stops it running an unbounded
        /// catch-up burst and emitting a matching burst of traffic. The observed effect is the
        /// simulation settling near 1/tickCost ticks per second with the backlog dropped, which is the
        /// intended degradation.
        /// </remarks>
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

        /// <summary>
        /// Binds, then runs the fixed-step loop on a dedicated background thread.
        /// </summary>
        /// <remarks>
        /// The bind happens SYNCHRONOUSLY on the calling thread, before the loop starts, so a taken port
        /// still throws to the caller rather than disappearing into a background thread. Singleplayer
        /// refusing to start beats it silently attaching to a stale server.
        /// <para>
        /// This is what stops the client frame paying for the server tick. Before it, one frame ran up
        /// to <see cref="MaxCatchUpTicks"/> ticks inline: a 50 ms tick became a 200 ms frame, measured
        /// as 5 fps with the client's own work totalling 2 ms of it.
        /// </para>
        /// </remarks>
        public void StartOnOwnThread()
        {
            Start();

            threadStop = new CancellationTokenSource();
            thread = new Thread(() => Loop(threadStop.Token))
            {
                IsBackground = true,
                Name = "ServerTick",
            };
            thread.Start();
        }

        private void Loop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                Step();
                Thread.Sleep(1);
            }
        }

        private void StopThread()
        {
            if (thread is null) return;

            threadStop?.Cancel();
            // Bounded: a tick that has wedged must not hang client shutdown. Two seconds is several
            // times the worst tick ever measured.
            thread.Join(TimeSpan.FromSeconds(2));
            thread = null;
            threadStop?.Dispose();
            threadStop = null;
        }

        /// <summary>Binds and then owns the calling thread until <paramref name="stop"/> fires.</summary>
        public void Run(CancellationToken stop)
        {
            Start();
            using var console = new DedicatedServerConsole();
            console.Start();
            Console.WriteLine("[Server] Console ready. Type 'help' for commands.");

            bool stopping = false;
            while (!stop.IsCancellationRequested && !stopping)
            {
                var action = console.Drain(server);
                switch (action.Kind)
                {
                    case DedicatedConsoleActionKind.Stop:
                        stopping = true;
                        continue;
                    case DedicatedConsoleActionKind.LoadMap:
                        RotateMap(action.MapPath!);
                        break;
                }
                Step();
                Thread.Sleep(1);
            }

            server.Stop();
            bound = false;
        }

        public void Dispose()
        {
            // Stop the loop before stopping the server, or the last tick races the teardown.
            StopThread();
            if (bound) server.Stop();
            bound = false;
        }

        private void RotateMap(string mapPath)
        {
            // Parse and validate before touching the active world.
            _ = RuntimeMapSerializer.Load(mapPath);

            server.Stop();
            options = options with { MapPath = mapPath };
            server = new GameServer(options);
            server.Start();
            accumulator = 0;
            lastTime = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"[Server] Loaded map {mapPath}");
        }
    }
}
