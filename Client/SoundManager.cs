using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Silk.NET.OpenAL;
using Stride.Engine;
using SVector3 = Stride.Core.Mathematics.Vector3;
using Stride.Core.Mathematics;

namespace Demiurge
{
    /// <summary>
    /// Audio via OpenAL directly (Silk.NET.OpenAL), bypassing Stride's audio entirely —
    /// Stride's native layer deadlocks on this platform (see AUDIO.md). OpenAL itself is
    /// fine, so we drive the same system library ourselves.
    ///
    /// Loads 16-bit PCM WAV files, caches one OpenAL buffer per file, and round-robins a
    /// fixed pool of sources. <see cref="Play(string)"/> is non-positional; <see
    /// cref="Play(string, SVector3)"/> places the sound in 3D relative to the listener
    /// (the camera). Mono WAVs spatialize; stereo WAVs play un-positioned (OpenAL rule).
    /// </summary>
    public sealed unsafe class SoundManager : IDisposable
    {
        private readonly AL _al;
        private readonly ALContext _alc;
        private readonly Device* _device;
        private readonly Context* _context;
        private readonly Entity? _listener;          // camera; drives 3D listener pose

        private readonly Dictionary<string, uint> _buffers = new();
        private readonly uint[] _sources;
        private int _next;

        public SoundManager(Entity? listener = null, int voices = 32)
        {
            _listener = listener;

            _alc = ALContext.GetApi(soft: false);
            _al = AL.GetApi(soft: false);

            _device = _alc.OpenDevice("");
            Console.WriteLine($"[OAL] OpenDevice -> {((nint)_device):x}");
            if (_device == null)
                throw new InvalidOperationException("OpenAL: failed to open the default audio device.");
            _context = _alc.CreateContext(_device, null);
            bool made = _alc.MakeContextCurrent(_context);
            Console.WriteLine($"[OAL] CreateContext -> {((nint)_context):x}, MakeCurrent -> {made}, ctxErr={_alc.GetError(_device)}");

            _sources = new uint[voices];
            for (int i = 0; i < voices; i++)
                _sources[i] = _al.GenSource();
            Console.WriteLine($"[OAL] generated {voices} sources, alErr={_al.GetError()}");
        }

        private void Err(string stage)
        {
            var e = _al.GetError();
            if (e != AudioError.NoError) Console.WriteLine($"[OAL] ERROR after {stage}: {e}");
        }

        /// <summary>Play a sound non-positionally (centered, full volume).</summary>
        public void Play(string wavPath) => PlayInternal(wavPath, null);

        /// <summary>Play a sound positioned in world space relative to the listener (camera).</summary>
        public void Play(string wavPath, SVector3 worldPosition) => PlayInternal(wavPath, worldPosition);

        private void PlayInternal(string wavPath, SVector3? worldPos)
        {
            // OpenAL's "current context" is process-global; Stride's audio system shares the
            // library, so make ours current before our calls target our device.
            _alc.MakeContextCurrent(_context);

            uint buffer = GetBuffer(wavPath);

            uint source = _sources[_next];
            _next = (_next + 1) % _sources.Length;

            // Stopping the source (safe in real OpenAL) lets us (re)assign the buffer, and
            // a still-playing voice we cycle back to is simply restarted.
            _al.SourceStop(source); Err("SourceStop");
            _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer); Err("SetBuffer");

            if (worldPos is { } p)
            {
                UpdateListener();
                _al.SetSourceProperty(source, SourceBoolean.SourceRelative, false);
                _al.SetSourceProperty(source, SourceVector3.Position, p.X, p.Y, p.Z);
            }
            else
            {
                // Relative to a listener pinned at the origin => centered, no attenuation.
                _al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
                _al.SetSourceProperty(source, SourceVector3.Position, 0f, 0f, 0f);
            }
            Err("SetPosition");

            _al.SourcePlay(source); Err("SourcePlay");
            _al.GetSourceProperty(source, GetSourceInteger.SourceState, out int st);
            _al.GetListenerProperty(ListenerVector3.Position, out var lp);
            Console.WriteLine($"[OAL] play buf={buffer} src={source} pos={worldPos} state={(SourceState)st} listener={lp}");
        }

        // Sync the OpenAL listener to the camera's world pose before a positional play.
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
            Console.WriteLine($"[OAL] loaded {wavPath}: fmt={format} bytes={data.Length} rate={sampleRate} buf={buffer} alErr={_al.GetError()}");

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

            BufferFormat format = (channels, bits) switch
            {
                (1, 8) => BufferFormat.Mono8,
                (1, 16) => BufferFormat.Mono16,
                (2, 8) => BufferFormat.Stereo8,
                (2, 16) => BufferFormat.Stereo16,
                _ => throw new NotSupportedException(
                    $"Unsupported WAV format ({channels}ch/{bits}bit): {path}. Use 16-bit PCM."),
            };
            return (format, data, sampleRate);
        }

        public void Dispose()
        {
            foreach (var s in _sources) _al.DeleteSource(s);
            foreach (var b in _buffers.Values) _al.DeleteBuffer(b);
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
            _al.Dispose();
            _alc.Dispose();
        }
    }
}
