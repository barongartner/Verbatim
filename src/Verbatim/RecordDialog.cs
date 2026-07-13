using Verbatim.Audio;
using Verbatim.Core;

namespace Verbatim;

/// <summary>
/// Captures audio to a WAV in the media folder, then hands the path back to the
/// main window to transcribe like any other file. Offers microphone, computer
/// audio (online meetings / any app), or both mixed.
/// </summary>
public sealed class RecordDialog : Form
{
    private readonly string _mediaDir;
    private readonly Recorder _recorder = new();

    private readonly RadioButton _micOnly = Radio("Microphone — record yourself or the room");
    private readonly RadioButton _sysOnly = Radio("Computer audio — online meetings, calls, anything playing");
    private readonly RadioButton _both = Radio("Microphone + computer audio — both sides of a call");
    private readonly ComboBox _micCombo = Combo();
    private readonly ComboBox _sysCombo = Combo();
    private readonly Label _micLabel = Dim("Microphone");
    private readonly Label _sysLabel = Dim("Output to capture");
    private readonly Panel _meter = new() { Size = new Size(392, 10), BackColor = Theme.BgRaised };
    private readonly Label _elapsed = new() { Text = "0:00", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Base(20f, FontStyle.Bold) };
    private readonly Label _hint = Dim("");
    private readonly Button _record = new() { Text = "●  Record", Size = new Size(150, 40) };
    private readonly Button _cancel = new() { Text = "Cancel", Size = new Size(90, 34) };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 66 };

    private DateTime _startedUtc;
    private float _level;

    /// <summary>Path of the recorded WAV (in the media dir) once the dialog
    /// closes with <see cref="DialogResult.OK"/>; otherwise null.</summary>
    public string? ResultPath { get; private set; }

    /// <summary>Friendly title for the resulting transcript.</summary>
    public string? ResultTitle { get; private set; }

    public RecordDialog(string mediaDir)
    {
        _mediaDir = mediaDir;

        Text = "Record audio";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Base();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 430);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        int y = 16;
        Controls.Add(new Label { Text = "What should Verbatim listen to?", ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y) });
        y += 26;
        foreach (var r in new[] { _both, _micOnly, _sysOnly })
        {
            r.Location = new Point(20, y);
            r.CheckedChanged += (_, _) => UpdateDeviceRows();
            Controls.Add(r);
            y += 26;
        }
        _both.Checked = true; // best default for meetings

        y += 8;
        _micLabel.Location = new Point(16, y + 4);
        _micCombo.SetBounds(150, y, 274, 24);
        Controls.Add(_micLabel); Controls.Add(_micCombo);
        y += 34;
        _sysLabel.Location = new Point(16, y + 4);
        _sysCombo.SetBounds(150, y, 274, 24);
        Controls.Add(_sysLabel); Controls.Add(_sysCombo);
        y += 40;

        // level meter
        Controls.Add(new Label { Text = "Level", ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y) });
        _meter.Location = new Point(16, y + 20);
        _meter.Paint += PaintMeter;
        Controls.Add(_meter);
        y += 44;

        _elapsed.Location = new Point(16, y);
        Controls.Add(_elapsed);
        y += 40;

        _hint.MaximumSize = new Size(408, 0);
        _hint.AutoSize = true;
        _hint.Location = new Point(16, y);
        Controls.Add(_hint);

        Theme.StyleFlat(_record, primary: true);
        _record.ForeColor = Theme.Danger;
        _record.Location = new Point(16, ClientSize.Height - 54);
        _record.Click += (_, _) => Toggle();
        Controls.Add(_record);

        Theme.StyleFlat(_cancel);
        _cancel.Location = new Point(ClientSize.Width - 106, ClientSize.Height - 51);
        _cancel.Click += (_, _) => { Close(); };
        Controls.Add(_cancel);

        PopulateDevices();
        UpdateDeviceRows();

        _tick.Tick += (_, _) => OnTick();
        FormClosing += OnClosing;
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);
    }

    private RecordingSource SelectedSource =>
        _micOnly.Checked ? RecordingSource.Microphone
        : _sysOnly.Checked ? RecordingSource.SystemAudio
        : RecordingSource.Both;

    private void PopulateDevices()
    {
        Fill(_micCombo, Recorder.InputDevices(), "No microphone found");
        Fill(_sysCombo, Recorder.OutputDevices(), "No output device found");

        static void Fill(ComboBox combo, List<AudioDevice> devices, string emptyText)
        {
            combo.Items.Clear();
            if (devices.Count == 0)
            {
                combo.Items.Add(emptyText);
                combo.SelectedIndex = 0;
                combo.Enabled = false;
                return;
            }
            foreach (var d in devices) combo.Items.Add(d);
            combo.SelectedIndex = 0; // default endpoint sorts first
        }
    }

    private void UpdateDeviceRows()
    {
        if (_recorder.IsRecording) return; // locked while capturing
        var s = SelectedSource;
        bool mic = s is RecordingSource.Microphone or RecordingSource.Both;
        bool sys = s is RecordingSource.SystemAudio or RecordingSource.Both;
        _micLabel.Enabled = _micCombo.Enabled = mic && _micCombo.Items[0] is AudioDevice;
        _sysLabel.Enabled = _sysCombo.Enabled = sys && _sysCombo.Items[0] is AudioDevice;
        _hint.Text = sys
            ? "Tip: computer audio only captures what's actually playing — start your meeting or video, then record."
            : "";
    }

    private string? SelectedId(ComboBox combo) =>
        combo.SelectedItem is AudioDevice d ? d.Id : null;

    private void Toggle()
    {
        if (_recorder.IsRecording) Stop();
        else StartRecording();
    }

    private void StartRecording()
    {
        var src = SelectedSource;
        // For a single-source mode, refuse up front if its device is missing.
        // ("Both" falls through — Recorder.Start reports cleanly if nothing's there.)
        if (src == RecordingSource.Microphone && _micCombo.SelectedItem is not AudioDevice)
        {
            MessageBox.Show(this, "No microphone is available.", "Verbatim"); return;
        }
        if (src == RecordingSource.SystemAudio && _sysCombo.SelectedItem is not AudioDevice)
        {
            MessageBox.Show(this, "No playback device is available to capture.", "Verbatim"); return;
        }

        try
        {
            _recorder.Start(src, SelectedId(_micCombo), SelectedId(_sysCombo));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not start recording: " + ex.Message, "Verbatim",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _startedUtc = DateTime.UtcNow;
        _record.Text = "■  Stop";
        _record.ForeColor = Color.White;
        SetConfigEnabled(false);
        _hint.Text = "Recording… speak or play your meeting. Click Stop when you're done.";
        _tick.Start();
    }

    private void Stop()
    {
        _tick.Stop();
        var path = Path.Combine(_mediaDir,
            "rec-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant() + ".wav");
        double seconds;
        try { seconds = _recorder.StopAndSave(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Recording failed: " + ex.Message, "Verbatim",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ResetToIdle();
            return;
        }

        if (seconds < 0.4)
        {
            try { File.Delete(path); } catch { /* ignore */ }
            MessageBox.Show(this,
                "Nothing was captured. If you chose computer audio, make sure something was actually playing.",
                "Verbatim");
            ResetToIdle();
            return;
        }

        ResultPath = path;
        ResultTitle = "Recording — " + DateTime.Now.ToString("MMM d, yyyy, h:mm tt");
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetToIdle()
    {
        _record.Text = "●  Record";
        _record.ForeColor = Theme.Danger;
        _elapsed.Text = "0:00";
        _level = 0;
        _meter.Invalidate();
        SetConfigEnabled(true);
        UpdateDeviceRows();
    }

    private void SetConfigEnabled(bool on)
    {
        _micOnly.Enabled = _sysOnly.Enabled = _both.Enabled = on;
        _micCombo.Enabled = _sysCombo.Enabled = on;
        _cancel.Enabled = on;
    }

    private void OnTick()
    {
        var elapsed = DateTime.UtcNow - _startedUtc;
        _elapsed.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        // smooth falloff so the meter doesn't strobe
        _level = Math.Max(_recorder.PeakLevel(), _level * 0.75f);
        _meter.Invalidate();
    }

    private void PaintMeter(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = (int)(_meter.Width * Math.Clamp(_level, 0f, 1f));
        if (w <= 0) return;
        var color = _level > 0.92f ? Theme.Danger : _level > 0.6f ? Theme.Mark : Theme.Accent;
        using var b = new SolidBrush(color);
        g.FillRectangle(b, 0, 0, w, _meter.Height);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _tick.Stop();
        if (_recorder.IsRecording && ResultPath is null)
            _recorder.Discard(); // cancelled mid-capture: drop the audio
        _recorder.Dispose();
    }

    private static RadioButton Radio(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Text,
        BackColor = Color.Transparent
    };

    private static ComboBox Combo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        BackColor = Theme.BgRaised,
        ForeColor = Theme.Text,
        FlatStyle = FlatStyle.Flat
    };

    private static Label Dim(string text) => new()
    {
        Text = text,
        ForeColor = Theme.TextDim,
        AutoSize = true
    };
}
