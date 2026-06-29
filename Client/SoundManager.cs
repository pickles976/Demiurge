using System.Text;
using Silk.NET.OpenAL;
using Stride.Engine;
using SVector3 = Stride.Core.Mathematics.Vector3;

namespace Demiurge
{
    /// <summary>Handle to a continuous (looping) sound, used to stop or adjust it later.</summary>
    public readonly struct SoundHandle
    {
        internal readonly uint Source;
        internal SoundHandle(uint source) => Source = source;
    }

    /// <summary>
    /// Audio via OpenAL directly (Silk.NET.OpenAL), bypassing Stride's audio — Stride's
    /// native layer deadlocks on this platform (see AUDIO.md). OpenAL itself is fine.
    ///
    /// API:
    ///   PlayOneShot / PlayOneShotSpatial      — fire-and-forget SFX (2D / positioned)
    ///   PlayContinuous / PlayContinuousSpatial — looping sounds; return a <see cref="SoundHandle"/>
    ///   StopContinuous(handle)                 — stop a looping sound
    ///   SetPosition(handle, ...)               — move a live looping spatial sound
    /// All play methods take an optional volume (gain). For pitch/speed variants, use
    /// separate pre-rendered clips (OpenAL's only rate control couples pitch and speed).
    ///
    /// Loads 16-bit PCM WAV; mono spatializes, stereo plays un-positioned (OpenAL rule).
    /// </summary>
    public sealed unsafe class SoundManager : IDisposable
    {
        private readonly AL _al;
        private readonly ALContext _alc;
        private readonly Device* _device;
        private readonly Context* _context;
        private readonly Entity? _listener;            // camera; drives the 3D listener pose

        private readonly Dictionary<string, uint> _buffers = new();
        private readonly uint[] _oneShots;             // round-robin pool for fire-and-forget
        private int _next;
        private readonly HashSet<uint> _continuous = new(); // dedicated sources, freed on stop

        /// <summary>World distance at which a spatial sound is at full volume; falloff scales from here.</summary>
        public float SpatialReferenceDistance = 10f;
        /// <summary>How quickly spatial sounds attenuate past the reference distance.</summary>
        public float SpatialRolloffFactor = 1f;

        public SoundManager(Entity? listener = null, int voices = 32)
        {
            _listener = listener;
            _alc = ALContext.GetApi(soft: false);
            _al = AL.GetApi(soft: false);

            _device = _alc.OpenDevice("");
            if (_device == null)
                throw new InvalidOperationException("OpenAL: failed to open the default audio device.");
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);

            _oneShots = new uint[voices];
            for (int i = 0; i < voices; i++)
                _oneShots[i] = _al.GenSource();
        }

        // ---- Fire-and-forget ----

        public void PlayOneShot(string wavPath, float volume = 1f)
            => OneShot(wavPath, null, volume);

        public void PlayOneShotSpatial(string wavPath, SVector3 position, float volume = 1f)
            => OneShot(wavPath, position, volume);

        private void OneShot(string wavPath, SVector3? pos, float volume)
        {
            EnsureContext();
            uint source = _oneShots[_next];
            _next = (_next + 1) % _oneShots.Length;
            Configure(source, GetBuffer(wavPath), pos, volume, looping: false);
            _al.SourcePlay(source);
        }

        // ---- Continuous (looping) ----

        public SoundHandle PlayContinuous(string wavPath, float volume = 1f)
            => Continuous(wavPath, null, volume);

        public SoundHandle PlayContinuousSpatial(string wavPath, SVector3 position, float volume = 1f)
            => Continuous(wavPath, position, volume);

        private SoundHandle Continuous(string wavPath, SVector3? pos, float volume)
        {
            EnsureContext();
            uint source = _al.GenSource();
            _continuous.Add(source);
            Configure(source, GetBuffer(wavPath), pos, volume, looping: true);
            _al.SourcePlay(source);
            return new SoundHandle(source);
        }

        public void StopContinuous(SoundHandle handle)
        {
            if (!_continuous.Remove(handle.Source)) return; // already stopped / invalid
            EnsureContext();
            _al.SourceStop(handle.Source);
            _al.DeleteSource(handle.Source);
        }

        /// <summary>Move a live looping spatial sound (e.g. an engine that's moving).</summary>
        public void SetPosition(SoundHandle handle, SVector3 position)
        {
            if (!_continuous.Contains(handle.Source)) return;
            EnsureContext();
            _al.SetSourceProperty(handle.Source, SourceVector3.Position, position.X, position.Y, position.Z);
        }

