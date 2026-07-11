namespace Verbatim.Controls;

/// <summary>Waveform seek bar: peaks drawn as bars, played portion in accent.</summary>
public sealed class WaveformBar : Control
{
    private IReadOnlyList<double>? _peaks;
    private double _duration;
    private double _position;
    private bool _dragging;

    public event Action<double>? SeekRequested;

    public WaveformBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgRaised;
        Cursor = Cursors.Hand;
        Height = 44;
    }

    public void SetAudio(IReadOnlyList<double>? peaks, double duration)
    {
        _peaks = peaks;
        _duration = duration;
        _position = 0;
        Invalidate();
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Position
    {
        get => _position;
        set { _position = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_peaks is null || _peaks.Count == 0 || _duration <= 0) return;
        var g = e.Graphics;
        float playedX = (float)(_position / _duration) * Width;
        int n = _peaks.Count;
        float bw = (float)Width / n;
        using var dim = new SolidBrush(Theme.WaveDim);
        using var lit = new SolidBrush(Theme.Accent);
        for (int i = 0; i < n; i++)
        {
            float h = Math.Max(2, (float)_peaks[i] * (Height - 6));
            float x = i * bw;
            g.FillRectangle(x <= playedX ? lit : dim, x, (Height - h) / 2, Math.Max(1, bw - 1), h);
        }
    }

    private void SeekTo(int mouseX)
    {
        if (_duration <= 0) return;
        var pct = Math.Clamp((double)mouseX / Math.Max(1, Width), 0, 1);
        SeekRequested?.Invoke(pct * _duration);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragging = true;
        SeekTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SeekTo(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }
}
