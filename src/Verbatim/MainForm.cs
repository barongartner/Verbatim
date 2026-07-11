using Verbatim.Audio;
using Verbatim.Controls;
using Verbatim.Core;

namespace Verbatim;

public sealed partial class MainForm : Form
{
    private readonly ProjectStore _store;
    private readonly ModelManager _models;
    private readonly YtDlp _ytdlp;
    private readonly string _engineDir;
    private readonly Player _player = new();

    private Project? _project;
    private CancellationTokenSource? _job;
    private bool _followPlayback = true;

    // views
    private readonly Panel _home = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel _progress = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _transcript = new() { Dock = DockStyle.Fill, Visible = false };

    // home widgets
    private readonly TextBox _urlBox = new();
    private readonly FlowLayoutPanel _library = new()
    { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = 660 };
    private readonly Label _libraryHeader = new();

    // progress widgets
    private readonly Label _progFile = new();
    private readonly Label _progStage = new();
    private readonly Label _progDetail = new();
    private readonly ProgressBar _progBar = new() { Width = 460, Height = 8 };

    // transcript widgets
    private readonly TextBox _titleBox = new();
    private readonly TextBox _searchBox = new();
    private readonly Label _searchCount = new();
    private readonly FlowLayoutPanel _speakerBar = new()
    { Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 7, 12, 3), BackColor = Theme.Bg };
    private readonly TranscriptView _view = new() { Dock = DockStyle.Fill };
    private readonly Button _playBtn = new();
    private readonly Label _timeNow = new();
    private readonly Label _timeTotal = new();
    private readonly WaveformBar _waveform = new();

    private System.Windows.Forms.Timer? _saveTimer;

    public MainForm()
    {
        var userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Verbatim");
        _store = new ProjectStore(userDir);
        _models = new ModelManager(_store.ModelsDir);
        _ytdlp = new YtDlp(_store.ToolsDir, _store.MediaDir);
        _engineDir = EngineExtractor.EnsureExtracted();
        _store.SweepMediaDir();

        Text = "Verbatim";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Base();
        ClientSize = new Size(1180, 800);
        MinimumSize = new Size(880, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        BuildHome();
        BuildProgress();
        BuildTranscript();
        Controls.Add(_home);
        Controls.Add(_progress);
        Controls.Add(_transcript);

        DragEnter += (_, e) =>
        {
            if (_home.Visible && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
            if (_home.Visible && files is { Length: > 0 }) _ = TranscribeFileAsync(files[0]);
        };
        FormClosing += (_, _) => { _job?.Cancel(); _player.Dispose(); };
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);
        KeyPreview = true;
        KeyDown += OnGlobalKeyDown;

        RefreshLibrary();
    }

    private void ShowView(Panel p)
    {
        _home.Visible = p == _home;
        _progress.Visible = p == _progress;
        _transcript.Visible = p == _transcript;
    }

    private void Toast(string msg) => _progDetail.Text = msg; // progress view feedback

    private static Label MakeLabel(string text, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        Font = Theme.Base(size, style),
        ForeColor = color,
        AutoSize = true,
        BackColor = Color.Transparent
    };

    // ---------------------------------------------------------------- home --
    private void BuildHome()
    {
        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Top,
            Padding = new Padding(0, 28, 0, 40)
        };

        var title = MakeLabel("Verbatim", 26f, FontStyle.Bold, Theme.Text);
        var tagline = MakeLabel("Every word, on your machine. Free, private, offline transcription.",
            10f, FontStyle.Regular, Theme.TextDim);

        var drop = new Panel { Size = new Size(660, 180), BackColor = Theme.BgRaised, Cursor = Cursors.Hand };
        drop.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, 1, 1, drop.Width - 3, drop.Height - 3);
            TextRenderer.DrawText(e.Graphics, "Drop an audio file here",
                Theme.Base(13f, FontStyle.Bold), new Rectangle(0, 52, drop.Width, 30),
                Theme.Text, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, "or click to browse — then Verbatim writes the transcript",
                Theme.Base(9.5f), new Rectangle(0, 86, drop.Width, 24),
                Theme.TextDim, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, "MP3 · WAV · M4A · AAC · WMA · FLAC",
                Theme.Base(8.5f), new Rectangle(0, 126, drop.Width, 20),
                Theme.TextDim, TextFormatFlags.HorizontalCenter);
        };
        drop.Click += (_, _) => BrowseForAudio();

        var urlRow = new Panel { Size = new Size(660, 36) };
        Theme.StyleInput(_urlBox);
        _urlBox.PlaceholderText = "…or paste a link — YouTube, podcast, or any audio/video URL";
        _urlBox.SetBounds(0, 3, 520, 30);
        _urlBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = TranscribeUrlAsync(_urlBox.Text); } };
        var urlBtn = new Button { Text = "Transcribe link", Size = new Size(130, 30), Location = new Point(530, 3) };
        Theme.StyleFlat(urlBtn, primary: true);
        urlBtn.Click += (_, _) => _ = TranscribeUrlAsync(_urlBox.Text);
        urlRow.Controls.Add(_urlBox);
        urlRow.Controls.Add(urlBtn);

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var openBtn = new Button { Text = "Open saved transcript…", AutoSize = true };
        Theme.StyleFlat(openBtn);
        openBtn.Click += (_, _) => OpenProjectFile();
        var settingsBtn = new Button { Text = "Settings", AutoSize = true };
        Theme.StyleFlat(settingsBtn);
        settingsBtn.Click += (_, _) =>
        {
            using var dlg = new SettingsForm(_store, _models, _ytdlp);
            dlg.ShowDialog(this);
        };
        actions.Controls.Add(openBtn);
        actions.Controls.Add(settingsBtn);

        _libraryHeader.Text = "Recent transcripts";
        _libraryHeader.Font = Theme.Base(10f, FontStyle.Bold);
        _libraryHeader.ForeColor = Theme.TextDim;
        _libraryHeader.AutoSize = true;
        _libraryHeader.Margin = new Padding(0, 22, 0, 6);

        foreach (var c in new Control[] { title, tagline, drop, urlRow, actions, _libraryHeader, _library })
        {
            c.Margin = new Padding(0, c == title ? 0 : 10, 0, 0);
            col.Controls.Add(c);
        }
        col.Location = new Point(Math.Max(0, (ClientSize.Width - 680) / 2), 0);
        _home.Resize += (_, _) => col.Left = Math.Max(0, (_home.ClientSize.Width - col.Width) / 2);
        _home.Controls.Add(col);
    }

    private void BrowseForAudio()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.mp4;*.m4b|All files|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _ = TranscribeFileAsync(dlg.FileName);
    }

    private void OpenProjectFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Verbatim project|*.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var p = _store.Import(dlg.FileName);
        if (p is null) { MessageBox.Show(this, "That file is not a Verbatim transcript.", "Verbatim"); return; }
        OpenProject(p);
    }

    private void RefreshLibrary()
    {
        _library.Controls.Clear();
        var entries = _store.List();
        _libraryHeader.Visible = entries.Count > 0;
        foreach (var e in entries) _library.Controls.Add(MakeLibraryCard(e));
    }

    private Panel MakeLibraryCard(LibraryEntry entry)
    {
        var card = new Panel { Size = new Size(660, 74), BackColor = Theme.BgRaised, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 8) };
        var title = MakeLabel(entry.Title, 10.5f, FontStyle.Bold, Theme.Text);
        title.Location = new Point(14, 10);
        var meta = MakeLabel(
            $"{Exporters.FmtTime(entry.DurationSec)} · {string.Join(", ", entry.SpeakerNames.Take(4))} · {FmtDate(entry.UpdatedAt)}",
            8.5f, FontStyle.Regular, Theme.TextDim);
        meta.Location = new Point(14, 32);
        var snippet = MakeLabel(entry.Snippet, 8.5f, FontStyle.Regular, Theme.TextDim);
        snippet.Location = new Point(14, 50);
        snippet.AutoSize = false;
        snippet.Size = new Size(590, 18);
        snippet.AutoEllipsis = true;

        var del = new Button { Text = "✕", Size = new Size(28, 28), Location = new Point(620, 23) };
        Theme.StyleFlat(del);
        del.FlatAppearance.BorderSize = 0;
        del.Click += (_, e2) =>
        {
            var fetched = _store.IsInMediaDir(entry.AudioPath);
            var note = fetched ? "Its downloaded audio will be removed too." : "The audio file is not touched.";
            if (MessageBox.Show(this, $"Delete transcript \"{entry.Title}\"? {note}", "Verbatim",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            _store.Delete(entry.Id);
            RefreshLibrary();
        };

        foreach (Control c in new Control[] { title, meta, snippet })
            c.Click += (_, _) => OpenSaved(entry.Id);
        card.Click += (_, _) => OpenSaved(entry.Id);
        card.Controls.AddRange([title, meta, snippet, del]);
        return card;
    }

    private static string FmtDate(string? iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime().ToString("MMM d, yyyy, h:mm tt") : "";

    private void OpenSaved(string id)
    {
        var p = _store.Load(id);
        if (p is null) { RefreshLibrary(); return; }
        OpenProject(p);
    }

    // ------------------------------------------------------------ progress --
    private void BuildProgress()
    {
        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.None
        };
        _progFile.Font = Theme.Base(12f, FontStyle.Bold);
        _progFile.ForeColor = Theme.Text;
        _progFile.AutoSize = true;
        _progStage.Font = Theme.Base(10.5f);
        _progStage.ForeColor = Theme.Text;
        _progStage.AutoSize = true;
        _progDetail.Font = Theme.Base(9f);
        _progDetail.ForeColor = Theme.TextDim;
        _progDetail.AutoSize = true;
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        Theme.StyleFlat(cancel);
        cancel.ForeColor = Theme.Danger;
        cancel.Click += (_, _) => _job?.Cancel();

        foreach (var c in new Control[] { _progFile, _progStage, _progBar, _progDetail, cancel })
        {
            c.Margin = new Padding(0, 8, 0, 0);
            c.Anchor = AnchorStyles.None;
            col.Controls.Add(c);
        }
        _progress.Controls.Add(col);
        _progress.Resize += (_, _) => col.Location = new Point(
            (_progress.ClientSize.Width - col.Width) / 2,
            (_progress.ClientSize.Height - col.Height) / 2 - 40);
    }

    private void SetProgress(string stage, double pct, string detail)
    {
        if (InvokeRequired) { BeginInvoke(() => SetProgress(stage, pct, detail)); return; }
        _progStage.Text = stage;
        _progBar.Value = (int)Math.Clamp(pct, 0, 100);
        _progDetail.Text = detail;
    }

    // ------------------------------------------------------- transcription --
    private async Task TranscribeFileAsync(string path)
    {
        if (_job is not null) return;
        _progFile.Text = Path.GetFileName(path);
        SetProgress("Preparing audio…", 2, "");
        ShowView(_progress);
        _job = new CancellationTokenSource();
        try
        {
            var decoded = await Task.Run(() => AudioDecoder.Decode(path, _job.Token));
            if (decoded.DurationSec > 4 * 3600)
                throw new InvalidOperationException("Files longer than 4 hours are not supported yet");
            var result = await RunPipelineAsync(decoded.Pcm16k, _job.Token);
            FinishTranscription(result, path, Path.GetFileName(path), null, decoded);
        }
        catch (Exception ex) { HandleJobError(ex, null); }
        finally { _job?.Dispose(); _job = null; }
    }

    private async Task TranscribeUrlAsync(string url)
    {
        if (_job is not null || string.IsNullOrWhiteSpace(url)) return;
        _progFile.Text = url.Trim();
        SetProgress("Fetching audio…", 1, "");
        ShowView(_progress);
        _job = new CancellationTokenSource();
        FetchedMedia? fetched = null;
        try
        {
            var progress = new Progress<StageProgress>(p => SetProgress(
                p.Stage switch
                {
                    "probe" => "Reading the link…",
                    "download" => "Fetching audio…",
                    _ => p.Stage
                }, p.Stage == "download" ? p.Pct : 2, p.Detail));
            fetched = await _ytdlp.FetchAsync(url, progress,
                (r, t) => SetProgress("Getting the link downloader (one time)…", t > 0 ? r * 100.0 / t : 0, ""),
                _job.Token);
            _progFile.Text = fetched.Title;

            var decoded = await Task.Run(() => AudioDecoder.Decode(fetched.FilePath, _job.Token));
            if (decoded.DurationSec > 4 * 3600)
                throw new InvalidOperationException("Audio longer than 4 hours is not supported yet");
            var result = await RunPipelineAsync(decoded.Pcm16k, _job.Token);
            if (result.Segments.Count == 0) _store.DiscardMediaIfUnreferenced(fetched.FilePath);
            FinishTranscription(result, fetched.FilePath, $"{fetched.Title}.{fetched.Ext}", fetched.Title, decoded);
            if (_project is not null) BeginInvoke(() => _urlBox.Text = "");
        }
        catch (Exception ex)
        {
            if (fetched is not null) _store.DiscardMediaIfUnreferenced(fetched.FilePath);
            HandleJobError(ex, url);
        }
        finally { _job?.Dispose(); _job = null; }
    }

    private async Task<PipelineResult> RunPipelineAsync(short[] pcm, CancellationToken ct)
    {
        var settings = _store.LoadSettings();
        await _models.EnsureAsync(settings.Model, new Progress<ModelProgress>(p =>
            SetProgress($"Downloading speech models (one time) — {p.Label}",
                p.Total > 0 ? p.Received * 100.0 / p.Total : 0, p.Status)), ct);

        var (enc, dec, tok) = _models.WhisperPaths(settings.Model);
        var tempDir = Path.Combine(Path.GetTempPath(), $"verbatim-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            var progress = new Progress<StageProgress>(p => SetProgress(
                p.Stage switch
                {
                    "prepare" => "Preparing audio…",
                    "diarize" => "Listening for speakers…",
                    "transcribe" => "Writing the transcript…",
                    _ => "Finishing…"
                },
                p.Stage switch
                {
                    "diarize" => 5 + p.Pct * 0.40,
                    "transcribe" => 45 + p.Pct * 0.55,
                    "done" => 100,
                    _ => 4
                }, p.Detail));
            return await TranscriptionPipeline.TranscribeAsync(pcm, new PipelineOptions
            {
                BinDir = _engineDir,
                SegmentationModel = _models.SegmentationPath,
                EmbeddingModel = _models.EmbeddingPath,
                WhisperEncoder = enc,
                WhisperDecoder = dec,
                WhisperTokens = tok,
                TempDir = tempDir,
                Language = settings.Language,
                NumSpeakers = settings.NumSpeakers
            }, progress, ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    private void FinishTranscription(PipelineResult result, string audioPath, string audioName,
        string? displayTitle, DecodedAudio decoded)
    {
        if (InvokeRequired) { BeginInvoke(() => FinishTranscription(result, audioPath, audioName, displayTitle, decoded)); return; }
        if (result.Segments.Count == 0)
        {
            MessageBox.Show(this, "No speech was found in that audio.", "Verbatim");
            ShowView(_home);
            RefreshLibrary();
            return;
        }
        var keys = result.Segments.Select(s => s.Speaker).Distinct().Order().ToList();
        var speakers = new Dictionary<string, SpeakerInfo>();
        for (int i = 0; i < keys.Count; i++)
            speakers[keys[i]] = new SpeakerInfo { Name = $"Speaker {i + 1}", Color = SpeakerPalette.Color(i) };

        _project = new Project
        {
            AppVersion = Application.ProductVersion.Split('+')[0],
            Title = displayTitle ?? Path.GetFileNameWithoutExtension(audioName),
            AudioName = audioName,
            AudioPath = audioPath,
            DurationSec = result.DurationSec,
            Language = result.Language,
            Model = _store.LoadSettings().Model,
            Speakers = speakers,
            Segments = result.Segments,
            Peaks = decoded.Peaks.ToList()
        };
        _store.Save(_project);
        OpenProject(_project);
    }

    private void HandleJobError(Exception ex, string? url)
    {
        if (InvokeRequired) { BeginInvoke(() => HandleJobError(ex, url)); return; }
        if (ex is not OperationCanceledException)
        {
            var msg = ex is EngineException ee ? ee.FriendlyMessage : ex.Message;
            MessageBox.Show(this, (url is null ? "Something went wrong: " : "Could not fetch that link: ") + msg,
                "Verbatim", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        ShowView(_home);
        RefreshLibrary();
    }

    // ---------------------------------------------------------- transcript --
    private void BuildTranscript()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Bg, Padding = new Padding(10, 8, 10, 8) };
        var back = new Button { Text = "←", Size = new Size(38, 32), Location = new Point(10, 8) };
        Theme.StyleFlat(back);
        back.Click += (_, _) => BackHome();

        Theme.StyleInput(_titleBox);
        _titleBox.SetBounds(56, 10, 220, 28);
        _titleBox.Font = Theme.Base(10.5f, FontStyle.Bold);
        _titleBox.TextChanged += (_, _) =>
        {
            if (_project is null) return;
            _project.Title = string.IsNullOrWhiteSpace(_titleBox.Text) ? "Untitled" : _titleBox.Text.Trim();
            ScheduleSave();
        };

        Theme.StyleInput(_searchBox);
        _searchBox.PlaceholderText = "Search transcript…  (Ctrl+F)";
        _searchBox.SetBounds(300, 10, 300, 28);
        _searchBox.TextChanged += (_, _) => RunSearch();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; JumpMatch(e.Shift ? -1 : 1); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _searchBox.Text = ""; }
        };
        _searchCount.ForeColor = Theme.TextDim;
        _searchCount.AutoSize = true;
        _searchCount.Location = new Point(606, 16);

        var prev = new Button { Text = "▲", Size = new Size(30, 28), Location = new Point(662, 10) };
        var next = new Button { Text = "▼", Size = new Size(30, 28), Location = new Point(694, 10) };
        Theme.StyleFlat(prev);
        Theme.StyleFlat(next);
        prev.Click += (_, _) => JumpMatch(-1);
        next.Click += (_, _) => JumpMatch(1);

        var copy = new Button { Text = "Copy", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Theme.StyleFlat(copy);
        copy.Location = new Point(header.Width - 190, 10);
        copy.Click += (_, _) =>
        {
            if (_project is not null) Clipboard.SetText(Exporters.BuildTxt(_project));
        };

        var export = new Button { Text = "Export ▾", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Theme.StyleFlat(export, primary: true);
        export.Location = new Point(header.Width - 110, 10);
        var menu = new ContextMenuStrip { BackColor = Theme.BgRaised, ForeColor = Theme.Text };
        menu.Items.Add("Text (.txt)", null, (_, _) => Export("txt"));
        menu.Items.Add("Subtitles (.srt)", null, (_, _) => Export("srt"));
        menu.Items.Add("Subtitles (.vtt)", null, (_, _) => Export("vtt"));
        menu.Items.Add("Data (.json)", null, (_, _) => Export("json"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Verbatim project…", null, (_, _) => Export("project"));
        export.Click += (_, _) => menu.Show(export, new Point(0, export.Height));

        header.Controls.AddRange([back, _titleBox, _searchBox, _searchCount, prev, next, copy, export]);

        // player bar
        var playerBar = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.BgRaised, Padding = new Padding(12, 10, 12, 10) };
        _playBtn.Text = "▶";
        _playBtn.Size = new Size(42, 42);
        _playBtn.Location = new Point(12, 11);
        Theme.StyleFlat(_playBtn, primary: true);
        _playBtn.Click += (_, _) => _player.Toggle();

        _timeNow.Text = "0:00";
        _timeNow.ForeColor = Theme.TextDim;
        _timeNow.AutoSize = true;
        _timeNow.Location = new Point(64, 24);
        _timeTotal.Text = "0:00";
        _timeTotal.ForeColor = Theme.TextDim;
        _timeTotal.AutoSize = true;
        _timeTotal.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _waveform.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _waveform.SeekRequested += t => { _player.PositionSec = t; };

        var follow = new CheckBox { Text = "Follow", Appearance = Appearance.Button, Checked = true, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        follow.FlatStyle = FlatStyle.Flat;
        follow.FlatAppearance.BorderColor = Theme.AccentDeep;
        follow.FlatAppearance.CheckedBackColor = Theme.BgHover;
        follow.ForeColor = Theme.Accent;
        follow.CheckedChanged += (_, _) =>
        {
            _followPlayback = follow.Checked;
            follow.ForeColor = follow.Checked ? Theme.Accent : Theme.TextDim;
        };

        playerBar.Controls.AddRange([_playBtn, _timeNow, _waveform, _timeTotal, follow]);
        playerBar.Resize += (_, _) =>
        {
            follow.Location = new Point(playerBar.Width - follow.Width - 14, 20);
            _timeTotal.Location = new Point(follow.Left - _timeTotal.Width - 10, 24);
            _waveform.SetBounds(112, 10, Math.Max(60, _timeTotal.Left - 122), 44);
        };

        _view.TimeClicked += i =>
        {
            if (_project is null) return;
            _player.PositionSec = _project.Segments[i].Start;
            if (!_player.IsPlaying) _player.Play();
        };
        _view.Edited += ScheduleSave;

        _player.PositionChanged += OnPlayerPosition;
        _player.PlaybackStateChanged += () => _playBtn.Text = _player.IsPlaying ? "⏸" : "▶";

        _transcript.Controls.Add(_view);
        _transcript.Controls.Add(_speakerBar);
        _transcript.Controls.Add(header);
        _transcript.Controls.Add(playerBar);
        _view.BringToFront();
    }

    private void OnPlayerPosition()
    {
        _timeNow.Text = Exporters.FmtTime(_player.PositionSec);
        _waveform.Position = _player.PositionSec;
        if (_view.SetActiveTime(_player.PositionSec) && _followPlayback && _view.ActiveRow >= 0)
            _view.ScrollToRow(_view.ActiveRow);
    }

    private void OpenProject(Project p)
    {
        _project = p;
        _titleBox.Text = p.Title;
        _searchBox.Text = "";
        _searchCount.Text = "";
        _view.Project = p;
        RebuildSpeakerBar();
        _timeTotal.Text = Exporters.FmtTime(p.DurationSec);
        _timeNow.Text = "0:00";
        var hasAudio = !string.IsNullOrEmpty(p.AudioPath) && File.Exists(p.AudioPath) && _player.Load(p.AudioPath);
        _playBtn.Enabled = hasAudio;
        _waveform.SetAudio(p.Peaks, p.DurationSec);
        ShowView(_transcript);
    }

    private void BackHome()
    {
        _player.Pause();
        if (_project is not null) _store.Save(_project);
        ShowView(_home);
        RefreshLibrary();
    }

    private void RebuildSpeakerBar()
    {
        _speakerBar.Controls.Clear();
        if (_project is null) return;
        var counts = _project.Segments.GroupBy(s => s.Speaker).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (key, info) in _project.Speakers)
        {
            var chip = new Button
            {
                Text = $"●  {info.Name}  {counts.GetValueOrDefault(key)}",
                AutoSize = true,
                ForeColor = ColorTranslator.FromHtml(info.Color),
                Margin = new Padding(0, 0, 8, 0)
            };
            Theme.StyleFlat(chip);
            chip.ForeColor = ColorTranslator.FromHtml(info.Color);
            chip.Click += (_, _) => RenameSpeaker(key);
            _speakerBar.Controls.Add(chip);
        }
        var hint = MakeLabel("Click a speaker to rename · double-click text to edit", 8.5f, FontStyle.Regular, Theme.TextDim);
        hint.Margin = new Padding(16, 8, 0, 0);
        _speakerBar.Controls.Add(hint);
    }

    private void RenameSpeaker(string key)
    {
        if (_project is null) return;
        var current = _project.Speakers[key].Name;
        var input = PromptDialog.Show(this, "Rename speaker", $"Name for {current}:", current);
        if (string.IsNullOrWhiteSpace(input) || input == current) return;
        _project.Speakers[key].Name = input.Trim();
        RebuildSpeakerBar();
        _view.InvalidateLayout();
        ScheduleSave();
    }

    private void RunSearch()
    {
        var n = _view.SetQuery(_searchBox.Text.Trim());
        _searchCount.Text = string.IsNullOrEmpty(_searchBox.Text) ? ""
            : n == 0 ? "0/0" : $"{_view.CurrentMatch + 1}/{n}";
        if (n > 0) _view.ScrollToRow(_view.Matches[0].Segment);
    }

    private void JumpMatch(int delta)
    {
        _view.MoveMatch(delta);
        if (_view.Matches.Count > 0)
        {
            _searchCount.Text = $"{_view.CurrentMatch + 1}/{_view.Matches.Count}";
            var seg = _view.Matches[_view.CurrentMatch].Segment;
            if (_project is not null && _player.IsLoaded)
                _player.PositionSec = _project.Segments[seg].Start;
        }
    }

    private void Export(string fmt)
    {
        if (_project is null) return;
        if (fmt == "project")
        {
            using var pd = new SaveFileDialog
            {
                FileName = SafeFilename(_project.Title) + ".verbatim.json",
                Filter = "Verbatim project|*.json"
            };
            if (pd.ShowDialog(this) == DialogResult.OK)
                File.WriteAllText(pd.FileName, System.Text.Json.JsonSerializer.Serialize(_project, Json.Options));
            return;
        }
        var (content, ext, filter) = fmt switch
        {
            "txt" => (Exporters.BuildTxt(_project), "txt", "Text|*.txt"),
            "srt" => (Exporters.BuildSrt(_project), "srt", "SubRip subtitles|*.srt"),
            "vtt" => (Exporters.BuildVtt(_project), "vtt", "WebVTT subtitles|*.vtt"),
            _ => (System.Text.Json.JsonSerializer.Serialize(_project, Json.Options), "json", "JSON|*.json")
        };
        using var dlg = new SaveFileDialog { FileName = $"{SafeFilename(_project.Title)}.{ext}", Filter = filter };
        if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dlg.FileName, content);
    }

    private static string SafeFilename(string name)
    {
        var s = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(s.Split('.')[0], "^(CON|PRN|AUX|NUL|COM\\d|LPT\\d)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) s = "_" + s;
        return s.Length > 0 ? s : "transcript";
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new System.Windows.Forms.Timer { Interval = 800 };
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTick;
        _saveTimer.Tick += SaveTick;
        _saveTimer.Start();
    }

    private void SaveTick(object? s, EventArgs e)
    {
        _saveTimer?.Stop();
        if (_project is not null) _store.Save(_project);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_transcript.Visible) return;
        if (e.Control && e.KeyCode == Keys.F)
        {
            e.SuppressKeyPress = true;
            _searchBox.Focus();
            _searchBox.SelectAll();
        }
        else if (e.KeyCode == Keys.Space && !_searchBox.Focused && !_titleBox.Focused)
        {
            e.SuppressKeyPress = true;
            _player.Toggle();
        }
    }
}