        // ---- internals ----

        private void Configure(uint source, uint buffer, SVector3? worldPos, float volume, bool looping)
        {
            _al.SourceStop(source); // lets us (re)assign the buffer; restarts a recycled voice
            _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
            _al.SetSourceProperty(source, SourceFloat.Gain, volume);
            _al.SetSourceProperty(source, SourceBoolean.Looping, looping);

            if (worldPos is { } p)
            {
                UpdateListener();
                _al.SetSourceProperty(source, SourceBoolean.SourceRelative, false);
                _al.SetSourceProperty(source, SourceFloat.ReferenceDistance, SpatialReferenceDistance);
                _al.SetSourceProperty(source, SourceFloat.RolloffFactor, SpatialRolloffFactor);
                _al.SetSourceProperty(source, SourceVector3.Position, p.X, p.Y, p.Z);
            }
            else
            {
                // Relative to a listener pinned at the origin => centered, no attenuation.
                _al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
                _al.SetSourceProperty(source, SourceVector3.Position, 0f, 0f, 0f);
            }
        }

        // OpenAL's "current context" is process-global and Stride shares the library, so
        // make ours current before issuing calls.
        private void EnsureContext() => _alc.MakeContextCurrent(_context);

        // Sync the OpenAL listener to the camera's world pose.
        private void UpdateListener()
        {
            if (_listener == null) return;

            var t = _listener.Transform;
            t.UpdateWorldMatrix();
            var world = t.WorldMatrix;

            var pos = world.TranslationVector;
            _al.SetListenerProperty(ListenerVector3.Position, pos.X, pos.Y, pos.Z);

            // OpenAL orientation = forward ("at") + up vectors. Stride cameras look down -Z.
            var fwd = SVector3.TransformNormal(-SVector3.UnitZ, world);
            var up = SVector3.TransformNormal(SVector3.UnitY, world);
            fwd.Normalize();
            up.Normalize();
            float* orient = stackalloc float[6] { fwd.X, fwd.Y, fwd.Z, up.X, up.Y, up.Z };
            _al.SetListenerProperty(ListenerFloatArray.Orientation, orient);
        }

        private uint GetBuffer(string wavPath)
        {
            if (_buffers.TryGetValue(wavPath, out var existing))
                return existing;

            var (format, data, sampleRate) = LoadWav(wavPath);
            uint buffer = _al.GenBuffer();
            fixed (byte* ptr = data)
                _al.BufferData(buffer, format, ptr, data.Length, sampleRate);

            _buffers[wavPath] = buffer;
            return buffer;
        }

        // Minimal RIFF/WAVE PCM parser: walks chunks, reads fmt + data.
        private static (BufferFormat format, byte[] data, int sampleRate) LoadWav(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12 ||
                Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                throw new InvalidDataException($"Not a WAV file: {path}");

            short channels = 0, bits = 0;
            int sampleRate = 0;
            byte[]? data = null;

            int pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                string id = Encoding.ASCII.GetString(bytes, pos, 4);
                int size = BitConverter.ToInt32(bytes, pos + 4);
                int body = pos + 8;
                if (id == "fmt " && body + 16 <= bytes.Length)
                {
                    channels = BitConverter.ToInt16(bytes, body + 2);
                    sampleRate = BitConverter.ToInt32(bytes, body + 4);
                    bits = BitConverter.ToInt16(bytes, body + 14);
                }
                else if (id == "data")
                {
                    int len = Math.Min(size, bytes.Length - body);
                    data = new byte[len];
                    Array.Copy(bytes, body, data, 0, len);
                }
                pos = body + size + (size & 1); // chunks are word-aligned
            }

            if (data == null)
                throw new InvalidDataException($"WAV has no data chunk: {path}");

            return ((channels, bits) switch
            {
                (1, 8) => BufferFormat.Mono8,
                (1, 16) => BufferFormat.Mono16,
                (2, 8) => BufferFormat.Stereo8,
                (2, 16) => BufferFormat.Stereo16,
                _ => throw new NotSupportedException(
                    $"Unsupported WAV format ({channels}ch/{bits}bit): {path}. Use 16-bit PCM."),
            }, data, sampleRate);
        }

        public void Dispose()
        {
            foreach (var s in _oneShots) _al.DeleteSource(s);
            foreach (var s in _continuous) _al.DeleteSource(s);
            foreach (var b in _buffers.Values) _al.DeleteBuffer(b);
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
            _al.Dispose();
            _alc.Dispose();
        }
    }
}
