using System.Text.RegularExpressions;
using Verbatim.Core;

namespace Verbatim.Controls;

/// <summary>
/// Owner-drawn transcript list: timestamp column (click to seek), colored
/// speaker names, word-wrapped text with search-match highlighting, an
/// active-row indicator that follows playback, and double-click inline
/// editing. Rows are laid out manually so highlights can be drawn on exact
/// substrings across wrapped lines.
/// </summary>
public sealed class TranscriptView : UserControl
{
    private const int TimeColWidth = 64;
    private const int SpeakerColWidth = 110;
    private const int RowPadV = 8;
    private const int RowGap = 2;
    private const int SidePad = 16;

    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, Width = 14 };
    private readonly Font _timeFont = Theme.Base(8.5f);
    private readonly Font _speakerFont = Theme.Base(9.5f, FontStyle.Bold);
    private readonly Font _textFont = Theme.Base(10.5f);
    private TextBox? _editor;

    private Project? _project;
    private string _query = "";
    private int _activeRow = -1;
    private int _currentMatch = -1; // index into Matches

    public record Match(int Segment, int Start, int Length);
    public List<Match> Matches { get; private set; } = new();

    public event Action<int>? TimeClicked;
    public event Action? Edited;

    // per-row layout cache
    private sealed class RowLayout
    {
        public int Top;
        public int Height;
        public List<(string Text, int CharOffset)> Lines = new();
    }

    private readonly List<RowLayout> _rows = new();
    private int _layoutWidth = -1;
    private int _contentHeight;

    public TranscriptView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => Invalidate();
        MouseWheel += (_, e) => ScrollBy(-e.Delta / 2);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Project? Project
    {
        get => _project;
        set { _project = value; _activeRow = -1; InvalidateLayout(); }
    }

    /// <summary>Sets the search query; returns the number of matches.</summary>
    public int SetQuery(string query)
    {
        _query = query ?? "";
        RecomputeMatches();
        _currentMatch = Matches.Count > 0 ? 0 : -1;
        Invalidate();
        return Matches.Count;
    }

    public int CurrentMatch => _currentMatch;

    public void MoveMatch(int delta)
    {
        if (Matches.Count == 0) return;
        _currentMatch = ((_currentMatch + delta) % Matches.Count + Matches.Count) % Matches.Count;
        ScrollToRow(Matches[_currentMatch].Segment);
        Invalidate();
    }

    public void RecomputeMatches()
    {
        Matches = new List<Match>();
        if (_project is null || string.IsNullOrEmpty(_query)) return;
        var re = new Regex(Regex.Escape(_query), RegexOptions.IgnoreCase);
        for (int i = 0; i < _project.Segments.Count; i++)
        {
            foreach (System.Text.RegularExpressions.Match m in re.Matches(_project.Segments[i].Text))
                Matches.Add(new Match(i, m.Index, m.Length));
        }
    }

    /// <summary>Highlights the row containing time t; returns true if it changed.</summary>
    public bool SetActiveTime(double t)
    {
        int idx = -1;
        if (_project is not null)
        {
            for (int i = 0; i < _project.Segments.Count; i++)
            {
                var s = _project.Segments[i];
                if (t >= s.Start - 0.05 && t < s.End + 0.2) { idx = i; break; }
                if (s.Start > t) break;
            }
        }
        if (idx == _activeRow) return false;
        _activeRow = idx;
        Invalidate();
        return true;
    }

    public int ActiveRow => _activeRow;

    public void ScrollToRow(int index)
    {
        EnsureLayout();
        if (index < 0 || index >= _rows.Count) return;
        var target = _rows[index].Top + _rows[index].Height / 2 - Height / 2;
        _scroll.Value = Math.Clamp(target, 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
        Invalidate();
    }

    public void InvalidateLayout()
    {
        _layoutWidth = -1;
        CommitEditor();
        Invalidate();
    }

    private void ScrollBy(int delta)
    {
        _scroll.Value = Math.Clamp(_scroll.Value + delta, 0,
            Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
    }

    // ------------------------------------------------------------ layout --
    private int TextColWidth => Math.Max(80,
        ClientSize.Width - _scroll.Width - TimeColWidth - SpeakerColWidth - SidePad * 2 - 16);

    private void EnsureLayout()
    {
        int w = TextColWidth;
        if (_layoutWidth == w && _rows.Count == (_project?.Segments.Count ?? 0)) return;
        _layoutWidth = w;
        _rows.Clear();
        _contentHeight = 8;
        if (_project is null) { UpdateScroll(); return; }

        int lineH = TextRenderer.MeasureText("Ag", _textFont).Height;
        foreach (var seg in _project.Segments)
        {
            var row = new RowLayout { Top = _contentHeight };
            row.Lines = WrapText(seg.Text, w);
            row.Height = Math.Max(lineH, row.Lines.Count * lineH) + RowPadV * 2;
            _contentHeight += row.Height + RowGap;
            _rows.Add(row);
        }
        _contentHeight += 40;
        UpdateScroll();
    }

    private List<(string, int)> WrapText(string text, int width)
    {
        var lines = new List<(string, int)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int lineStart = pos;
            int lastBreak = -1;
            int i = pos;
            while (i < text.Length)
            {
                if (text[i] == ' ') lastBreak = i;
                int len = i - lineStart + 1;
                var slice = text.Substring(lineStart, len);
                if (TextRenderer.MeasureText(slice, _textFont, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding).Width > width && len > 1)
                {
                    break;
                }
                i++;
            }
            if (i >= text.Length)
            {
                lines.Add((text[lineStart..], lineStart));
                break;
            }
            int end = lastBreak > lineStart ? lastBreak : i;
            lines.Add((text[lineStart..end], lineStart));
            pos = end;
            while (pos < text.Length && text[pos] == ' ') pos++;
        }
        if (lines.Count == 0) lines.Add(("", 0));
        return lines;
    }

    private void UpdateScroll()
    {
        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, _contentHeight);
        _scroll.LargeChange = Math.Max(1, Height);
        _scroll.SmallChange = 40;
        if (_scroll.Value > Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1))
            _scroll.Value = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        InvalidateLayout();
    }

    // ------------------------------------------------------------- paint --
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_project is null) return;
        EnsureLayout();
        var g = e.Graphics;
        int offset = _scroll.Value;
        int lineH = TextRenderer.MeasureText("Ag", _textFont).Height;

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            int top = row.Top - offset;
            if (top + row.Height < 0) continue;
            if (top > Height) break;

            var seg = _project.Segments[i];
            var rowRect = new Rectangle(SidePad / 2, top, ClientSize.Width - _scroll.Width - SidePad, row.Height);

            if (i == _activeRow)
            {
                using var b = new SolidBrush(Theme.ActiveRow);
                g.FillRectangle(b, rowRect);
                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, rowRect.Left, rowRect.Top, 2, rowRect.Height);
            }

            // time (click target)
            TextRenderer.DrawText(g, Exporters.FmtTime(seg.Start), _timeFont,
                new Rectangle(SidePad, top + RowPadV, TimeColWidth - 8, lineH),
                Theme.TextDim, TextFormatFlags.Right | TextFormatFlags.NoPadding);

            // speaker
            var color = _project.Speakers.TryGetValue(seg.Speaker, out var sp)
                ? ColorTranslator.FromHtml(sp.Color) : Theme.TextDim;
            TextRenderer.DrawText(g, _project.SpeakerName(seg.Speaker), _speakerFont,
                new Rectangle(SidePad + TimeColWidth, top + RowPadV, SpeakerColWidth - 6, lineH),
                color, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // text with match highlighting
            int textX = SidePad + TimeColWidth + SpeakerColWidth;
            for (int li = 0; li < row.Lines.Count; li++)
            {
                var (lineText, charOffset) = row.Lines[li];
                int y = top + RowPadV + li * lineH;
                DrawHighlights(g, i, lineText, charOffset, textX, y, lineH);
                TextRenderer.DrawText(g, lineText, _textFont, new Point(textX, y),
                    Theme.Text, TextFormatFlags.NoPadding);
            }
        }
    }

    private void DrawHighlights(Graphics g, int segIndex, string lineText, int charOffset, int x, int y, int lineH)
    {
        if (Matches.Count == 0) return;
        for (int mi = 0; mi < Matches.Count; mi++)
        {
            var m = Matches[mi];
            if (m.Segment != segIndex) continue;
            int lineEnd = charOffset + lineText.Length;
            int hs = Math.Max(m.Start, charOffset);
            int he = Math.Min(m.Start + m.Length, lineEnd);
            if (hs >= he) continue;

            int prefixW = hs == charOffset ? 0 : TextRenderer.MeasureText(
                lineText[..(hs - charOffset)], _textFont,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            int matchW = TextRenderer.MeasureText(
                lineText[(hs - charOffset)..(he - charOffset)], _textFont,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

            using var b = new SolidBrush(mi == _currentMatch ? Theme.MarkCurrent : Theme.Mark);
            g.FillRectangle(b, x + prefixW, y, matchW, lineH);
        }
    }

    // ------------------------------------------------------------- input --
    private int RowAt(int clientY)
    {
        EnsureLayout();
        int y = clientY + _scroll.Value;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (y >= _rows[i].Top && y < _rows[i].Top + _rows[i].Height) return i;
        }
        return -1;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        CommitEditor();
        int i = RowAt(e.Y);
        if (i < 0) return;
        if (e.X < SidePad + TimeColWidth) TimeClicked?.Invoke(i);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = RowAt(e.Y);
        if (i < 0 || _project is null) return;
        if (e.X < SidePad + TimeColWidth + SpeakerColWidth) return;
        BeginEdit(i);
    }

    private void BeginEdit(int index)
    {
        CommitEditor();
        EnsureLayout();
        var row = _rows[index];
        var seg = _project!.Segments[index];
        _editor = new TextBox
        {
            Multiline = true,
            Text = seg.Text,
            Font = _textFont,
            BackColor = Theme.BgRaised,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(SidePad + TimeColWidth + SpeakerColWidth - 2,
                row.Top - _scroll.Value + RowPadV - 2, TextColWidth + 4, row.Height - RowPadV),
            Tag = index
        };
        _editor.KeyDown += (_, e2) =>
        {
            if (e2.KeyCode == Keys.Enter && !e2.Shift) { e2.SuppressKeyPress = true; CommitEditor(); }
            if (e2.KeyCode == Keys.Escape) { e2.SuppressKeyPress = true; _editor!.Text = seg.Text; CommitEditor(); }
        };
        _editor.LostFocus += (_, _) => CommitEditor();
        Controls.Add(_editor);
        _editor.Focus();
        _editor.SelectAll();
    }

    public void CommitEditor()
    {
        if (_editor is null || _project is null) return;
        var editor = _editor;
        _editor = null; // guard against reentry via LostFocus
        int index = (int)editor.Tag!;
        var v = Regex.Replace(editor.Text, @"\s+", " ").Trim();
        Controls.Remove(editor);
        editor.Dispose();
        if (v.Length > 0 && v != _project.Segments[index].Text)
        {
            _project.Segments[index].Text = v;
            RecomputeMatches();
            InvalidateLayout();
            Edited?.Invoke();
        }
    }
}
