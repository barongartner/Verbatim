using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Verbatim.Core;

namespace Verbatim.Audio;

/// <summary>What Verbatim listens to while recording.</summary>
public enum RecordingSource
{
    /// <summary>A microphone — record yourself or a room.</summary>
    Microphone,
    /// <summary>The computer's own output ("loopback"): online meetings, calls,
    /// and audio playing in any app. Captures everyone you hear, not your mic.</summary>
    SystemAudio,
    /// <summary>Microphone and system audio mixed — the whole meeting, both
    /// sides. The right default for calls.</summary>
    Both
}

/// <summary>A selectable capture endpoint (mic or output device).</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name}  (default)" : Name;
}

/// <summary>
/// Records from the microphone, the computer's own output (system/loopback), or
/// both mixed, straight to the 16 kHz mono PCM the transcription pipeline wants.
/// Each source is captured on its own WASAPI clock and resampled to 16 kHz mono
/// as samples arrive; on stop the sources are summed (see <see cref="AudioMixer"/>)
/// and written as a WAV. Windows-only — WASAPI loopback has no macOS equivalent,
/// which is why this lives in the WinForms project, not Core.
/// </summary>
public sealed class Recorder : IDisposable
{
    public const int SampleRate = TranscriptionPipeline.SampleRate; // 16000

    private readonly MMDeviceEnumerator _enum = new();
    private SourceCapture? _mic;
    private SourceCapture? _system;

    public bool IsRecording { get; private set; }

    public static List<AudioDevice> InputDevices() => Endpoints(DataFlow.Capture);
    public static List<AudioDevice> OutputDevices() => Endpoints(DataFlow.Render);

    private static List<AudioDevice> Endpoints(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defId = en.HasDefaultAudioEndpoint(flow, Role.Multimedia)
                ? en.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID
                : null;
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defId));
        }
        catch { /* no audio subsystem — return whatever we have */ }
        // default first, then alphabetical
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Begin capturing. <paramref name="micId"/>/<paramref name="systemId"/>
    /// may be null for "system default"; each is used only when the source needs it.
    /// Unknown/removed device ids fall back to the current default.</summary>
    public void Start(RecordingSource source, string? micId, string? systemId)
    {
        if (IsRecording) return;
        try
        {
            if (source is RecordingSource.Microphone or RecordingSource.Both)
                _mic = SourceCapture.ForMicrophone(Resolve(DataFlow.Capture, micId));
            if (source is RecordingSource.SystemAudio or RecordingSource.Both)
                _system = SourceCapture.ForLoopback(Resolve(DataFlow.Render, systemId));

            _mic?.Start();
            _system?.Start();
            IsRecording = true;
        }
        catch
        {
            _mic?.Dispose(); _mic = null;
            _system?.Dispose(); _system = null;
            throw;
        }
    }

    private MMDevice Resolve(DataFlow flow, string? id)
    {
        if (id is not null)
        {
            try { return _enum.GetDevice(id); } catch { /* removed — use default */ }
        }
        return _enum.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }

    /// <summary>Loudest sample seen on any active source since the last call,
    /// in [0,1] — drives the level meter.</summary>
    public float PeakLevel()
    {
        float p = 0;
        if (_mic is not null) p = Math.Max(p, _mic.TakePeak());
        if (_system is not null) p = Math.Max(p, _system.TakePeak());
        return p;
    }

    /// <summary>Stop capturing, mix the sources, and write 16 kHz mono WAV to
    /// <paramref name="wavPath"/>. Returns its duration in seconds (0 if nothing
    /// was captured — e.g. system audio with nothing playing and no mic).</summary>
    public double StopAndSave(string wavPath)
    {
        if (!IsRecording) return 0;
        IsRecording = false;

        var mic = _mic?.StopAndDrain();
        var system = _system?.StopAndDrain();
        var pcm = AudioMixer.Mix(mic, system);

        Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
        WavWriter.WriteSlice(wavPath, pcm, SampleRate, 0, pcm.Length);
        return (double)pcm.Length / SampleRate;
    }

    /// <summary>Stop capturing and throw the audio away (cancelled recording).</summary>
    public void Discard()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _mic?.StopAndDrain();
        _system?.StopAndDrain();
    }

    public void Dispose()
    {
        _mic?.Dispose();
        _system?.Dispose();
        _enum.Dispose();
    }

    /// <summary>
    /// One capture source: native WASAPI capture → 16 kHz mono float accumulator.
    /// Resampling runs incrementally on the capture thread so a long meeting never
    /// holds the raw high-rate audio in memory.
    /// </summary>
    private sealed class SourceCapture : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly BufferedWaveProvider _buffer;
        private readonly ISampleProvider _chain;         // 16 kHz mono float
        private readonly List<float> _samples = new(1 << 20);
        private readonly float[] _scratch = new float[SampleRate]; // ~1s per drain
        private readonly object _lock = new();
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private float _peak;

        private SourceCapture(IWaveIn capture)
        {
            _capture = capture;
            _buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                ReadFully = false,               // Read returns 0 when drained (not zero-padding)
                DiscardOnBufferOverflow = true,  // never grow without bound if a drain stalls
                BufferDuration = TimeSpan.FromSeconds(10)
            };

            ISampleProvider sp = _buffer.ToSampleProvider();               // float, native rate/channels
            if (sp.WaveFormat.Channels > 1) sp = new MonoDownmix(sp);      // stereo/5.1 -> mono
            if (sp.WaveFormat.SampleRate != SampleRate)
                sp = new WdlResamplingSampleProvider(sp, SampleRate);      // -> 16 kHz
            _chain = sp;

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => _stopped.TrySetResult();
        }

        public static SourceCapture ForMicrophone(MMDevice dev) => new(new WasapiCapture(dev));
        public static SourceCapture ForLoopback(MMDevice dev) => new(new WasapiLoopbackCapture(dev));

        public void Start() => _capture.StartRecording();

        private void OnData(object? sender, WaveInEventArgs e)
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Drain();
        }

        private void Drain()
        {
            lock (_lock)
            {
                int n;
                while ((n = _chain.Read(_scratch, 0, _scratch.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        var v = _scratch[i];
                        _samples.Add(v);
                        var a = MathF.Abs(v);
                        if (a > _peak) _peak = a;
                    }
                }
            }
        }

        public float TakePeak()
        {
            lock (_lock) { var p = _peak; _peak = 0; return p; }
        }

        public IReadOnlyList<float> StopAndDrain()
        {
            try { _capture.StopRecording(); } catch { /* already stopped */ }
            try { _stopped.Task.Wait(TimeSpan.FromSeconds(3)); } catch { /* flush best-effort */ }
            Drain(); // pick up whatever RecordingStopped flushed into the buffer
            lock (_lock) return _samples;
        }

        public void Dispose()
        {
            try { _capture.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>Averages any channel count down to mono (WASAPI shared-mode
    /// capture is commonly stereo). Cheaper and more forgiving than NAudio's
    /// stereo-only <c>ToMono</c>.</summary>
    private sealed class MonoDownmix : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _ch;
        private float[] _buf = [];

        public WaveFormat WaveFormat { get; }

        public MonoDownmix(ISampleProvider src)
        {
            _src = src;
            _ch = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int need = count * _ch;
            if (_buf.Length < need) _buf = new float[need];
            int read = _src.Read(_buf, 0, need);
            int frames = read / _ch;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int b = f * _ch;
                for (int c = 0; c < _ch; c++) sum += _buf[b + c];
                buffer[offset + f] = sum / _ch;
            }
            return frames;
        }
    }
}
