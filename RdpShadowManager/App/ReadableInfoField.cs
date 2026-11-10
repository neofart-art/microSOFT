namespace RdpShadowManager;

/// <summary>A selectable value participating in preferred-size layout, including wrapped lines.</summary>
internal sealed class ReadableInfoField : TextBox
{
    private bool fitting;
    public ReadableInfoField()
    {
        ReadOnly = true; Multiline = true; WordWrap = true;
        ShortcutsEnabled = true; ScrollBars = ScrollBars.None;
        AutoSize = false; Dock = DockStyle.Top;
        BackColor = SystemColors.Window; MinimumSize = new(40, 24);
    }
    private int MeasureHeight(int width)
    {
        int inset = Math.Max(12, (int)Math.Ceiling(12 * DeviceDpi / 96d));
        var size = TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font,
            new Size(Math.Max(1, width - inset), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        return Math.Max(Font.Height + inset, size.Height + inset);
    }
    internal int RequiredHeight() => MeasureHeight(Width);
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 1 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Width;
        return new(Math.Max(40, width), MeasureHeight(Math.Max(40, width)));
    }
    // TableLayoutPanel must not shrink the native edit below the height of its actual content.
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        => base.SetBoundsCore(x, y, width, Math.Max(height, MeasureHeight(width)), specified);
    private void FitText()
    {
        if (fitting || IsDisposed || Width <= 0) return;
        fitting = true;
        try { Height = RequiredHeight(); Parent?.PerformLayout(); }
        finally { fitting = false; }
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitText(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitText(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitText(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitText(); }
}
