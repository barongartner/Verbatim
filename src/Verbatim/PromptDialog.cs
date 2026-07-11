namespace Verbatim;

/// <summary>Minimal dark-themed single-field input dialog.</summary>
public static class PromptDialog
{
    public static string? Show(IWin32Window owner, string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = Theme.Base(),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 120),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(14, 12), ForeColor = Theme.TextDim };
        var box = new TextBox { Text = initial };
        Theme.StyleInput(box);
        box.SetBounds(14, 36, 330, 28);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(264, 76), Size = new Size(80, 30) };
        Theme.StyleFlat(ok, primary: true);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(176, 76), Size = new Size(80, 30) };
        Theme.StyleFlat(cancel);
        form.Controls.AddRange([lbl, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { Theme.ApplyDarkTitleBar(form); box.Focus(); box.SelectAll(); };
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
