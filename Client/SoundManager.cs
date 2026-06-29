using System;
using System.Collections.Generic;
using Stride.Audio;

namespace Demiurge
{
    /// <summary>
    /// 2D one-shot sound playback, resolved via <c>Game.Services</c>.
    ///
    /// PLATFORM CAVEAT (proven via a hung-thread dump): on this machine Stride's native
    /// <c>xnAudioSourceStop</c> deadlocks (OpenAL-Soft 1.23 on PipeWire; not backend-
    /// specific). It is reached by ANY of: <c>Stop()</c>, <c>Dispose()</c>, replaying or
    /// reusing an instance, or even reading <c>PlayState</c> — because Stride's PlayState
    /// getter calls <c>Stop()</c> once a voice has finished. A finalizer on a collected
    /// instance would hit it too.
    ///
    /// Therefore the only safe lifecycle here is: create a fresh instance, <c>Play()</c>
    /// once, and hold a reference forever (never stop/reuse/dispose/collect). The cost is
    /// that each play permanently leaks an OpenAL source; once the driver's source limit
    /// (~256) is hit, <c>CreateInstance</c> throws and further plays are silently skipped —
    /// but the game never freezes. This is an engine/platform bug to report upstream.
    ///
    /// On a working OpenAL stack the idiomatic version is a small pool reused via
    /// <c>Play()</c> (and <c>Stop()</c> to restart); that's what to use once the native
    /// deadlock is resolved.
    /// </summary>
    public sealed class SoundManager
    {
        // Held forever on purpose: releasing or finalizing an instance would invoke the
        // deadlocking native Stop(). A bounded leak is the lesser evil vs a hard freeze.
        private readonly List<SoundInstance> _played = new();

        /// <summary>Play a sound once in 2D (non-positional).</summary>
        public void Play(Sound sound)
        {
            try
            {
                var instance = sound.CreateInstance();
                _played.Add(instance);
                instance.Play();
            }
            catch (Exception)
            {
                // Out of OpenAL sources (after the leak hits the driver limit) — skip
                // rather than crash. Sound stops; gameplay continues.
            }
        }
    }
}
