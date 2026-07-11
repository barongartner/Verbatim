using NAudio.Wave;

namespace Verbatim.Audio;

/// <summary>Simple audio player over NAudio: load a file, play/pause, seek.</summary>
public sealed class Player : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    public event Action? PositionChanged;
    public event Action? PlaybackStateChanged;

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public double DurationSec => _reader?.TotalTime.TotalSeconds ?? 0;

    public double PositionSec
    {
        get => _reader?.CurrentTime.TotalSeconds ?? 0;
        set
        {
            if (_reader is null) return;
            var t = TimeSpan.FromSeconds(Math.Clamp(value, 0, DurationSec));
            _reader.CurrentTime = t;
            PositionChanged?.Invoke();
        }
    }

    public Player() => _timer.Tick += (_, _) =>
    {
        PositionChanged?.Invoke();
        if (_reader is not null && _reader.CurrentTime >= _reader.TotalTime) Pause();
    };

    public bool Load(string path)
    {
        Unload();
        try
        {
            _reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? new WaveFileReader(path)
                : new MediaFoundationReader(path);
            _output = new WaveOutEvent { DesiredLatency = 200 };
            _output.Init(_reader);
            return true;
        }
        catch
        {
            Unload();
            return false;
        }
    }

    public void Play()
    {
        if (_output is null) return;
        _output.Play();
        _timer.Start();
        PlaybackStateChanged?.Invoke();
    }

    public void Pause()
    {
        if (_output is null) return;
        _output.Pause();
        _timer.Stop();
        PlaybackStateChanged?.Invoke();
        PositionChanged?.Invoke();
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Unload()
    {
        _timer.Stop();
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        Unload();
        _timer.Dispose();
    }
}
