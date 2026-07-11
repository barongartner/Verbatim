using System.Runtime.InteropServices;

namespace Verbatim;

/// <summary>Verbatim's dark palette — same values as the 1.x UI.</summary>
public static class Theme
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#0e1116");
    public static readonly Color BgRaised = ColorTranslator.FromHtml("#151a22");
    public static readonly Color BgHover = ColorTranslator.FromHtml("#1c232e");
    public static readonly Color Border = ColorTranslator.FromHtml("#232b37");
    public static readonly Color Text = ColorTranslator.FromHtml("#e8ecf1");
    public static readonly Color TextDim = ColorTranslator.FromHtml("#8b95a5");
    public static readonly Color Accent = ColorTranslator.FromHtml("#4cc2ff");
    public static readonly Color AccentDeep = ColorTranslator.FromHtml("#1e8fd0");
    public static readonly Color Danger = ColorTranslator.FromHtml("#ff6b6b");
    public static readonly Color Mark = ColorTranslator.FromHtml("#ffd54d");
    public static readonly Color MarkCurrent = ColorTranslator.FromHtml("#ff9d2e");
    public static readonly Color ActiveRow = ColorTranslator.FromHtml("#14222e");
    public static readonly Color WaveDim = ColorTranslator.FromHtml("#2a3442");

    public static Font Base(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark window title bar (Windows 10 1809+).</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        int on = 1;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));
    }

    public static void StyleFlat(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = primary ? AccentDeep : Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Accent : BgHover;
        b.BackColor = primary ? AccentDeep : Color.Transparent;
        b.ForeColor = primary ? Color.White : Text;
        b.Font = Base(9.5f, primary ? FontStyle.Bold : FontStyle.Regular);
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
    }

    public static void StyleInput(TextBox t)
    {
        t.BackColor = BgRaised;
        t.ForeColor = Text;
        t.BorderStyle = BorderStyle.FixedSingle;
        t.Font = Base(10f);
    }
}
