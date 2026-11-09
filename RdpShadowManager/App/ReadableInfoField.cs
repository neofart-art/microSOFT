namespace RdpShadowManager;

/// <summary>Selectable text whose layout always reserves enough space for its complete value.</summary>
internal sealed class ReadableInfoField : TextBox
{
    public ReadableInfoField()
    {
        ReadOnly = true; Multiline = true; WordWrap = true;
        ShortcutsEnabled = true; ScrollBars = ScrollBars.None;
        AutoSize = false; Dock = DockStyle.Top;
        BackColor = SystemColors.Window; MinimumSize = new(40, 24);
    }
    internal int RequiredHeight()
    {
        int inset = Math.Max(8, (int)Math.Ceiling(8 * DeviceDpi / 96d));
        var size = TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font,
            new Size(Math.Max(1, ClientSize.Width - inset), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        return Math.Max(Font.Height + inset, size.Height + inset);
    }
    private void FitText()
    {
        if (Width <= 0 || IsDisposed) return;
        int needed = RequiredHeight();
        if (Height != needed) { Height = needed; Parent?.PerformLayout(); }
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitText(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitText(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitText(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitText(); }
}
