using Verbatim.Core;

namespace Verbatim;

public sealed class SettingsForm : Form
{
    private static readonly (string Code, string Name)[] Languages =
    [
        ("", "Auto-detect"), ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("sv", "Swedish"), ("no", "Norwegian"),
        ("da", "Danish"), ("fi", "Finnish"), ("pl", "Polish"), ("cs", "Czech"), ("uk", "Ukrainian"),
        ("ru", "Russian"), ("tr", "Turkish"), ("ar", "Arabic"), ("he", "Hebrew"), ("hi", "Hindi"),
        ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"), ("vi", "Vietnamese"), ("th", "Thai"),
        ("id", "Indonesian"), ("el", "Greek"), ("ro", "Romanian"), ("hu", "Hungarian"), ("ta", "Tamil")
    ];

    private readonly ProjectStore _store;
    private readonly AppSettings _settings;

    public SettingsForm(ProjectStore store, ModelManager models, YtDlp ytdlp)
    {
        _store = store;
        _settings = store.LoadSettings();

        Text = "Settings";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Base();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 470);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        int y = 14;
        Add(new Label { Text = "Transcription model", ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y) });
        y += 24;
        foreach (var m in ModelManager.WhisperModels)
        {
            var ready = models.WhisperReady(m.Id);
            var radio = new RadioButton
            {
                Text = $"{m.Label} — {m.Detail}   ({(ready ? "downloaded" : $"{m.DownloadMB} MB download")})",
                Checked = _settings.Model == m.Id,
                AutoSize = true,
                Location = new Point(20, y),
                ForeColor = Theme.Text,
                Tag = m.Id
            };
            radio.CheckedChanged += (_, _) =>
            {
                if (radio.Checked) { _settings.Model = (string)radio.Tag!; Save(); }
            };
            Controls.Add(radio);
            y += 28;
        }

        y += 10;
        var langCombo = AddCombo("Spoken language", ref y,
            Languages.Select(l => l.Name).ToArray(),
            Array.FindIndex(Languages, l => l.Code == _settings.Language) is var li and >= 0 ? li : 0);
        langCombo.SelectedIndexChanged += (_, _) =>
        {
            _settings.Language = Languages[Math.Max(0, langCombo.SelectedIndex)].Code;
            Save();
        };

        var spkItems = new[] { "Detect automatically", "1 (just me)", "2", "3", "4", "5", "6", "7", "8" };
        var spkCombo = AddCombo("Number of speakers", ref y, spkItems,
            Math.Clamp(_settings.NumSpeakers, 0, 8));
        spkCombo.SelectedIndexChanged += (_, _) =>
        {
            _settings.NumSpeakers = Math.Max(0, spkCombo.SelectedIndex);
            Save();
        };

        y += 8;
        Add(new Label
        {
            Text = $"Model storage: {models.StorageBytes() / 1_000_000} MB",
            ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y)
        });
        var openBtn = new Button { Text = "Open folder", AutoSize = true, Location = new Point(360, y - 6) };
        Theme.StyleFlat(openBtn);
        openBtn.Click += (_, _) =>
        {
            Directory.CreateDirectory(models.ModelsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{models.ModelsDir}\"") { UseShellExecute = false });
        };
        Add(openBtn);
        y += 38;

        var ytdlpLabel = new Label { Text = "Link downloader: checking…", ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y) };
        Add(ytdlpLabel);
        var updateBtn = new Button { Text = "Update", AutoSize = true, Location = new Point(360, y - 6) };
        Theme.StyleFlat(updateBtn);
        Add(updateBtn);
        updateBtn.Click += async (_, _) =>
        {
            updateBtn.Enabled = false;
            ytdlpLabel.Text = "Link downloader: updating…";
            try
            {
                var msg = await ytdlp.UpdateAsync(null, CancellationToken.None);
                ytdlpLabel.Text = $"Link downloader: {msg}";
            }
            catch
            {
                ytdlpLabel.Text = "Link downloader: update failed — check your connection";
            }
            updateBtn.Enabled = true;
        };
        _ = RefreshYtdlpAsync(ytdlp, ytdlpLabel);
        y += 44;

        Add(new Label
        {
            Text = $"Verbatim {Application.ProductVersion.Split('+')[0]} — Whisper + pyannote, fully offline",
            ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y + 8)
        });
        var done = new Button { Text = "Done", DialogResult = DialogResult.OK, Size = new Size(90, 32) };
        Theme.StyleFlat(done, primary: true);
        done.Location = new Point(ClientSize.Width - 106, ClientSize.Height - 46);
        Add(done);
        AcceptButton = done;
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);
    }

    private static async Task RefreshYtdlpAsync(YtDlp ytdlp, Label label)
    {
        var v = await ytdlp.VersionAsync();
        if (!label.IsDisposed)
            label.Text = "Link downloader: " + (v is null ? "downloads automatically on first use" : $"yt-dlp {v}");
    }

    private void Add(Control c) => Controls.Add(c);

    private ComboBox AddCombo(string label, ref int y, string[] items, int selected)
    {
        Add(new Label { Text = label, ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(16, y + 4) });
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.BgRaised,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(190, y),
            Width = 200
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        Add(combo);
        y += 40;
        return combo;
    }

    private void Save() => _store.SaveSettings(_settings);
}
